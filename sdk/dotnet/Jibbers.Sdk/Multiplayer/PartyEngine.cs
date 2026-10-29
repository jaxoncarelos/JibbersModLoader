using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace Jibbers.Sdk.Multiplayer;

/// <summary>Everything the party shows, published after every change. Immutable.</summary>
internal sealed record PartySnapshot(
    PartyState State, SteamId Lobby, string Code, bool Locked, IReadOnlyList<PartyMember> Members,
    IReadOnlyList<FriendParty> Friends, string Status)
{
    public static readonly PartySnapshot Unavailable = new(PartyState.Unavailable, SteamId.None, "", false, Array.Empty<PartyMember>(), Array.Empty<FriendParty>(), "Waiting for Steam");
    public PartyMember? Local => Members.FirstOrDefault(m => m.IsLocal);
    public PartyMember? Host => Members.FirstOrDefault(m => m.IsHost);
}

/// <summary>
/// The party's lobby and messages. One Steam lobby per player, shared by every multiplayer mod:
/// public (so a code search finds it) but only listed under its code, owned by the host. All Steam
/// matchmaking happens in <see cref="Tick"/>, on one thread; the game dispatches Steam's callbacks
/// itself and may take their results, so every step also checks Steam's state directly.
/// </summary>
internal sealed class PartyEngine
{
    public const int MaxMembers = 16;
    internal const string KeyParty = "jb_party", KeyCode = "jb_code", KeyLocked = "jb_locked", KeySize = "jb_size", KeyMods = "jb_mods";
    const string CodeAlphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789"; // no 0/O, 1/I/L
    const double MemberRefresh = 0.5, PingRefresh = 2, FriendRefresh = 6;

    enum Step { Idle, Creating, FindingMine, Searching, Joining, In }

    readonly ISteamBackend steam;
    readonly Func<double> clock;
    readonly Action<string> log;
    readonly ConcurrentQueue<Action> requests = new();
    readonly ConcurrentQueue<(SteamId From, string Mod, byte[] Payload)> loopback = new();
    readonly List<(SteamId From, byte[] Data)> inbox = new();
    readonly Dictionary<SteamId, PartyMember> members = new();
    readonly HashSet<SteamId> pendingLobbyData = new();

    Step step;
    ulong call;
    double started, nextMembers, nextPing, nextFriends, retryAt;
    int tries;
    SteamId lobby, joinTarget;
    string code = "", joinCode = "", status = "", advertisedMods = "\0";
    bool locked;
    IReadOnlyList<FriendParty> friends = Array.Empty<FriendParty>();
    IReadOnlyDictionary<string, string> localMods = new Dictionary<string, string>();
    volatile PartySnapshot snapshot;

    /// <summary>Called on the party thread for each mod message from a member (or from yourself).</summary>
    public Action<PartyMember, string, ReadOnlyMemory<byte>>? Deliver;
    /// <summary>Called on the party thread after the snapshot changed.</summary>
    public Action<PartySnapshot>? Changed;
    /// <summary>Called on the party thread when someone joins (<c>true</c>) or leaves (<c>false</c>).</summary>
    public Action<PartyMember, bool>? Membership;
    /// <summary>Short lines worth showing the player ("Ben joined the party").</summary>
    public Action<string>? Notice;

    public PartyEngine(ISteamBackend steam, Func<double> clock, Action<string> log)
    {
        this.steam = steam;
        this.clock = clock;
        this.log = log;
        snapshot = PartySnapshot.Unavailable with { State = PartyState.None, Status = "" };
    }

    public PartySnapshot Snapshot => snapshot;
    public SteamId LocalId => steam.LocalId;

    // ---- requests (any thread; run on the next tick) ----

    public void Create() => requests.Enqueue(() =>
    {
        if (step != Step.Idle) return;
        code = NewCode();
        BeginCreate();
    });

    public void JoinCode(string text) => requests.Enqueue(() =>
    {
        var wanted = NormalizeCode(text);
        if (wanted.Length != 6) { SetStatus("Party codes are 6 letters and numbers"); return; }
        if (step == Step.In && code == wanted) return;
        LeaveNow(quiet: true);
        joinCode = wanted;
        tries = 0;
        BeginSearch();
    });

    public void JoinFriend(SteamId friend) => requests.Enqueue(() =>
    {
        var target = steam.FriendLobby(friend);
        if (!target.IsValid || steam.LobbyData(target, KeyParty) != "1") { SetStatus($"{steam.NameOf(friend)} is not in a party"); return; }
        if (step == Step.In && target == lobby) return;
        LeaveNow(quiet: true);
        BeginJoin(target);
    });

