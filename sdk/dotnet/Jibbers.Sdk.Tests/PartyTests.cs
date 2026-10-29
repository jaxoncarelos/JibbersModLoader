using System.Text;
using Jibbers.Sdk.Multiplayer;

static class PartyTests
{
    /// <summary>Steam's lobbies and message queues, shared by several fake clients.</summary>
    sealed class FakeNet
    {
        public sealed class Lobby
        {
            public SteamId Owner;
            public readonly List<SteamId> Members = new();
            public readonly Dictionary<string, string> Data = new();
            public readonly Dictionary<(SteamId, string), string> MemberData = new();
            public bool Joinable = true;
        }
        public readonly Dictionary<SteamId, Lobby> Lobbies = new();
        public readonly Dictionary<SteamId, Queue<(SteamId, byte[])>> Inboxes = new();
        public readonly Dictionary<ulong, (SteamId Client, SteamId Lobby, bool Ok)> Calls = new();
        public ulong NextCall = 100, NextLobby = 9000;
        public double Now;
    }

    sealed class FakeSteam(FakeNet net, ulong id, string name) : ISteamBackend
    {
        public SteamId Id { get; } = new(id);
        public bool EatResults;
        public readonly List<SteamId> Friends = new();
        List<SteamId> found = new();

        public SteamId LocalId => Id;
        public uint AppId => 480;
        public string LocalName => name;
        public string NameOf(SteamId other) => names.TryGetValue(other.Value, out var n) ? n : "";
        public static readonly Dictionary<ulong, string> names = new();
        public IReadOnlyList<SteamId> OnlineFriends() => Friends;
        public SteamId FriendLobby(SteamId friend) => net.Lobbies.FirstOrDefault(l => l.Value.Members.Contains(friend)).Key;

        public ulong CreateLobby(int maxMembers)
        {
            var lobby = new SteamId(net.NextLobby++);
            net.Lobbies[lobby] = new FakeNet.Lobby { Owner = Id, Members = { Id } };
            var call = net.NextCall++;
            net.Calls[call] = (Id, lobby, true);
            return call;
        }
        public CallStatus LobbyCreated(ulong call, out SteamId lobby)
        {
            lobby = net.Calls[call].Lobby;
            return EatResults ? CallStatus.Failed : CallStatus.Done;
        }
        public ulong JoinLobby(SteamId lobby)
        {
            var ok = net.Lobbies.TryGetValue(lobby, out var l) && l.Joinable;
            if (ok && !l!.Members.Contains(Id)) l.Members.Add(Id);
            var call = net.NextCall++;
            net.Calls[call] = (Id, lobby, ok);
            return call;
        }
        public CallStatus LobbyEntered(ulong call, out bool entered)
        {
            entered = net.Calls[call].Ok;
            return EatResults ? CallStatus.Pending : CallStatus.Done;
        }
        public void LeaveLobby(SteamId lobby)
        {
            if (!net.Lobbies.TryGetValue(lobby, out var l)) return;
            l.Members.Remove(Id);
            if (l.Members.Count == 0) net.Lobbies.Remove(lobby);
            else if (l.Owner == Id) l.Owner = l.Members[0];
        }
        public ulong FindLobbies(string? key, string? value)
        {
            found = net.Lobbies.Where(l => key is null || (l.Value.Data.TryGetValue(key, out var v) && v == value)).Select(l => l.Key).ToList();
            return net.NextCall++;
        }
        public bool CallDone(ulong call) => true;
        public IReadOnlyList<SteamId> FoundLobbies() => found;
        public IReadOnlyList<SteamId> Members(SteamId lobby) => net.Lobbies.TryGetValue(lobby, out var l) ? l.Members.ToList() : new List<SteamId>();
        public SteamId Owner(SteamId lobby) => net.Lobbies.TryGetValue(lobby, out var l) ? l.Owner : SteamId.None;
        public string LobbyData(SteamId lobby, string key) => net.Lobbies.TryGetValue(lobby, out var l) && l.Data.TryGetValue(key, out var v) ? v : "";
        public int LobbyDataCount(SteamId lobby) => net.Lobbies.TryGetValue(lobby, out var l) ? l.Data.Count : 0;
        public void RequestLobbyData(SteamId lobby) { }
        public void SetLobbyData(SteamId lobby, string key, string value) { if (Owner(lobby) == Id) net.Lobbies[lobby].Data[key] = value; }
        public void SetJoinable(SteamId lobby, bool joinable) => net.Lobbies[lobby].Joinable = joinable;
        public string MemberData(SteamId lobby, SteamId member, string key) => net.Lobbies.TryGetValue(lobby, out var l) && l.MemberData.TryGetValue((member, key), out var v) ? v : "";
        public void SetMemberData(SteamId lobby, string key, string value) => net.Lobbies[lobby].MemberData[(Id, key)] = value;
        public bool InviteToLobby(SteamId lobby, SteamId friend) => true;
        public void OpenInviteOverlay(SteamId lobby) { }
        public readonly Dictionary<string, string> Presence = new();
        public void SetRichPresence(string key, string? value) { if (value is null) Presence.Remove(key); else Presence[key] = value; }
        public bool Send(SteamId to, ReadOnlySpan<byte> data, bool reliable)
        {
            if (!net.Inboxes.TryGetValue(to, out var q)) net.Inboxes[to] = q = new();
            q.Enqueue((Id, data.ToArray()));
            return true;
        }
        public int Receive(List<(SteamId From, byte[] Data)> into, int max)
        {
            var n = 0;
            if (net.Inboxes.TryGetValue(Id, out var q))
                while (n < max && q.TryDequeue(out var m)) { into.Add(m); n++; }
            return n;
        }
        public void Accept(SteamId peer) { }
        public void Close(SteamId peer) { }
        public int Ping(SteamId peer) => 42;
    }

