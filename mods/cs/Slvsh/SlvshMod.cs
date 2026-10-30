using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json.Nodes;
using Jibbers.Sdk;
using Jibbers.Sdk.Game.Event;
using Jibbers.Sdk.Interop;
using Jibbers.Sdk.Multiplayer;
using Jibbers.Sdk.UI;
using Slvsh.Integration;

namespace Slvsh;

/// <summary>
/// SLVSH with your party: one player starts a game, party members with the mod join, and players
/// take turns setting tricks for the others to match. While someone takes their turn, everyone
/// else's camera watches them; afterwards everyone else votes on whether it counted. The player who
/// starts is the game's host and runs the rules; each player judges their own attempt from the
/// game's trick events, which the vote can overrule.
/// </summary>
public sealed class SlvshMod : JibbersMod
{
    public override string Id => "slvsh";
    public override string Name => "SLVSH";
    public override string Author => "Jibbers";
    public override string Description => "Play SLVSH with your party: set a trick, everyone else has to match it, miss and you get a letter. Watch every turn and vote on whether it counted.";
    /// <summary>The original native SLVSH mod uses the same keys and game.</summary>
    public override IReadOnlyList<string> Conflicts => new[] { "jibbers_slvsh" };

    // Starting and leaving need Ctrl held; the votes are plain Y / N but only while a vote is open.
    [KeyBinding("Start, join or begin a game (with Ctrl)", Key.Enter, Key = "start_key", Section = "Keys")]
    public Key GoKey { get; private set; }

    [KeyBinding("Leave or end the game (with Ctrl)", Key.Q, Key = "quit_key", Section = "Keys")]
    public Key LeaveKey { get; private set; }

    [KeyBinding("Vote: it counted", Key.Y, Key = "yes_key", Section = "Keys", Tooltip = "Only while a vote is open.")]
    public Key VoteYesKey { get; private set; }

    [KeyBinding("Vote: it didn't count", Key.N, Key = "no_key", Section = "Keys", Tooltip = "Only while a vote is open.")]
    public Key VoteNoKey { get; private set; }

    // Ctrl, like the other SLVSH keys, and not T: T opens the game's chat. A new setting key, so a saved T doesn't carry over.
    [KeyBinding("Call your set (radial menu, with Ctrl)", Key.G, Key = "call_key_ctrl", Section = "Keys", Tooltip = "On your set, opens the wheel to call the line you'll land. Pick with 1-9 (or the right stick and A), Backspace (or B) goes back. A controller opens it with a right-stick click.")]
    public Key CallKey { get; private set; }

    [Slider("Time for each try (seconds)", 45, 15, 180, 5, Key = "attempt_secs", Section = "Rules")]
    public double AttemptSeconds { get; private set; }

    [Toggle("Vote on every try (host decides)", Key = "voting", Section = "Rules", Tooltip = "After each try everyone else votes on whether it counted; ties and silence go to trick detection.")]
    public bool Voting { get; private set; }

    [Slider("Time to vote (seconds)", 12, 5, 30, 1, Key = "vote_secs", Section = "Rules")]
    public double VoteSeconds { get; private set; }

    [Toggle("Start every try on the setter's line (host decides)", false, Key = "same_line", Section = "Rules", Tooltip = "When it's your turn to match, you start where the setter's run-in began, at their speed.")]
    public bool SameLine { get; private set; }

    [Toggle("Revenge (host decides)", Key = "revenge", Section = "Rules", Tooltip = "When a setter misses their own set, the next player gets one try at it. Land it and the setter gets a letter.")]
    public bool RevengeRule { get; private set; }

    [Slider("Run-in before the trick (seconds)", 6, 2, 15, 1, Key = "run_in", Section = "Rules", Tooltip = "How far back from the setter's trick the line starts.")]
    public double RunIn { get; private set; }


    [Toggle("Watch each player's turn", Key = "spectate", Section = "Camera", Tooltip = "Your camera follows whoever is taking their turn (when they're on your server), then comes back for yours.")]
    public bool Spectate { get; private set; }

