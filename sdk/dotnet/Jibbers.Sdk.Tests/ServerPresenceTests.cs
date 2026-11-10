using Jibbers.Sdk.Multiplayer;

static class ServerPresenceTests
{
    const ulong AliceId = 76561198000000001, BobId = 76561198000000002, CarolId = 76561198000000003;
    const ulong LobbyId = 0x0184000000001000;
    sealed class Pair
    {
        public readonly PartyTests.FakeNet Net = new() { NextLobby = LobbyId };
        public readonly PartyTests.FakeSteam Alice, Bob;
        public readonly PartyEngine A, B;
        public Pair()
        {
            Alice = new(Net, AliceId, "Alice"); Bob = new(Net, BobId, "Bob");
            PartyTests.FakeSteam.names[AliceId] = "Alice"; PartyTests.FakeSteam.names[BobId] = "Bob";
            A = new(Alice, () => Net.Now, _ => { }); B = new(Bob, () => Net.Now, _ => { });
            RefreshRoster(); A.Tick(); B.Tick(); Net.Now = 1; A.Tick(); B.Tick();
        }
        public void RefreshRoster()
        {
            A.SetServerPlayers(true, [new(new(BobId), "Bob")]);
            B.SetServerPlayers(true, [new(new(AliceId), "Alice")]);
        }
        public void Invite()
        {
            A.InviteServerPlayer(new(BobId)); A.Tick();
            Net.Now++; B.Tick();
        }
    }
    [Test] static void NonFriendsOnTheCurrentServerDiscoverAndAcceptEntirelyInGame()
    {
        var peers = new Pair();
        Assert.Equal(0, peers.Alice.Friends.Count);
        Assert.Equal("Bob", peers.A.ServerPresence.Players.Single().Name);
        Assert.Equal("Alice", peers.B.ServerPresence.Players.Single().Name);
        peers.Invite();
        Assert.Equal(new SteamId(AliceId), peers.B.ServerPresence.Invitation!.From);
        Assert.Equal(PartyState.None, peers.B.Snapshot.State, "receiving an invite must not auto-join");
        peers.B.AcceptServerInvite(); peers.B.Tick(); peers.A.Tick();
        Assert.Equal(PartyState.InParty, peers.B.Snapshot.State);
        Assert.Equal(peers.A.Snapshot.Lobby, peers.B.Snapshot.Lobby);
        Assert.Equal(0, peers.Alice.SteamInvites + peers.Bob.SteamInvites);
        Assert.Equal(0, peers.Alice.OverlayOpens + peers.Bob.OverlayOpens);
    }
    [Test] static void LeavingTheServerClearsPresenceAndMakesStaleButtonsInert()
    {
        var peers = new Pair(); peers.Invite();
        peers.B.SetServerPlayers(false, []); peers.B.Tick();
        Assert.True(peers.B.ServerPresence.Invitation is null);
        Assert.Equal(0, peers.B.ServerPresence.Players.Count);
        Assert.True(!peers.Bob.Presence.ContainsKey(ServerPresenceDiscovery.PresenceKey));
        peers.B.AcceptServerInvite(); peers.B.Tick();
        Assert.Equal(PartyState.None, peers.B.Snapshot.State);
        peers.A.SetServerPlayers(true, []); peers.A.Tick();
        peers.A.InviteServerPlayer(new(BobId)); peers.A.Tick();
        Assert.Equal(0, peers.A.ServerPresence.Players.Count);
    }
    [Test] static void DeclinedExpiredLockedAndOffServerInvitesDoNotJoin()
    {
        var declined = new Pair(); declined.Invite(); declined.B.DeclineServerInvite(); declined.B.Tick();
        declined.Net.Now += 1; declined.B.Tick();
        Assert.True(declined.B.ServerPresence.Invitation is null, "a cached nonce cannot re-open a declined invite");
        var locked = new Pair(); locked.Invite();
        locked.Net.Lobbies[locked.A.Snapshot.Lobby].Data[PartyEngine.KeyLocked] = "1";
        locked.B.AcceptServerInvite(); locked.B.Tick();
        Assert.Equal(PartyState.None, locked.B.Snapshot.State);
        var expired = new Pair(); expired.Invite(); expired.Net.Now += 50;
        expired.B.AcceptServerInvite(); expired.B.Tick();
        Assert.Equal(PartyState.None, expired.B.Snapshot.State);
        var outsider = new Pair();
        outsider.Bob.SetRichPresence(ServerPresenceDiscovery.InvitationKey, $"1:{AliceId}:{LobbyId}:{new string('a',32)}");
        outsider.A.SetServerPlayers(true, []); outsider.Net.Now++; outsider.A.Tick();
        Assert.True(outsider.A.ServerPresence.Invitation is null);
    }
    [Test] static void PresenceQueriesBackOffAndNeverScanFriendsOnOtherServers()
    {
        var peers = new Pair();
        peers.Alice.Friends.Add(new(CarolId));
        for (var time = 2; time < 10; time++)
        {
            peers.Net.Now = time;
            if (time % 3 == 0) peers.RefreshRoster();
            peers.A.Tick(); peers.B.Tick();
        }
        Assert.Equal(1, peers.Alice.PresenceRequests.Count(id => id.Value == BobId));
        Assert.Equal(new SteamId(BobId), peers.Alice.PlayedWith.Single());
        Assert.True(!peers.Alice.PresenceRequests.Any(id => id.Value == CarolId));
        peers.Net.Now = 10; peers.RefreshRoster(); peers.A.Tick();
        Assert.Equal(2, peers.Alice.PresenceRequests.Count(id => id.Value == BobId));
        peers.Net.Now = 20; peers.A.Tick();
        Assert.Equal(0, peers.A.ServerPresence.Players.Count, "a stopped game-roster worker cannot leave discovery alive forever");
    }
    [Test] static void InvalidIdentityAndInvitationPayloadsAreRejected()
    {
        Assert.True(CurrentServerPlayers.TryAccount("steam:" + AliceId, out var account));
        Assert.Equal(new SteamId(AliceId), account);
        foreach (var bad in new[] { "1", "steam:123", "discord:" + AliceId, "Alice " + AliceId, "-1", "18446744073709551615" })
            Assert.True(!CurrentServerPlayers.TryAccount(bad, out _), bad);
        var valid = $"1:{BobId}:{LobbyId}:{new string('a',32)}";
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