    sealed class Client
    {
        public readonly FakeSteam Steam;
        public readonly PartyEngine Engine;
        public readonly List<(string From, string Mod, string Text)> Got = new();
        public readonly List<string> Notices = new();
        public Client(FakeNet net, ulong id, string name)
        {
            FakeSteam.names[id] = name;
            Steam = new FakeSteam(net, id, name);
            Engine = new PartyEngine(Steam, () => net.Now, _ => { });
            Engine.Deliver = (from, mod, payload) => Got.Add((from.Name, mod, Encoding.UTF8.GetString(payload.Span)));
            Engine.Notice = Notices.Add;
        }
        public PartySnapshot S => Engine.Snapshot;
    }

    static void Run(FakeNet net, params Client[] clients)
    {
        for (var i = 0; i < 40; i++)
        {
            net.Now += 0.25;
            foreach (var c in clients) c.Engine.Tick();
        }
    }

    static (FakeNet, Client, Client) Pair()
    {
        var net = new FakeNet();
        var a = new Client(net, 1, "Ana");
        var b = new Client(net, 2, "Ben");
        a.Engine.Create();
        Run(net, a, b);
        b.Engine.JoinCode(a.S.Code.ToLowerInvariant().Insert(3, "-"));
        Run(net, a, b);
        return (net, a, b);
    }

    [Test]
    static void MakingAndJoiningByCode()
    {
        var (_, a, b) = Pair();
        Assert.Equal(PartyState.InParty, a.S.State);
        Assert.Equal(PartyState.InParty, b.S.State);
        Assert.Equal(6, a.S.Code.Length);
        Assert.Equal(a.S.Code, b.S.Code);
        Assert.Equal(2, b.S.Members.Count);
        Assert.True(a.S.Host!.IsLocal);
        Assert.Equal("Ana", b.S.Host!.Name);
        Assert.True(a.Notices.Contains("Ben joined the party"));
        Assert.True(!b.Notices.Any(n => n.Contains("Ana joined")), "no join notices for people already there");
    }

    [Test]
    static void ModsAreAdvertisedAndMessagesRoutedToTheSameMod()
    {
        var (net, a, b) = Pair();
        a.Engine.SetLocalMods(new Dictionary<string, string> { ["slvsh"] = "1.0.0" });
        b.Engine.SetLocalMods(new Dictionary<string, string> { ["slvsh"] = "1.0.1", ["other"] = "2.0.0" });
        Run(net, a, b);
        var ben = a.S.Members.Single(m => m.Name == "Ben");
        Assert.True(ben.HasMod("slvsh") && ben.HasMod("other"));
        Assert.Equal("1.0.1", ben.ModVersion("slvsh"));
        Assert.Equal(42, ben.PingMs);
        Assert.True(a.S.Local!.HasMod("slvsh"));

        Assert.True(a.Engine.Send(b.Steam.Id, "slvsh", "hi ben"u8.ToArray(), true));
        Assert.True(a.Engine.Send(a.Steam.Id, "slvsh", "note to self"u8.ToArray(), true));
        Run(net, a, b);
        Assert.Equal(("Ana", "slvsh", "hi ben"), b.Got.Single());
        Assert.Equal(("Ana", "slvsh", "note to self"), a.Got.Single());
    }

    [Test]
    static void StrangersForgedAndBrokenFramesAreDropped()
    {
        var (net, a, b) = Pair();
        var stranger = new FakeSteam(net, 99, "Eve");
        stranger.Send(a.Steam.Id, Wire.Frame(Wire.KindMod, "slvsh", "boo"u8), true);
        b.Steam.Send(a.Steam.Id, "JB\u0001\u0000ÿbroken"u8, true);
        b.Steam.Send(a.Steam.Id, "not a frame"u8, true);
        Run(net, a, b);
        Assert.Equal(0, a.Got.Count);
        Assert.True(!a.Engine.Send(stranger.Id, "slvsh", "x"u8.ToArray(), true), "can't send outside the party");
    }