    /// <summary>Joins a party by its Steam lobby, as a Steam invite or "Join game" names it.</summary>
    public void JoinLobby(SteamId target) => requests.Enqueue(() =>
    {
        if (!target.IsValid || (step == Step.In && target == lobby)) return;
        LeaveNow(quiet: true);
        BeginJoin(target);
    });

    public void Leave() => requests.Enqueue(() => LeaveNow(quiet: false));

    public void SetLocked(bool value) => requests.Enqueue(() =>
    {
        if (step != Step.In || steam.Owner(lobby) != steam.LocalId) return;
        steam.SetJoinable(lobby, !value);
        steam.SetLobbyData(lobby, KeyLocked, value ? "1" : "0");
        locked = value;
        Notice?.Invoke(value ? "Party locked: nobody new can join" : "Party unlocked");
        Publish();
    });

    public void Invite(SteamId friend) => requests.Enqueue(() =>
    {
        if (step == Step.In && steam.InviteToLobby(lobby, friend)) Notice?.Invoke($"Invited {steam.NameOf(friend)}");
    });

    public void OpenInviteOverlay() => requests.Enqueue(() => { if (step == Step.In) steam.OpenInviteOverlay(lobby); });

    /// <summary>The mods (id → version) this player advertises to the party: the ones that opened a channel.</summary>
    public void SetLocalMods(IReadOnlyDictionary<string, string> mods) => requests.Enqueue(() => { localMods = mods; AdvertiseMods(); Publish(); });

    /// <summary>Sends one framed message; to yourself it loops back on the next tick. Any thread.</summary>
    public bool Send(SteamId to, string mod, byte[] payload, bool reliable)
    {
        var snap = snapshot;
        if (snap.State != PartyState.InParty || !snap.Members.Any(m => m.Id == to)) return false;
        if (to == steam.LocalId) { loopback.Enqueue((to, mod, payload)); return true; }
        return steam.Send(to, Wire.Frame(Wire.KindMod, mod, payload), reliable);
    }

    // ---- the party thread ----

    public void Tick()
    {
        while (requests.TryDequeue(out var request)) request();
        var now = clock();
        switch (step)
        {
            case Step.Creating:
                switch (steam.LobbyCreated(call, out var made))
                {
                    case CallStatus.Done: Entered(made, created: true); break;
                    case CallStatus.Pending when now - started < 3: break;
                    default: BeginFindMine(); break;
                }
                break;
            case Step.FindingMine:
                if (!steam.CallDone(call) && now - started < 4) break;
                var me = steam.LocalId;
                var mine = steam.FoundLobbies().FirstOrDefault(l => steam.Owner(l) == me && steam.LobbyDataCount(l) == 0 && steam.Members(l) is { Count: 1 } m && m[0] == me);
                if (mine.IsValid) Entered(mine, created: true);
                else if (++tries < 3) BeginFindMine();
                else Fail("Couldn't make a party. Try again in a moment.");
                break;
            case Step.Searching:
                if (now < retryAt || (!steam.CallDone(call) && now - started < 5)) break;
                var found = steam.FoundLobbies().FirstOrDefault(l => steam.LobbyData(l, KeyParty) == "1" && steam.LobbyData(l, KeyCode) == joinCode);
                if (found.IsValid) BeginJoin(found);
                else if (++tries < 3) { retryAt = now + 1.5; call = steam.FindLobbies(KeyCode, joinCode); started = retryAt; }
                else Fail($"No party found with code {joinCode}");
                break;
            case Step.Joining:
                var answer = steam.LobbyEntered(call, out var entered);
                if (answer == CallStatus.Done && !entered && !steam.Members(joinTarget).Contains(steam.LocalId)) Fail("That party is full or locked");
                else if (entered || steam.Members(joinTarget).Contains(steam.LocalId)) Entered(joinTarget, created: false);
                else if (now - started > 10) Fail("Couldn't join that party");
                break;
            case Step.In:
                if (now >= nextMembers) RefreshMembers(now);
                if (step == Step.In) ReceiveMessages();
                break;
        }
        while (loopback.TryDequeue(out var self))
            if (step == Step.In && members.TryGetValue(self.From, out var local)) Deliver?.Invoke(local, self.Mod, self.Payload);
        if (now >= nextFriends) RefreshFriends(now);
    }

    void BeginCreate()
    {
        call = steam.CreateLobby(MaxMembers);
        started = clock();
        tries = 0;
        step = Step.Creating;
        SetStatus("Making a party");
    }

