using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json.Nodes;
using Jibbers.Sdk;
using Jibbers.Sdk.Game.Event;
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

    // Every key is pressed with Ctrl held, so none of them clash with the game or other mods' F-keys.
    [KeyBinding("Start, join or begin a game (with Ctrl)", Key.Enter, Key = "start_key", Section = "Keys")]
    public Key GoKey { get; private set; }

    [KeyBinding("Leave or end the game (with Ctrl)", Key.Q, Key = "quit_key", Section = "Keys")]
    public Key LeaveKey { get; private set; }

    [KeyBinding("Vote: it counted (with Ctrl)", Key.Y, Key = "yes_key", Section = "Keys", Tooltip = "Only while a vote is open.")]
    public Key VoteYesKey { get; private set; }

    [KeyBinding("Vote: it didn't count (with Ctrl)", Key.N, Key = "no_key", Section = "Keys", Tooltip = "Only while a vote is open.")]
    public Key VoteNoKey { get; private set; }

    [Choice("A match counts when (host decides)", MatchRules.Exact, MatchRules.Exact, MatchRules.Spins, MatchRules.Any, Key = "match_rule", Section = "Rules")]
    public string Rule { get; private set; } = MatchRules.Exact;

    [Slider("Time for each try (seconds)", 45, 15, 180, 5, Key = "attempt_secs", Section = "Rules")]
    public double AttemptSeconds { get; private set; }

    [Toggle("Vote on every try (host decides)", Key = "voting", Section = "Rules", Tooltip = "After each try everyone else votes on whether it counted; ties and silence go to trick detection.")]
    public bool Voting { get; private set; }

    [Slider("Time to vote (seconds)", 12, 5, 30, 1, Key = "vote_secs", Section = "Rules")]
    public double VoteSeconds { get; private set; }

    [Toggle("Watch each player's turn", Key = "spectate", Section = "Camera", Tooltip = "Your camera follows whoever is taking their turn (when they're on your server), then comes back for yours.")]
    public bool Spectate { get; private set; }

    static readonly Stopwatch clock = Stopwatch.StartNew();
    static double Now => clock.Elapsed.TotalSeconds;
    const double PositionEvery = 0.2, CameraEvery = 0.2;

    readonly TrickLog tricks = new();
    readonly SpectateCamera camera = new();
    readonly ConcurrentQueue<(PartyMember From, JsonNode? Message)> inbox = new();
    readonly ConcurrentQueue<ulong> departed = new();
    PartyChannel? channel;
    OverlayPanel? overlay;
    Thread? thread;
    volatile bool stopping, pressedGo, pressedLeave, pressedYes, pressedNo;

    public override void OnLoad()
    {
        channel = Party.Channel(this);
        channel.Received += message => inbox.Enqueue((message.From, message.Json()));
        Party.MemberChanged += OnMemberChanged;
        overlay = Overlay.Register("slvsh", new OverlayOptions { Anchor = HudAnchor.TopRight, Offset = new Vector2(-12, -12) });
        TrickPerformed.Subscribe(t => tricks.AddTrick(Now, t.Name ?? "", t.SequenceId));
        Respawn.Subscribe(_ => tricks.AddReset(Now));
        Reset.Subscribe(_ => tricks.AddReset(Now));
        Input.OnPressed(() => GoKey, () => pressedGo = true, Key.Control);
        Input.OnPressed(() => LeaveKey, () => pressedLeave = true, Key.Control);
        Input.OnPressed(() => game?.Phase == Phase.Vote ? VoteYesKey : 0, () => pressedYes = true, Key.Control);
        Input.OnPressed(() => game?.Phase == Phase.Vote ? VoteNoKey : 0, () => pressedNo = true, Key.Control);
        thread = new Thread(Run) { IsBackground = true, Name = "SLVSH" };
        thread.Start();
    }

    public override void OnUnload()
    {
        stopping = true;
        Party.MemberChanged -= OnMemberChanged;
        thread?.Join(500);
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
    }

    void Step()
    {
        var now = Now;
        var me = Party.Local?.Id.Value ?? 0;
        if (Party.State != PartyState.InParty || !Enabled)
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

        Judge(me, now);
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
                case "result" when host?.Game.Id == id:
                    var trick = m["trick"]?.GetValue<string>() ?? "";
                    if (trick.Length > 80) trick = "";
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
                host = new SlvshHost(me, Party.Local?.Name ?? "You", Rule, (int)Math.Clamp(AttemptSeconds, 15, 180), Voting ? (int)Math.Clamp(VoteSeconds, 5, 30) : 0, () => Now);
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
        if (game is not { Phase: Phase.Set or Phase.Match } g || g.Attempter != me || g.Attempt == sentResult)
        {
            if (game is null || game.Attempter != me) judge = null;
            return;
        }
        if (judge is not { } current || current.Attempt != g.Attempt) judge = current = (g.Attempt, new TrickJudge(tricks, now));
        if (current.Judge.Step(now) is not { } outcome) return;
        Log($"SLVSH: attempt {g.Attempt}: {outcome}");
        sentResult = g.Attempt;
        judge = null;
        if (host is not null) host.Result(me, g.Attempt, outcome);
        else
            channel!.Send(new SteamId(g.Host), new JsonObject
            {
                ["t"] = "result", ["id"] = g.Id, ["attempt"] = g.Attempt, ["landed"] = outcome.Landed, ["trick"] = outcome.Trick,
            });
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
        try { watching = MainThread.Run(() => camera.Update(name, at, now)); }
        catch (TimeoutException) { }
        catch (Exception e) when (e is InvalidOperationException or MissingMethodException)
        {
            if (watching != SpectateStatus.NotHere) Log($"SLVSH: can't watch {name}: {e.Message}");
            watching = SpectateStatus.NotHere;
        }
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
            GoKey = $"Ctrl+{GoKey}",
            LeaveKey = $"Ctrl+{LeaveKey}",
            YesKey = $"Ctrl+{VoteYesKey}",
            NoKey = $"Ctrl+{VoteNoKey}",
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