    [Test]
    static void LeavingMovesTheHostAndTellsEveryone()
    {
        var net = new FakeNet();
        var a = new Client(net, 1, "Ana");
        var b = new Client(net, 2, "Ben");
        var c = new Client(net, 3, "Cat");
        a.Engine.Create();
        Run(net, a, b, c);
        b.Engine.JoinCode(a.S.Code);
        c.Engine.JoinCode(a.S.Code);
        Run(net, a, b, c);
        Assert.Equal(3, c.S.Members.Count);
        a.Engine.Leave();
        Run(net, a, b, c);
        Assert.Equal(PartyState.None, a.S.State);
        Assert.Equal(2, c.S.Members.Count);
        Assert.Equal("Ben", c.S.Host!.Name);
        Assert.True(c.Notices.Contains("Ana left the party"));
        Assert.True(b.Notices.Contains("You're the party host now"));
    }

    [Test]
    static void BadCodesLockedPartiesAndEatenResults()
    {
        var net = new FakeNet();
        var a = new Client(net, 1, "Ana");
        var b = new Client(net, 2, "Ben");
        b.Engine.JoinCode("ZZZZZZ");
        Run(net, a, b);
        Assert.Equal(PartyState.None, b.S.State);
        Assert.Equal("No party found with code ZZZZZZ", b.S.Status);
        b.Engine.JoinCode("abc");
        Run(net, b);
        Assert.Equal("Party codes are 6 letters and numbers", b.S.Status);

        // The game took Steam's reply to CreateLobby: the party is found among the lobbies instead.
        a.Steam.EatResults = true;
        a.Engine.Create();
        Run(net, a, b);
        Assert.Equal(PartyState.InParty, a.S.State);
        a.Steam.EatResults = false;

        a.Engine.SetLocked(true);
        Run(net, a);
        Assert.True(a.S.Locked);
        b.Engine.JoinCode(a.S.Code);
        Run(net, a, b);
        Assert.Equal(PartyState.None, b.S.State);
        Assert.Equal("That party is full or locked", b.S.Status);
    }

    [Test]
    static void FriendsInPartiesAreListedAndJoinable()
    {
        var net = new FakeNet();
        var a = new Client(net, 1, "Ana");
        var b = new Client(net, 2, "Ben");
        b.Steam.Friends.Add(a.Steam.Id);
        Run(net, a, b);
        Assert.True(!b.S.Friends.Single().InParty);
        a.Engine.Create();
        Run(net, a, b);
        var friend = b.S.Friends.Single();
        Assert.True(friend.InParty);
        Assert.Equal(1, friend.PartySize);
        b.Engine.JoinFriend(friend.Friend);
        Run(net, a, b);
        Assert.Equal(PartyState.InParty, b.S.State);
        Assert.Equal(0, b.S.Friends.Count);
    }

    [Test]
    static void WireAndModListParsing()
    {
        var frame = Wire.Frame(Wire.KindMod, "slvsh", "payload"u8);
        Assert.True(Wire.TryParse(frame, out var kind, out var id, out var payload));
        Assert.Equal((Wire.KindMod, "slvsh", "payload"), (kind, id, Encoding.UTF8.GetString(payload.Span)));
        Assert.True(!Wire.TryParse(frame[..6], out _, out _, out _));
        Assert.Throws<ArgumentException>(() => Wire.Frame(Wire.KindMod, "x", new byte[Wire.MaxPayload + 1]));
        var mods = PartyEngine.ParseMods("slvsh=1.0.0, rumble ,=bad,");
        Assert.Equal("1.0.0", mods["slvsh"]);
        Assert.Equal("", mods["rumble"]);
        Assert.Equal(2, mods.Count);
        Assert.Equal("K7Q2MX", PartyEngine.NormalizeCode(" k7q-2mx "));
        Assert.True(PartyEngine.NewCode().All(c => "ABCDEFGHJKMNPQRSTUVWXYZ23456789".Contains(c)));
    }

    [Test]
    static void SteamInvitesJoinByLobbyAndFriendsSeeJoinGame()
    {
        var net = new FakeNet();
        var a = new Client(net, 1, "Ana");
        var b = new Client(net, 2, "Ben");
        a.Engine.Create();
        Run(net, a, b);
        Assert.Equal($"+connect_lobby {a.S.Lobby}", a.Steam.Presence["connect"]);
        b.Engine.JoinLobby(a.S.Lobby);
        Run(net, a, b);
        Assert.Equal(PartyState.InParty, b.S.State);
        Assert.Equal(a.S.Code, b.S.Code);
        Assert.Equal("2", a.Steam.Presence["steam_player_group_size"]);
        b.Engine.Leave();
        Run(net, a, b);
        Assert.True(!b.Steam.Presence.ContainsKey("connect"), "leaving clears Join game");

        // Some other lobby of the game is not a party.
        var other = new SteamId(net.NextLobby++);
        net.Lobbies[other] = new FakeNet.Lobby { Owner = new SteamId(99) };
        net.Lobbies[other].Members.Add(new SteamId(99));
        b.Engine.JoinLobby(other);
        Run(net, a, b);
        Assert.Equal(PartyState.None, b.S.State);
        Assert.Equal("That invite isn't for a party", b.S.Status);
        Assert.True(!net.Lobbies[other].Members.Contains(b.Steam.Id), "left it again");
    }
}
