using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Jibbers.Sdk.Multiplayer;

static class ServerTests
{
    /// <summary>One player's view of the party and the game: what the session sends, announces and connects to.</summary>
    sealed class FakePlayer : IServerSessionHost
    {
        public readonly PartyMember Self;
        public readonly List<PartyMember> Party = new();
        public readonly ConcurrentQueue<string> Lines = new();
        public FakePlayer? Peer;
        public ServerSession Session = null!;
        public (string Address, int Port, string Password)? Connected;
        public int FastHolds;
        public bool InPartyNow = true, OverlayOpened, PartyMade;
        public double Now;

        public FakePlayer(ulong id, string name)
        {
            Self = new PartyMember(new SteamId(id), name, false, true, new Dictionary<string, string>(), 0);
            Session = new ServerSession(this, () => Now);
        }

        public SteamId Me => Self.Id;
        public bool InParty => InPartyNow;
        public IReadOnlyList<PartyMember> Members => Party;
        public string PanelKey => "F7";
        public readonly ConcurrentQueue<string> Logged = new();
        public void Log(string line) => Logged.Enqueue(line);
        public void Send(SteamId to, byte[] payload, bool reliable)
        {
            // Delivered as the SDK would: to the member with that id, from us as a remote member.
            if (Peer is not null && Peer.Me == to) Peer.Session.Receive(Peer.Party.Single(m => m.Id == Me), payload);
        }
        public void Connect(string address, int port, string password) => Connected = (address, port, password);
        public void Announce(string line) => Lines.Enqueue(line);
        public void Fast(bool on) => FastHolds += on ? 1 : -1;
        public void CreateParty() => PartyMade = true;
        public void OpenInviteOverlay() => OverlayOpened = true;
    }

    static (FakePlayer Ana, FakePlayer Ben) Party()
    {
        var ana = new FakePlayer(1, "Ana");
        var ben = new FakePlayer(2, "Ben");
        ana.Peer = ben;
        ben.Peer = ana;
        ana.Party.AddRange(new[] { ana.Self with { IsHost = true }, ben.Self with { IsLocal = false } });
        ben.Party.AddRange(new[] { ana.Self with { IsHost = true, IsLocal = false }, ben.Self });
        return (ana, ben);
    }

    static byte[] Receive(Socket s, out EndPoint from)
    {
        var buffer = new byte[2048];
        from = new IPEndPoint(IPAddress.Any, 0);
        var n = s.ReceiveFrom(buffer, ref from);
        return buffer[..n];
    }

    [Test]
    static void AFriendJoinsAServerThroughTheTunnel()
    {
        var (ana, ben) = Party();
        // Ana's game server: echoes what it gets, and remembers who sent it.
        using var server = Loopback.Udp();
        server.ReceiveTimeout = 3000;
        var serverPort = ((IPEndPoint)server.LocalEndPoint!).Port;

        ana.Session.Update(new ServerInfo(true, true, serverPort, "hunter2"));
        Assert.True(ana.Lines.Any(l => l.StartsWith("Hosting")), "the host is told they're hosting");
        Assert.True(ben.Session.Hosts.Contains(ana.Me), "the party hears about the server");
        Assert.True(ben.Lines.Any(l => l == "Ana is hosting: press F7 to join"));
        Assert.Equal("Join Ana's server", ben.Session.PanelActions().Single().Label);

        ben.Session.Join(ana.Me);
        Assert.True(ben.Connected is { Address: "127.0.0.1" } c && c.Password == "hunter2", "the game connects to the local relay with the server's password");
        Assert.True(ana.Lines.Contains("Ben is joining your server"));
        Assert.Equal("Joining Ana's server…", ben.Session.Status);

        // Ben's game talks to the relay as if it were the server; the server hears it and answers.
        using var game = Loopback.Udp();
        game.ReceiveTimeout = 3000;
        game.SendTo("hello"u8.ToArray(), new IPEndPoint(IPAddress.Loopback, ben.Connected!.Value.Port));
        Assert.Equal("hello", Encoding.UTF8.GetString(Receive(server, out var from)));
        Assert.True(((IPEndPoint)from).Port != ((IPEndPoint)game.LocalEndPoint!).Port, "the server sees the host relay's socket, not the remote game");
        server.SendTo("welcome"u8.ToArray(), from);
        Assert.Equal("welcome", Encoding.UTF8.GetString(Receive(game, out _)));
        Assert.True(ben.FastHolds == 1 && ana.FastHolds == 1, "both poll fast while the tunnel is open");

        ben.Session.Update(new ServerInfo(false, true, 0, ""));
        Assert.Equal("Playing on Ana's server", ben.Session.Status);
        Assert.Equal("Hosting · 1 joined through Steam", ana.Session.Status);
        Assert.True(ben.Lines.Contains("Joined Ana's server"));

        // The game's client reading stopped for a moment (it does while it connects or logs in)
        // must not close the tunnel: that is what cut joins off before the handshake finished.
        ben.Session.Update(new ServerInfo(false, false, 0, ""));
        Assert.True(!ben.Lines.Contains("Left Ana's server"), "a moment's stop doesn't leave");
        game.SendTo("still here"u8.ToArray(), new IPEndPoint(IPAddress.Loopback, ben.Connected!.Value.Port));
        Assert.Equal("still here", Encoding.UTF8.GetString(Receive(server, out _)));
        ben.Session.Update(new ServerInfo(false, true, 0, ""));
        ben.Now = 10;
        ben.Session.Update(new ServerInfo(false, false, 0, ""));
        Assert.True(!ben.Lines.Contains("Left Ana's server"), "the stop is timed from when it started");
        Assert.True(ben.Logged.Any(l => l.StartsWith("welcomed by Ana: the game connects to 127.0.0.1:")), "the join is logged");
        Assert.True(ana.Logged.Any(l => l.StartsWith("Ben is joining: sent the welcome")));

        ben.Now = 13;
        ben.Session.Update(new ServerInfo(false, false, 0, ""));
        Assert.True(ben.Lines.Contains("Left Ana's server"));
        Assert.True(ben.Logged.Any(l => l.StartsWith("the game's client stopped for 3 s: leaving Ana's server (2 packets sent, 1 received)")), "with the traffic");
        Assert.True(ana.Logged.Any(l => l.StartsWith("Ben left: closing their tunnel (2 packets to the server, 1 back)")));
        Assert.Equal("", ben.Session.Status);
        Assert.Equal("Hosting your server", ana.Session.Status);
        Assert.True(ben.FastHolds == 0 && ana.FastHolds == 0, "polling slows down again");
        ana.Session.Dispose();
        ben.Session.Dispose();
    }