    static readonly Stopwatch clock = Stopwatch.StartNew();
    static double Now => clock.Elapsed.TotalSeconds;
    const double PositionEvery = 0.2, CameraEvery = 0.2;

    readonly TrickLog tricks = new();
    readonly SpectateCamera camera;
    readonly SpawnTeleport spawns;

    readonly LineStart line;
    /// <summary>The setters' lines (setter → the trick and where its run-in began).</summary>
    readonly Dictionary<ulong, (string Trick, double[] State)> lines = new();
    uint lineFor;
    double lineAppliedAt;

    public SlvshMod()
    {
        camera = new SpectateCamera(text => Log(text));
        spawns = new SpawnTeleport(text => Log(text));
        line = new LineStart(() => Now, text => Log(text));
    }
    readonly ConcurrentQueue<(PartyMember From, JsonNode? Message)> inbox = new();
    readonly ConcurrentQueue<ulong> departed = new();
    PartyChannel? channel;
    OverlayPanel? overlay;
    Thread? thread;
    volatile bool stopping, pressedGo, pressedLeave, pressedYes, pressedNo, pressedCall, pressedBack, pressedSolo;
    /// <summary>Your Steam ID, or a stand-in when practicing alone outside a party.</summary>
    ulong myId;
    volatile int pressedSlice = -1;
    readonly ConcurrentQueue<char> typed = new();
    OverlayPanel? callPanel;

    public override void OnLoad()
    {
        channel = Party.Channel(this);
        channel.Received += message => inbox.Enqueue((message.From, message.Json()));
        line.Start();
        Party.MemberChanged += OnMemberChanged;
        overlay = Overlay.Register("slvsh", new OverlayOptions { Anchor = HudAnchor.TopRight, Offset = new Vector2(-12, -12) });
        TrickPerformed.Subscribe(t => tricks.AddTrick(Now, t.Name ?? "", t.SequenceId));
        Respawn.Subscribe(_ => tricks.AddReset(Now));
        // Each respawn at your own spawn: save where the game put you, as where your turns start.
        RagdollResetFinished.Subscribe(_ =>
        {
            if (spawnMoved) return; // at the setter's spawn for a turn: not yours
            try { line.SpawnedAt(spawns.Actual()); }
            catch (Exception e) when (e is InvalidOperationException or MissingMethodException or Il2CppException) { line.SpawnedAt(null); }
        });
        Reset.Subscribe(_ => tricks.AddReset(Now));
        // Ctrl+Shift+start (not in the menus or README): practice alone, no party needed.
        Input.OnPressed(() => GoKey, () => { if (Input.IsDown(Key.Shift)) pressedSolo = true; else pressedGo = true; }, Key.Control);
        Input.OnPressed(() => LeaveKey, () => pressedLeave = true, Key.Control);
        Input.OnPressed(() => game?.Phase == Phase.Vote ? VoteYesKey : 0, () => pressedYes = true);
        Input.OnPressed(() => game?.Phase == Phase.Vote ? VoteNoKey : 0, () => pressedNo = true);
        Input.OnPressed(() => CanCall && menu is not { Typing: true } ? CallKey : 0, () => pressedCall = true, Key.Control); // not while typing a trick
        Input.OnPressed(() => menu is not null ? Key.Backspace : 0, () => pressedBack = true);
        for (var i = 0; i < 10; i++)
        {
            var slice = i;
            Key Digit(Key first) => menu is { Typing: false } ? (Key)((int)first + (slice + 1) % 10) : 0;
            Input.OnPressed(() => Digit(Key.D0), () => pressedSlice = slice);
            Input.OnPressed(() => Digit(Key.Num0), () => pressedSlice = slice);
        }
        // Typing a trick: letters, digits, space and dashes go to the text; Enter adds it.
        foreach (var key in Enumerable.Range((int)Key.A, 26).Concat(Enumerable.Range((int)Key.D0, 10)).Concat(Enumerable.Range((int)Key.Num0, 10)).Append((int)Key.Space).Append((int)Key.Minus))
        {
            var k = (Key)key;
            var c = k switch { >= Key.A and <= Key.Z => (char)('a' + (k - Key.A)), >= Key.D0 and <= Key.D9 => (char)('0' + (k - Key.D0)), >= Key.Num0 and <= Key.Num9 => (char)('0' + (k - Key.Num0)), Key.Space => ' ', _ => '-' };
            Input.OnPressed(() => menu is { Typing: true } ? k : 0, () => typed.Enqueue(c));
        }
        Input.OnPressed(() => menu is { Typing: true } ? Key.Enter : 0, () => typed.Enqueue('\n'));
        callPanel = Overlay.Register("slvsh-call", new OverlayOptions { Anchor = HudAnchor.Center });
        // Every frame: fly the camera behind whoever you're watching.
        Jibbers.Sdk.Game.Env.Manager.UpdateMethod.Before((_, _) =>
        {
            if (!stopping) camera.Frame();
        });
        thread = new Thread(Run) { IsBackground = true, Name = "SLVSH" };
        thread.Start();
    }

