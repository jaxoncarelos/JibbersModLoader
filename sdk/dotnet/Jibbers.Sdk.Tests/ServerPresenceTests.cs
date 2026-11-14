using Jibbers.Sdk.Multiplayer;

static class ServerPresenceTests
{
    const ulong AliceId = 76561198000000001, BobId = 76561198000000002, CarolId = 76561198000000003, DanId = 76561198000000004;
    const ulong LobbyId = 0x0184000000001000;

    /// <summary>Strangers (not Steam friends) with Play Together on one game server; the game tells nobody any Steam id.</summary>
    sealed class Server
    {
        public readonly PartyTests.FakeNet Net = new() { NextLobby = LobbyId };
        public readonly List<(PartyTests.FakeSteam Steam, PartyEngine Engine, int ClientId)> Players = new();
        /// <summary>Everyone on the game server, modded or not: client id and the name the server gave them.</summary>
        public readonly List<RosterEntry> Roster = new();
        public readonly List<string> Notices = new();

        public (PartyTests.FakeSteam Steam, PartyEngine Engine) Add(ulong id, string name, int clientId, bool modded = true)
        {
            Roster.Add(new(clientId, name));
            if (!modded) return default;
            PartyTests.FakeSteam.names[id] = name;
            var steam = new PartyTests.FakeSteam(Net, id, name);
            var engine = new PartyEngine(steam, () => Net.Now, _ => { });
            engine.Notice = Notices.Add;
            Players.Add((steam, engine, clientId));
            return (steam, engine);
        }

        public void Run(double seconds = 6, bool roster = true)
        {
            for (var i = 0; i < seconds * 2; i++)
            {
                Net.Now += 0.5;
                foreach (var (_, engine, clientId) in Players)
                {
                    if (roster) engine.SetServerPlayers(true, new(Roster.ToArray(), clientId));
                    engine.Tick();
                }
            }
        }

        public List<PartyTests.FakeNet.Lobby> ServerLobbies() => Net.Lobbies.Values.Where(l => l.Invisible).ToList();
    }

    sealed record Peers(Server Server, PartyEngine A, PartyEngine B, PartyTests.FakeSteam Alice, PartyTests.FakeSteam Bob);

    /// <summary>Alice and Bob with Play Together, Carol without, all on one server; long enough for any lobby merge.</summary>
    static Peers Pair()
    {
        var server = new Server();
        var (alice, a) = server.Add(AliceId, "Alice", 0);
        var (bob, b) = server.Add(BobId, "Bob", 1);
        server.Add(CarolId, "Carol", 2, modded: false);
        server.Run(40);
        return new(server, a, b, alice, bob);
    }

    static void Invite(Peers p)
    {
        p.A.InviteServerPlayer(new(BobId));
        p.Server.Run(4);
    }

    [Test] static void StrangersOnTheSameServerFindEachOtherAndJoinAPartyInGame()
    {
        var p = Pair();
        Assert.Equal(0, p.Alice.Friends.Count);
        Assert.Equal("Bob", p.A.ServerPresence.Players.Single().Name);
        Assert.Equal("Alice", p.B.ServerPresence.Players.Single().Name);
        Assert.Equal(1, p.Server.ServerLobbies().Count);
        Assert.Equal(new SteamId(BobId), p.Alice.PlayedWith.Single());

        Invite(p);
        Assert.Equal(PartyState.InParty, p.A.Snapshot.State);
        Assert.Equal(new SteamId(AliceId), p.B.ServerPresence.Invitation!.From);
        Assert.Equal(PartyState.None, p.B.Snapshot.State, "receiving an invite must not auto-join");
        Assert.True(p.Server.Notices.Any(n => n.StartsWith("Alice invited you")));

        p.B.AcceptServerInvite();
        p.Server.Run(2);
        Assert.Equal(PartyState.InParty, p.B.Snapshot.State);
        Assert.Equal(p.A.Snapshot.Lobby, p.B.Snapshot.Lobby);
        Assert.Equal(1, p.Server.ServerLobbies().Count, "both stay in the server lobby beside the party");
        Assert.True(!PartyPanel.ServerPlayerActions(p.A.Snapshot, p.A.ServerPresence, _ => { }, () => { }, () => { }).Any(a => a.Label == "Invite Bob"), "party members are not offered again");
        Assert.Equal(0, p.Alice.SteamInvites + p.Bob.SteamInvites);
        Assert.Equal(0, p.Alice.OverlayOpens + p.Bob.OverlayOpens);
    }