    [Test]
    static void TheGameAlreadyRunningAClientIsntAJoin()
    {
        // Joining from singleplayer: the game's client reads started before it has reconnected,
        // then stopped while it does. That must not count as joined and then left.
        var (ana, ben) = Party();
        ana.Session.Update(new ServerInfo(true, true, 7770, ""));
        ben.Session.Join(ana.Me);
        ben.Session.Update(new ServerInfo(false, true, 0, ""));
        Assert.Equal("Joining Ana's server…", ben.Session.Status);
        ben.Session.Update(new ServerInfo(false, false, 0, ""));
        ben.Now = 10;
        ben.Session.Update(new ServerInfo(false, false, 0, ""));
        Assert.True(!ben.Lines.Contains("Left Ana's server") && ben.Session.Status == "Joining Ana's server…", "still joining");
        ana.Session.Dispose();
        ben.Session.Dispose();
    }

    [Test]
    static void AcceptingAnInviteJoinsTheServerByItself()
    {
        var (ana, ben) = Party();
        ben.Session.AutoJoinNext();
        ana.Session.Update(new ServerInfo(true, true, 7770, ""));
        ben.Session.Update(new ServerInfo(false, false, 0, ""));
        Assert.True(ben.Connected is not null, "joined without opening the panel");
        Assert.True(!ben.Lines.Any(l => l.Contains("press F7")), "no 'press F7' for a join that's already happening");
        ana.Session.Dispose();
        ben.Session.Dispose();
    }

    [Test]
    static void ServersComeAndGo()
    {
        var (ana, ben) = Party();
        ana.Session.Update(new ServerInfo(true, true, 7770, ""));
        Assert.True(ben.Session.Hosts.Count == 1);
        ana.Session.Update(ServerInfo.None);
        Assert.True(ben.Session.Hosts.Count == 1, "a moment's stop keeps the server");
        ana.Now = 3;
        ana.Session.Update(ServerInfo.None);
        Assert.True(ben.Session.Hosts.Count == 0, "stopping the server tells the party");
        ana.Now = 0;

        ana.Session.Update(new ServerInfo(true, true, 7770, ""));
        ben.Now = 30;
        ben.Session.Update(ServerInfo.None);
        Assert.True(ben.Session.Hosts.Count == 0, "a host that stops announcing is forgotten");

        ana.Now = ben.Now = 40;
        ana.Session.Update(new ServerInfo(true, true, 7770, ""));
        ben.Session.Join(ana.Me);
        ben.Session.MemberLeft(ana.Me);
        Assert.True(ben.Lines.Contains("Ana left the party") && ben.Session.Status == "", "the host leaving ends the join");

        // Asking a server that never answers gives up.
        var (cat, dan) = Party();
        cat.Peer = null;
        dan.Session.Join(cat.Me);
        dan.Now = 20;
        dan.Session.Update(ServerInfo.None);
        Assert.True(dan.Lines.Contains("Ana's server didn't answer"));
        foreach (var p in new[] { ana, ben, cat, dan }) p.Session.Dispose();
    }

    [Test]
    static void InvitingFriendsToYourServer()
    {
        var (ana, _) = Party();
        ana.InPartyNow = false;
        ana.Session.Update(new ServerInfo(true, true, 7770, ""));
        Assert.True(ana.Lines.Contains("Hosting: press F7 to invite friends over Steam"));
        var invite = ana.Session.PanelActions().Single();
        Assert.Equal("Makes a party first", invite.Detail);
        invite.Run();
        Assert.True(ana.PartyMade && !ana.OverlayOpened);
        ana.InPartyNow = true;
        ana.Session.Update(new ServerInfo(true, true, 7770, ""));
        Assert.True(ana.OverlayOpened, "the invite dialog opens once the party exists");
        ana.Session.Dispose();
    }

    [Test]
    static void SteamJoinRequestsNameTheLobby()
    {
        Assert.Equal(109775241234567890UL, SteamInvites.ParseLobby("\"C:\\Games\\Jibbers.exe\" +connect_lobby 109775241234567890").Value);
        Assert.Equal(5UL, SteamInvites.ParseLobby("+connect_lobby 5\0\0\0").Value);
        Assert.True(!SteamInvites.ParseLobby("+connect_lobby").IsValid);
        Assert.True(!SteamInvites.ParseLobby("+connect_lobby abc").IsValid);
        Assert.True(!SteamInvites.ParseLobby(null).IsValid);
    }
}