    public override void OnUnload()
    {
        stopping = true;
        line.Recording = false;
        Party.MemberChanged -= OnMemberChanged;
        thread?.Join(500);
        try { MainThread.Run(spawns.Restore); }
        catch (Exception e) { Log($"SLVSH: couldn't put your spawn point back: {e.Message}"); }
        try { MainThread.Run(camera.Restore); }
        catch (Exception e) { Log($"SLVSH: couldn't give the camera back: {e.Message}"); }
    }

    void OnMemberChanged(PartyMember member, bool joined)
    {
        if (!joined) departed.Enqueue(member.Id.Value);
    }

    // ---- the game thread: messages, keys, rules, judging, the camera and the overlay ----

    SlvshHost? host;
    volatile GameState? game;
    double gameSeen, lastStateSent = -10, lastInvite = -10, hintUntil, lastPosition, lastCamera;
    (uint Id, ulong From, string Name, int Players, double At)? invite;
    (uint Id, double At)? joining;
    (uint Attempt, TrickJudge Judge)? judge;
    uint sentResult;
    /// <summary>The game the per-turn memory (sentResult, placedFor, judge) belongs to.</summary>
    uint turnsOf;
    string hint = "";
    SpectateStatus watching;
    readonly Dictionary<ulong, (Vector3 At, double When)> reported = new();
    SlvshView? shown;

    void Run()
    {
        while (!stopping)
        {
            Thread.Sleep(50);
            try { Step(); }
            catch (Exception e) { Log($"SLVSH: {e}"); Thread.Sleep(1000); }
        }
        overlay?.Hide();
        callPanel?.Hide();
    }