    [Test] static void PlayersOnOtherServersOrNotOnTheRosterAreNeverShown()
    {
        var p = Pair();
        // Dan copies this server's lobby key and joins it, but the game doesn't have him on its roster.
        var lobby = p.Server.Net.Lobbies.Single(l => l.Value.Invisible);
        var dan = new PartyTests.FakeSteam(p.Server.Net, DanId, "Dan");
        lobby.Value.Members.Add(dan.Id);
        dan.SetMemberData(lobby.Key, ServerPresenceDiscovery.KeyPlay, "2:480");
        dan.SetMemberData(lobby.Key, ServerPresenceDiscovery.KeyClient, "7");
        dan.SetMemberData(lobby.Key, ServerPresenceDiscovery.KeyName, "Dan");
        p.Server.Run(2);
        Assert.Equal("Bob", p.A.ServerPresence.Players.Single().Name);
        // Pretending to be Carol (on the roster, without the mod) needs her client id and her name.
        dan.SetMemberData(lobby.Key, ServerPresenceDiscovery.KeyClient, "2");
        p.Server.Run(2);
        Assert.True(!p.A.ServerPresence.Players.Any(x => x.Id.Value == DanId), "client id 2 is Carol, not Dan");
        // Claiming Alice's own client id never shows anyone to Alice.
        dan.SetMemberData(lobby.Key, ServerPresenceDiscovery.KeyClient, "0");
        dan.SetMemberData(lobby.Key, ServerPresenceDiscovery.KeyName, "Alice");
        p.Server.Run(2);
        Assert.True(!p.A.ServerPresence.Players.Any(x => x.Id.Value == DanId));

        // Another server has its own lobby; nobody sees across.
        var other = new Server();
        var (_, e) = other.Add(DanId, "Dan", 0);
        other.Add(CarolId, "Carol", 1, modded: false);
        other.Run();
        Assert.True(ServerPresenceDiscovery.ServerKey(480, p.Server.Roster) != ServerPresenceDiscovery.ServerKey(480, other.Roster));
        Assert.Equal(0, e.ServerPresence.Players.Count);
    }

    [Test] static void AClientWithoutItsClientIdIsMatchedByAnUnambiguousName()
    {
        var p = Pair();
        var lobby = p.Server.Net.Lobbies.Single(l => l.Value.Invisible);
        var carol = new PartyTests.FakeSteam(p.Server.Net, CarolId, "Carol");
        lobby.Value.Members.Add(carol.Id);
        carol.SetMemberData(lobby.Key, ServerPresenceDiscovery.KeyPlay, "2:480");
        carol.SetMemberData(lobby.Key, ServerPresenceDiscovery.KeyClient, "-1");
        carol.SetMemberData(lobby.Key, ServerPresenceDiscovery.KeyName, "carol!");
        p.Server.Run(2);
        Assert.True(p.A.ServerPresence.Players.Any(x => x.Id.Value == CarolId && x.Name == "Carol"), "shown under the server's name for her");
        p.Server.Roster.Add(new(5, "Carol"));
        p.Server.Run(2);
        Assert.True(!p.A.ServerPresence.Players.Any(x => x.Id.Value == CarolId), "two Carols on the server: can't tell which");
    }

    [Test] static void TheServerKeyIsSharedAndOnlyChangesWhenTheAnchorPlayerLeaves()
    {
        RosterEntry[] roster = [new(4, "Zed"), new(1, "Ana ☃"), new(9, "Bo")];
        var key = ServerPresenceDiscovery.ServerKey(480, roster);
        Assert.Equal(key, ServerPresenceDiscovery.ServerKey(480, roster.Reverse().ToArray()));
        Assert.Equal(key, ServerPresenceDiscovery.ServerKey(480, [.. roster, new(12, "New player")]));
        Assert.True(key != ServerPresenceDiscovery.ServerKey(480, roster.Where(r => r.ClientId != 1).ToArray()));
        Assert.True(key != ServerPresenceDiscovery.ServerKey(481, roster), "other games never share lobbies");
        Assert.Equal("", ServerPresenceDiscovery.ServerKey(480, []));
        Assert.Equal("ana", ServerPresenceDiscovery.NameKey("  ANA ☃!"));
    }

    [Test] static void PlayersWhoMadeLobbiesAtTheSameMomentEndUpTogether()
    {
        var server = new Server();
        var (_, a) = server.Add(AliceId, "Alice", 0);
        var (_, b) = server.Add(BobId, "Bob", 1);
        // Both search before either lobby exists, so each makes one.
        server.Run(2);
        Assert.Equal(2, server.ServerLobbies().Count);
        Assert.Equal(0, a.ServerPresence.Players.Count);
        server.Run(40);
        Assert.Equal(1, server.ServerLobbies().Count);
        Assert.Equal("Bob", a.ServerPresence.Players.Single().Name);
        Assert.Equal("Alice", b.ServerPresence.Players.Single().Name);
    }