    void BeginFindMine()
    {
        call = steam.FindLobbies(null, null);
        started = clock();
        step = Step.FindingMine;
    }

    void BeginSearch()
    {
        call = steam.FindLobbies(KeyCode, joinCode);
        started = retryAt = clock();
        step = Step.Searching;
        SetStatus($"Looking for party {joinCode}");
    }

    void BeginJoin(SteamId target)
    {
        joinTarget = target;
        call = steam.JoinLobby(target);
        started = clock();
        step = Step.Joining;
        SetStatus("Joining the party");
    }

    void Entered(SteamId target, bool created)
    {
        lobby = target;
        step = Step.In;
        if (created)
        {
            steam.SetLobbyData(lobby, KeyParty, "1");
            steam.SetLobbyData(lobby, KeyCode, code);
            steam.SetLobbyData(lobby, KeyLocked, "0");
        }
        if (!created && steam.LobbyData(lobby, KeyParty) != "1")
        {
            // An invite to some other lobby of this game (not a party).
            steam.LeaveLobby(lobby);
            Fail("That invite isn't for a party");
            return;
        }
        code = steam.LobbyData(lobby, KeyCode);
        // Friends see "Join game" in Steam, which lands in this lobby (see SteamInvites).
        steam.SetRichPresence("connect", $"+connect_lobby {lobby}");
        steam.SetRichPresence("steam_player_group", lobby.ToString());
        members.Clear();
        advertisedMods = "\0";
        AdvertiseMods();
        status = "";
        log(created ? $"party {code} made" : $"joined party {code}");
        Notice?.Invoke(created ? $"Party made. Code: {code}" : "Joined the party");
        RefreshMembers(clock());
        // Say hello to everyone, which also opens the message sessions both ways.
        foreach (var m in members.Keys.Where(m => m != steam.LocalId)) steam.Send(m, Wire.Frame(Wire.KindControl, "party", "hello"u8), true);
    }

    void Fail(string why)
    {
        step = Step.Idle;
        lobby = SteamId.None;
        log(why);
        SetStatus(why);
    }

    void ClearPresence()
    {
        steam.SetRichPresence("connect", null);
        steam.SetRichPresence("steam_player_group", null);
        steam.SetRichPresence("steam_player_group_size", null);
    }

    void LeaveNow(bool quiet)
    {
        if (step == Step.In)
        {
            ClearPresence();
            steam.LeaveLobby(lobby);
            foreach (var m in members.Keys.Where(m => m != steam.LocalId)) steam.Close(m);
            log($"left party {code}");
            if (!quiet) Notice?.Invoke("You left the party");
        }
        else if (step == Step.Joining && steam.Members(joinTarget).Contains(steam.LocalId))
            steam.LeaveLobby(joinTarget);
        step = Step.Idle;
        lobby = SteamId.None;
        members.Clear();
        code = "";
        locked = false;
        status = "";
        Publish();
    }