    void Step()
    {
        var now = Now;
        var me = Party.Local?.Id.Value ?? (host is { Solo: true } ? myId : 0);
        if (pressedSolo)
        {
            pressedSolo = false;
            // Free to practice: no game, a finished one, or your own lobby nobody has joined yet.
            var free = host is null ? game is null || game.Phase == Phase.Over
                : host.Game.Phase == Phase.Over || host.Game is { Phase: Phase.Lobby, Players.Count: 1 } || host.Solo;
            if (!Enabled || !free) Log($"SLVSH: can't practice alone now ({(Enabled ? "you're in a game: Ctrl+Q leaves it first" : "the mod is off")})");
            else
            {
                me = myId = me != 0 ? me : 1;
                host = new SlvshHost(me, Party.Local?.Name ?? "You", (int)Math.Clamp(AttemptSeconds, 15, 180), Voting ? (int)Math.Clamp(VoteSeconds, 5, 30) : 0, () => Now, SameLine, (int)Math.Clamp(RunIn, 2, 15), solo: true);
                host.Start();
                game = host.Game;
                lines.Clear();
                Log("SLVSH: practicing alone");
            }
        }
        myId = me;
        if ((Party.State != PartyState.InParty && host is not { Solo: true }) || !Enabled)
        {
            if (game is not null) Hint(Party.State != PartyState.InParty ? "You left the party, so the game ended" : "");
            host = null;
            game = null;
            invite = null;
            joining = null;
            judge = null;
            while (inbox.TryDequeue(out _)) { }
            if (pressedGo && Enabled) Hint($"Make or join a party first: press {Party.PanelKey} for the Party panel");
            pressedGo = pressedLeave = pressedYes = pressedNo = false;
            Camera(me, now);
            Show(me);
            return;
        }

        while (departed.TryDequeue(out var gone))
        {
            host?.Remove(gone);
            reported.Remove(gone);
            lines.Remove(gone);
            if (host is null && game?.Host == gone)
            {
                game = null;
                Hint("The host left, so the game ended");
            }
        }
        while (inbox.TryDequeue(out var item)) Handle(item.From, item.Message, me, now);
        Keys(me, now);

        if (host is not null)
        {
            if (host.Game.Phase == Phase.Lobby && now - lastInvite > 2)
            {
                lastInvite = now;
                channel!.Broadcast(new JsonObject { ["t"] = "invite", ["id"] = host.Game.Id, ["players"] = host.Game.Players.Count }, Delivery.Unreliable);
            }
            host.Tick();
            if (host.Changed || now - lastStateSent > 1)
            {
                var reliable = host.Changed;
                host.Changed = false;
                lastStateSent = now;
                var state = host.Game.ToJson();
                foreach (var p in host.Game.Players.Where(p => p.Id != me)) channel!.Send(new SteamId(p.Id), state, reliable ? Delivery.Reliable : Delivery.Unreliable);
            }
            game = host.Game;
            gameSeen = now;
            if (host.OverAt is { } over && now - over > 25)
            {
                host = null;
                game = null;
            }
        }
        else if (game is not null && now - gameSeen > (game.Phase == Phase.Over ? 25 : 12))
        {
            game = null;
            Hint("Lost touch with the game's host");
        }
        if (invite is { } i && now - i.At > 8) invite = null;
        if (joining is { } j && now - j.At > 10)
        {
            joining = null;
            Hint("Couldn't join that game (it may have started)");
        }

        Calling(me);
        Spawns(me);
        Judge(me, now);
        Lines(me, now);
        ReportPosition(me, now);
        Camera(me, now);
        Show(me);
    }

    void Handle(PartyMember from, JsonNode? m, ulong me, double now)
    {
        var sender = from.Id.Value;
        try
        {
            var id = m?["id"]?.GetValue<uint>() ?? 0;
            switch (m?["t"]?.GetValue<string>())
            {
                case "invite" when game is null && host is null && joining is null && sender != me:
                    invite = (id, sender, from.Name, Math.Clamp(m["players"]?.GetValue<int>() ?? 1, 1, SlvshHost.MaxPlayers), now);
                    break;
                case "join" when host?.Game.Id == id:
                    host.Join(sender, from.Name);
                    break;
                case "leave" when host?.Game.Id == id:
                    host.Remove(sender);
                    break;
                case "call" when host?.Game.Id == id:
                    host.Called(sender, m["attempt"]?.GetValue<uint>() ?? 0, CalledLine.FromJson(m["line"]), SpawnSpot.FromJson(m["spawn"]));
                    break;
                case "result" when host?.Game.Id == id:
                    var trick = m["trick"]?.GetValue<string>() ?? "";
                    if (trick.Length > GameState.MaxTried) trick = "";
                    host.Result(sender, m["attempt"]?.GetValue<uint>() ?? 0, new Outcome(m["landed"]?.GetValue<bool>() == true && trick.Length > 0, trick));
                    break;
                case "vote" when host?.Game.Id == id:
                    host.Vote(sender, m["attempt"]?.GetValue<uint>() ?? 0, m["yes"]?.GetValue<bool>() == true);
                    break;
                case "pos" when game?.Id == id && game.Watching(sender):
                    if (m["p"] is JsonArray { Count: 3 } p)
                    {
                        var at = new Vector3(p[0]!.GetValue<float>(), p[1]!.GetValue<float>(), p[2]!.GetValue<float>());
                        if (float.IsFinite(at.X) && float.IsFinite(at.Y) && float.IsFinite(at.Z)) reported[sender] = (at, now);
                    }
                    break;
                case "line" when game?.Id == id && sender != me && game.SameLine:
                    var lineTrick = m["trick"]?.GetValue<string>() ?? "";
                    if (lineTrick.Length is > 0 and <= GameState.MaxTrick && m["s"]?.GetValue<string>() is { Length: <= 65536 } packed && LineStart.Unpack(packed) is { } state)
                        lines[sender] = (lineTrick, state);
                    break;
                case "state" when host is null:
                    if (GameState.FromJson(m) is not { } s || s.Host != sender) return;
                    if (game?.Id != s.Id && joining?.Id != s.Id) return;
                    if (s.Find(me) is not null)
                    {
                        game = s;
                        gameSeen = now;
                        invite = null;
                        joining = null;
                    }
                    else if (game?.Id == s.Id)
                    {
                        game = null;
                        Hint("You were removed from the game");
                    }
                    break;
            }
        }
        catch (Exception e) when (e is InvalidOperationException or FormatException)
        {
            // A malformed message from another PC: ignore it.
        }
    }

    void Keys(ulong me, double now)
    {
        if (pressedYes || pressedNo)
        {
            var yes = pressedYes;
            pressedYes = pressedNo = false;
            if (game is { Phase: Phase.Vote } g && g.Voters.Contains(me))
            {
                pressedGo = false; // the vote key may share the start key
                if (host is not null) host.Vote(me, g.Attempt, yes);
                else channel!.Send(new SteamId(g.Host), new JsonObject { ["t"] = "vote", ["id"] = g.Id, ["attempt"] = g.Attempt, ["yes"] = yes });
            }
        }
        if (pressedGo)
        {
            pressedGo = false;
            if (host is { Game.Phase: Phase.Lobby })
            {
                if (!host.Start()) Hint("You need at least 2 players: wait for someone to join");
            }
            else if (game is { Phase: Phase.Over } || (host is null && game is null && invite is null && joining is null))
            {
                // Start a new game (or the next one, once this one is over).
                host = new SlvshHost(me, Party.Local?.Name ?? "You", (int)Math.Clamp(AttemptSeconds, 15, 180), Voting ? (int)Math.Clamp(VoteSeconds, 5, 30) : 0, () => Now,
                    SameLine, (int)Math.Clamp(RunIn, 2, 15), revenge: RevengeRule);
                lines.Clear();
                game = host.Game;
                lastInvite = -10;
                if (channel!.Peers.Count == 0) Hint("Nobody else in your party has SLVSH yet: they need the mod to join");
                Log("SLVSH: started a game");
            }
            else if (invite is { } i)
            {
                channel!.Send(new SteamId(i.From), new JsonObject { ["t"] = "join", ["id"] = i.Id });
                joining = (i.Id, now);
                invite = null;
            }
        }
        if (pressedLeave)
        {
            pressedLeave = false;
            if (host is not null)
            {
                var end = host.Game with { Phase = Phase.Over, Attempter = 0, News = $"{Party.Local?.Name ?? "The host"} ended the game" };
                foreach (var p in end.Players.Where(p => p.Id != me)) channel!.Send(new SteamId(p.Id), end.ToJson());
                Log("SLVSH: ended the game");
            }
            else if (game is not null && game.Phase != Phase.Over) channel!.Send(new SteamId(game.Host), new JsonObject { ["t"] = "leave", ["id"] = game.Id });
            host = null;
            game = null;
            invite = null;
            joining = null;
            judge = null;
        }
    }

    /// <summary>Your attempt: judge it from the game's trick events and report it to the host.</summary>
    void Judge(ulong me, double now)
    {
        TurnsFor(game);
        if (game is not { Phase: Phase.Set or Phase.Match } g || g.Attempter != me || g.Attempt == sentResult)
        {
            if (game is null || game.Attempter != me) judge = null;
            return;
        }
        // A set counts only once it's called, and a try starts when you're let go: nothing counts before.
        if (g.Call.Count == 0)
        {
            judge = null;
            return;
        }
        if (judge is not { } current || current.Attempt != g.Attempt) judge = current = (g.Attempt, new TrickJudge(tricks, now, g.Call));
        var outcome = current.Judge.Step(now);
        // Time's nearly up and you did ride your line, but detection didn't see it: let the vote decide, rather than timing out with no vote.
        if (outcome is null && current.Judge.Begun && g.SecondsLeft <= 1 && g.SecondsTotal > 0)
            outcome = new Outcome(false, current.Judge.Current ?? "");
        if (outcome is null) return;
        Log($"SLVSH: attempt {g.Attempt}: {outcome}");
        if (g.Phase == Phase.Set && g.SameLine && outcome.Landed) ShareLine(g, me, current.Judge, g.Trick);
        sentResult = g.Attempt;
        judge = null;
        if (host is not null) host.Result(me, g.Attempt, outcome);
        else
            channel!.Send(new SteamId(g.Host), new JsonObject
            {
                ["t"] = "result", ["id"] = g.Id, ["attempt"] = g.Attempt, ["landed"] = outcome.Landed, ["trick"] = outcome.Trick,
            });
    }

    /// <summary>
    /// Your set landed: send the others where its run-in began (your rider's physics state from
    /// <see cref="GameState.RunIn"/> seconds before the trick, after your last respawn), so their tries start there.
    /// </summary>
    void ShareLine(GameState g, ulong me, TrickJudge judged, string trick)
    {
        if (judged.TrickAt is not { } at) return;
        var resets = tricks.Since(judged.Started).Resets.Where(r => r < at).ToList();
        var since = resets.Count > 0 ? resets.Max() + 0.5 : judged.Started;
        if (line.Pick(at - g.RunIn, since) is not { } state) return;
        var packed = LineStart.Pack(state);
        if (packed.Length > 60000) { Log($"SLVSH: your line is too big to send ({state.Length} values)"); return; }
        var message = new JsonObject { ["t"] = "line", ["id"] = g.Id, ["trick"] = trick, ["s"] = packed };
        foreach (var p in g.Players.Where(p => p.Id != me)) channel!.Send(new SteamId(p.Id), message);
    }

    /// <summary>
    /// Records your rider while you set; on your turn to match, starts you on the setter's line, and
    /// again if you respawn before your trick (respawning is how you get back up to the jump).
    /// </summary>
    void Lines(ulong me, double now)
    {
        var g = game;
        line.Recording = g is { SameLine: true, Phase: Phase.Set } && g.Attempter == me;
        if (g is not { SameLine: true, Phase: Phase.Match } || g.Attempter != me) return;
        if (!lines.TryGetValue(g.Setter, out var set) || set.Trick != g.Trick) return;
        if (lineFor != g.Attempt)
        {
            lineFor = g.Attempt;
            lineAppliedAt = now;
            line.Apply(set.State);
            Log($"SLVSH: starting on {g.NameOf(g.Setter)}'s line");
            return;
        }
        var respawned = tricks.Since(lineAppliedAt).Resets;
        if (respawned.Count > 0 && now - respawned[^1] > 0.4 && judge is { } j && j.Judge.Current is null)
        {
            lineAppliedAt = now;
            line.Apply(set.State);
        }
    }

    /// <summary>During your turn and its vote, tell the others where you are, so their cameras can find your rider.</summary>
    void ReportPosition(ulong me, double now)
    {
        if (game is not { } g || !g.Watching(me) || now - lastPosition < PositionEvery) return;
        lastPosition = now;
        Vector3? at;
        try { at = MainThread.Run(camera.OwnPosition); }
        catch (TimeoutException) { return; }
        if (at is not { } p) return;
        var message = new JsonObject { ["t"] = "pos", ["id"] = g.Id, ["p"] = new JsonArray(p.X, p.Y, p.Z) };
        foreach (var player in g.Players.Where(x => x.Id != me)) channel!.Send(new SteamId(player.Id), message, Delivery.Unreliable);
    }

    /// <summary>Watches whoever is taking their turn (and through their vote), and comes back for yours.</summary>
    void Camera(ulong me, double now)
    {
        var g = game;
        var target = Spectate && g is not null && g.Attempter != me && g.Watching(g.Attempter) ? g.Attempter : 0UL;
        if (target == 0 && !camera.Active)
        {
            watching = SpectateStatus.Off;
            return;
        }
        if (target != 0 && now - lastCamera < CameraEvery) return;
        lastCamera = now;
        var name = target == 0 ? null : g!.NameOf(target);
        Vector3? at = target != 0 && reported.TryGetValue(target, out var r) && now - r.When < 3 ? r.At : null;
        try { watching = MainThread.Run(() => camera.Update(target, name, at)); }
        catch (TimeoutException) { }
        catch (Exception e) when (e is InvalidOperationException or MissingMethodException or Il2CppException)
        {
            if (watching != SpectateStatus.NotHere) Log($"SLVSH: can't watch {name}: {e.Message}");
            watching = SpectateStatus.NotHere;
        }
    }

    // ---- calling your set: the radial menu ----

    CallMenu? menu;
    uint menuFor;
    int lit = -1;
    PadButton padHeld;
    OverlayFrame? menuShown;

    /// <summary>Your set, and you haven't called it yet.</summary>
    bool CanCall => game is { Phase: Phase.Set, Call.Count: 0 } g && g.Attempter == myId;

    /// <summary>Opens the wheel at the start of your set (and on the call key), drives it from the keys and a controller, and sends the line once called.</summary>
    void Calling(ulong me)
    {
        var g = game;
        var mySet = g is { Phase: Phase.Set, Call.Count: 0 } && g.Attempter == me;
        var pad = Gamepad.Read().WithDeadzone(0.25f);
        var pressed = pad.Connected ? pad.Buttons & ~padHeld : PadButton.None;
        padHeld = pad.Connected ? pad.Buttons : PadButton.None;
        var toggle = pressedCall || (pressed & PadButton.RightStick) != 0;
        pressedCall = false;
        if (!mySet)
        {
            menu = null;
            pressedBack = false;
            pressedSlice = -1;
            typed.Clear();
            DrawMenu();
            return;
        }
        if (menuFor != g!.Attempt) { menuFor = g.Attempt; menu = new CallMenu(); lit = -1; } // opens by itself when your set starts
        else if (toggle) menu = menu is null ? new CallMenu() : null;
        if (menu is { } m)
        {
            while (typed.TryDequeue(out var c))
                if (c == '\n') m.Enter();
                else m.Type(c);
            var count = m.Labels.Count;
            if (MathF.Sqrt(pad.RightX * pad.RightX + pad.RightY * pad.RightY) > 0.5f) lit = CallWheel.SliceAt(pad.RightX, pad.RightY, count);
            if (pressedSlice is >= 0 and var slice) m.Pick(slice);
            else if ((pressed & PadButton.A) != 0 && lit >= 0) m.Pick(lit);
            if (pressedBack || (pressed & PadButton.B) != 0) m.Back();
            if (m.Labels.Count != count) lit = -1;
            if (m.Cancelled) menu = null;
            else if (m.Called is { } line)
            {
                menu = null;
                Log($"SLVSH: called {CalledLine.Describe(line)}");
                // Your set starts from your spawn point (the others start there too).
                SpawnSpot? spot = null;
                try { spot = MainThread.Run(spawns.Current); }
                catch (Exception e) when (e is TimeoutException or InvalidOperationException or MissingMethodException or Il2CppException) { Log($"SLVSH: couldn't read your spawn point: {e.Message}"); }
                if (host is not null) host.Called(me, g.Attempt, line, spot);
                else channel!.Send(new SteamId(g.Host), new JsonObject { ["t"] = "call", ["id"] = g.Id, ["attempt"] = g.Attempt, ["line"] = CalledLine.ToJson(line), ["spawn"] = spot?.ToJson() });
            }
        }
        pressedBack = false;
        pressedSlice = -1;
        DrawMenu();
    }

    void DrawMenu()
    {
        var frame = menu is { } m ? CallWheel.Build(m, lit, "Backspace / B", $"Ctrl+{CallKey} / R-stick") : null;
        if (frame is null)
        {
            if (menuShown is not null) callPanel!.Hide();
            menuShown = null;
            return;
        }
        callPanel!.Draw(frame);
        if (menuShown is null) callPanel.Show();
        menuShown = frame;
    }

    uint placedFor;
    bool inGame, spawnMoved;

    /// <summary>
    /// Freezes your rider at your spawn while it isn't your live turn (others trying, votes, or you
    /// still calling your set). When your turn goes live you're put at the spawn and let go: your own
    /// for your set, the setter's when matching (your spawn point is lent to theirs for the turn, so a
    /// respawn takes you back there). When it ends you're put back and frozen again.
    /// </summary>
    void Spawns(ulong me)
    {
        TurnsFor(game);
        var g = game;
        var playing = g is { Phase: Phase.Set or Phase.Match or Phase.Vote };
        var live = g is not null && g.Attempter == me && g.Attempt != sentResult
            && (g.Phase == Phase.Match || g is { Phase: Phase.Set, Call.Count: > 0 });
        try
        {
            if (!playing)
            {
                if (inGame) { inGame = false; line.Release(); RestoreSpawn(); }
                return;
            }
            inGame = true;
            if (live && placedFor != g!.Attempt)
            {
                placedFor = g.Attempt;
                var at = g.Phase == Phase.Match ? g.Spawn : null;
                if (at is not null)
                {
                    MainThread.Run(() => spawns.MoveTo(at));
                    spawnMoved = true;
                    Log($"SLVSH: your turn: starting at {g.NameOf(g.Setter)}'s spawn");
                }
                // A line that starts switch starts you riding switch.
                var switched = g.Call.Count > 0 && g.Call[0].Stance == "switch";
                if (switched) Log("SLVSH: your line starts switch: placing you switch");
                // Let go straight away: a get-ready hold here only held up the turn.
                line.Place(at, switched);
            }
            else if (!live)
            {
                RestoreSpawn();
                line.Freeze();
            }
        }
        catch (Exception e) when (e is TimeoutException or InvalidOperationException or MissingMethodException or Il2CppException)
        {
            Log($"SLVSH: couldn't move you to the spawn: {e.Message}");
        }
    }

    /// <summary>
    /// Attempt numbers start again at 1 in every game, so what was done for attempt N belongs to one
    /// game: a new game starts it afresh. Before, a second game's first turn counted as already
    /// placed and reported, and kept you frozen at your spawn after you called your set.
    /// </summary>
    void TurnsFor(GameState? g)
    {
        if (g is null || g.Id == turnsOf) return;
        turnsOf = g.Id;
        sentResult = 0;
        placedFor = 0;
        judge = null;
    }

    void RestoreSpawn()
    {
        if (!spawnMoved) return;
        spawnMoved = false;
        MainThread.Run(spawns.Restore);
    }

    void Hint(string text)
    {
        hint = text;
        hintUntil = Now + 6;
    }

    void Show(ulong me)
    {
        var g = game;
        var view = new SlvshView
        {
            Me = me,
            Game = g,
            Invite = g is null && invite is { } i ? (i.Name, i.Players) : null,
            Trying = judge?.Judge.Current,
            Hint = Now < hintUntil ? hint : joining is not null ? "Joining the game…" : "",
            Watching = g is not null && g.Attempter != me && g.Watching(g.Attempter) ? watching : SpectateStatus.Off,
            CallKey = $"Ctrl+{CallKey}",
            GoKey = $"Ctrl+{GoKey}",
            LeaveKey = $"Ctrl+{LeaveKey}",
            YesKey = VoteYesKey.ToString(),
            NoKey = VoteNoKey.ToString(),
        };
        if (view == shown) return;
        shown = view;
        if (Scoreboard.Build(view) is { } frame)
        {
            overlay!.Draw(frame);
            overlay.Show();
        }
        else overlay!.Hide();
    }
}