    [Test] static void ALaterArrivalJoinsTheExistingLobby()
    {
        var server = new Server();
        var (_, a) = server.Add(AliceId, "Alice", 0);
        server.Run(3);
        var (_, b) = server.Add(BobId, "Bob", 1);
        server.Run(3);
        Assert.Equal(1, server.ServerLobbies().Count);
        Assert.Equal("Bob", a.ServerPresence.Players.Single().Name);
        Assert.Equal("Alice", b.ServerPresence.Players.Single().Name);
    }

    [Test] static void LeavingTheServerLeavesTheLobbyAndMakesStaleButtonsInert()
    {
        var p = Pair(); Invite(p);
        p.B.SetServerPlayers(false, ServerRoster.Empty); p.B.Tick();
        Assert.True(p.B.ServerPresence.Invitation is null);
        Assert.Equal(0, p.B.ServerPresence.Players.Count);
        Assert.True(!p.Server.ServerLobbies().Any(l => l.Members.Contains(p.Bob.Id)), "Bob left the server lobby");
        p.B.AcceptServerInvite(); p.B.Tick();
        Assert.Equal(PartyState.None, p.B.Snapshot.State);

        // A roster worker that stops (game closed, scene gone) can't leave discovery alive forever.
        p.Server.Run(2, roster: false);
        p.Server.Net.Now += 10; p.A.Tick();
        Assert.Equal(0, p.A.ServerPresence.Players.Count);
        Assert.Equal(0, p.Server.ServerLobbies().Count);
    }

    [Test] static void DeclinedExpiredAndLockedInvitesDoNotJoin()
    {
        var declined = Pair(); Invite(declined);
        declined.B.DeclineServerInvite(); declined.Server.Run(2);
        Assert.True(declined.B.ServerPresence.Invitation is null, "a cached nonce cannot re-open a declined invite");

        var locked = Pair(); Invite(locked);
        locked.Server.Net.Lobbies[locked.A.Snapshot.Lobby].Data[PartyEngine.KeyLocked] = "1";
        locked.B.AcceptServerInvite(); locked.B.Tick();
        Assert.Equal(PartyState.None, locked.B.Snapshot.State);

        var expired = Pair(); Invite(expired);
        expired.Server.Run(50);
        Assert.True(expired.B.ServerPresence.Invitation is null);
        expired.B.AcceptServerInvite(); expired.B.Tick();
        Assert.Equal(PartyState.None, expired.B.Snapshot.State);
    }

    [Test] static void FriendsStillSeeTheirFriendsPartyNotTheServerLobby()
    {
        var p = Pair();
        p.Bob.Friends.Add(p.Alice.Id);
        p.A.Create();
        p.Server.Run(8);
        Assert.True(p.B.Snapshot.Friends.Single().InParty, "Alice's party, not the invisible server lobby, is what Bob's friends list sees");
    }

    [Test] static void InvalidInvitationPayloadsAreRejected()
    {
        var valid = $"1:{BobId}:{LobbyId}:{new string('a', 32)}";
        Assert.True(ServerPresenceDiscovery.TryInvitation(valid, new(BobId), out _, out _));
        foreach (var bad in new[] { valid + ":extra", valid.Replace("1:", "2:"), "1:2:3:abc", new string('a', 10000), valid.Replace(LobbyId.ToString(), AliceId.ToString()) })
            Assert.True(!ServerPresenceDiscovery.TryInvitation(bad, new(BobId), out _, out _), bad);
        Assert.True(!ServerPresenceDiscovery.TryInvitation(valid, new(AliceId), out _, out _));
    }

    [Test] static void PanelOffersServerInvitesAndExplicitPartyReplacementAcceptance()
    {
        var players = new ServerPresence([new(new(BobId), "Bob"), new(new(CarolId), "Carol")], new(new(AliceId), "Alice", new(LobbyId), "nonce", 50));
        var party = PartySnapshot.Unavailable with { State = PartyState.InParty,
            Members = [new(new(CarolId), "Carol", false, false, new Dictionary<string,string>(), 0)] };
        var invited = SteamId.None; var accepted = 0; var declined = 0;
        var actions = PartyPanel.ServerPlayerActions(party, players, id => invited = id, () => accepted++, () => declined++);
        Assert.True(actions[0].Detail.Contains("Leave this party"));
        actions[0].Run(); actions[1].Run();
        actions.Single(action => action.Label == "Invite Bob").Run();
        Assert.Equal(new SteamId(BobId), invited);
        Assert.Equal(1, accepted); Assert.Equal(1, declined);
        Assert.True(!actions.Any(action => action.Label == "Invite Carol"));
    }
}