    void AdvertiseMods()
    {
        if (step != Step.In) return;
        var text = string.Join(",", localMods.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => $"{kv.Key}={kv.Value}"));
        if (text == advertisedMods) return;
        advertisedMods = text;
        steam.SetMemberData(lobby, KeyMods, text);
    }

    void RefreshMembers(double now)
    {
        nextMembers = now + MemberRefresh;
        var me = steam.LocalId;
        var ids = steam.Members(lobby);
        if (!ids.Contains(me))
        {
            log($"no longer in party {code}");
            Notice?.Invoke("The party closed");
            ClearPresence();
            step = Step.Idle;
            lobby = SteamId.None;
            members.Clear();
            code = "";
            Publish();
            return;
        }
        var owner = steam.Owner(lobby);
        if (owner == me)
        {
            if (steam.LobbyData(lobby, KeySize) != ids.Count.ToString()) steam.SetLobbyData(lobby, KeySize, ids.Count.ToString());
        }
        locked = steam.LobbyData(lobby, KeyLocked) == "1";
        if (members.Count != ids.Count) steam.SetRichPresence("steam_player_group_size", ids.Count.ToString());
        var pings = now >= nextPing;
        if (pings) nextPing = now + PingRefresh;
        var changed = false;
        var initial = members.Count == 0; // just entered: the others were already here
        foreach (var gone in members.Keys.Where(id => !ids.Contains(id)).ToList())
        {
            var left = members[gone];
            members.Remove(gone);
            steam.Close(gone);
            changed = true;
            Membership?.Invoke(left, false);
            Notice?.Invoke($"{left.Name} left the party");
        }
        foreach (var id in ids)
        {
            var local = id == me;
            var name = local ? steam.LocalName : steam.NameOf(id);
            if (name.Length == 0) name = "Player";
            members.TryGetValue(id, out var old);
            if (!local) steam.Accept(id);
            var member = new PartyMember(id, name, id == owner, local, local ? localMods : ParseMods(steam.MemberData(lobby, id, KeyMods)),
                local ? 0 : pings ? steam.Ping(id) : old?.PingMs ?? -1);
            if (old is null || old.Name != member.Name || old.IsHost != member.IsHost || old.PingMs != member.PingMs || !SameMods(old.Mods, member.Mods))
            {
                members[id] = member;
                changed = true;
                if (old is null && !local)
                {
                    Membership?.Invoke(member, true);
                    if (!initial) Notice?.Invoke($"{member.Name} joined the party");
                }
                else if (old is not null && !old.IsHost && member.IsHost)
                    Notice?.Invoke(local ? "You're the party host now" : $"{member.Name} is the party host now");
            }
        }
        if (changed || snapshot.State != PartyState.InParty || snapshot.Locked != locked) Publish();
    }

    void ReceiveMessages()
    {
        inbox.Clear();
        steam.Receive(inbox, 256);
        foreach (var (from, data) in inbox)
        {
            // Only party members, and never trust a frame that says it is from someone else.
            if (from == steam.LocalId || !members.TryGetValue(from, out var sender)) continue;
            if (!Wire.TryParse(data, out var kind, out var mod, out var payload)) continue;
            if (kind == Wire.KindMod) Deliver?.Invoke(sender, mod, payload);
        }
    }

    void RefreshFriends(double now)
    {
        nextFriends = now + FriendRefresh;
        var list = new List<FriendParty>();
        foreach (var friend in steam.OnlineFriends())
        {
            var l = steam.FriendLobby(friend);
            var isParty = false;
            int size = 0;
            var isLocked = false;
            if (l.IsValid)
            {
                if (steam.LobbyData(l, KeyParty) == "1")
                {
                    isParty = true;
                    int.TryParse(steam.LobbyData(l, KeySize), out size);
                    isLocked = steam.LobbyData(l, KeyLocked) == "1";
                }
                else if (pendingLobbyData.Add(l)) steam.RequestLobbyData(l);
            }
            if (isParty && l == lobby) continue; // already together
            list.Add(new FriendParty(friend, steam.NameOf(friend), isParty, size, isLocked));
        }
        if (pendingLobbyData.Count > 500) pendingLobbyData.Clear();
        var sorted = list.OrderByDescending(f => f.InParty && !f.Locked).ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ToList();
        if (!sorted.SequenceEqual(friends))
        {
            friends = sorted;
            Publish();
        }
    }

    void SetStatus(string text)
    {
        status = text;
        Publish();
    }

    void Publish()
    {
        var state = step switch
        {
            Step.In => PartyState.InParty,
            Step.Creating or Step.FindingMine => PartyState.Creating,
            Step.Searching or Step.Joining => PartyState.Joining,
            _ => PartyState.None,
        };
        var list = members.Values.OrderByDescending(m => m.IsHost).ThenByDescending(m => m.IsLocal).ThenBy(m => m.Name, StringComparer.OrdinalIgnoreCase).ToList();
        snapshot = new PartySnapshot(state, lobby, step == Step.In ? code : "", locked, list, friends, status);
        Changed?.Invoke(snapshot);
    }

    static bool SameMods(IReadOnlyDictionary<string, string> a, IReadOnlyDictionary<string, string> b) => a.Count == b.Count && a.All(kv => b.TryGetValue(kv.Key, out var v) && v == kv.Value);

    internal static IReadOnlyDictionary<string, string> ParseMods(string text)
    {
        var mods = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var part in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var eq = part.IndexOf('=');
            var id = eq < 0 ? part : part[..eq];
            if (id.Length is > 0 and <= 64 && mods.Count < 64) mods[id] = eq < 0 ? "" : part[(eq + 1)..];
        }
        return mods;
    }

    internal static string NewCode() => string.Create(6, 0, static (span, _) => { for (var i = 0; i < span.Length; i++) span[i] = CodeAlphabet[RandomNumberGenerator.GetInt32(CodeAlphabet.Length)]; });

    /// <summary>Uppercase, without spaces or dashes.</summary>
    internal static string NormalizeCode(string text) => new(text.ToUpperInvariant().Where(char.IsAsciiLetterOrDigit).ToArray());
}
