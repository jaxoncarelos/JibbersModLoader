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

        public FakePlayer(ulong id, string name, LocalMapIndex? maps = null)
        {
            Self = new PartyMember(new SteamId(id), name, false, true, new Dictionary<string, string>(), 0);
            Session = new ServerSession(this, () => Now, maps);
        }

        public SteamId Me => Self.Id;
        public bool InParty => InPartyNow;
        public IReadOnlyList<PartyMember> Members => Party;
        public string PanelKey => "F7";
        public bool FollowLeader { get; set; } = true;
        public readonly ConcurrentQueue<string> Logged = new();
        public void Log(string line) => Logged.Enqueue(line);
        /// <summary>Reliable sends to refuse, as Steam does when its send buffer is full.</summary>
        public int RefuseReliable;
        /// <summary>Payloads sent that never arrive: an older SDK on the other end, or a lost answer.</summary>
        public Func<byte[], bool>? Drop;
        /// <summary>Bytes of TCP connections sent through the tunnel.</summary>
        public long StreamBytes;
        readonly object deliver = new();
        public bool Send(SteamId to, byte[] payload, bool reliable)
        {
            if (reliable && Interlocked.Decrement(ref RefuseReliable) >= 0) return false;
            if (Drop?.Invoke(payload) == true) return true; // lost on the way (or never understood)
            if (StreamTunnel.TryParse(payload, out _, out var op, out var data) && op == StreamTunnel.Data) Interlocked.Add(ref StreamBytes, data.Length);
            // Delivered as the SDK would: to the member with that id, from us as a remote member, one at a time.
            if (Peer is not null && Peer.Me == to)
                lock (deliver) Peer.Session.Receive(Peer.Party.Single(m => m.Id == Me), payload);
            return true;
        }
        public void Connect(string address, int port, string password) => Connected = (address, port, password);
        public void Announce(string line) => Lines.Enqueue(line);
        // Called from relay threads too, so counted atomically.
        public void Fast(bool on) => Interlocked.Add(ref FastHolds, on ? 1 : -1);
        public void CreateParty() => PartyMade = true;
        public void OpenInviteOverlay() => OverlayOpened = true;
    }

    static (FakePlayer Ana, FakePlayer Ben) Party(LocalMapIndex? benMaps = null)
    {
        var ana = new FakePlayer(1, "Ana");
        var ben = new FakePlayer(2, "Ben", benMaps);
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
        // The host relay forwards to the server before it takes its fast hold, so the answer can beat it here.
        Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref ben.FastHolds) == 1 && Volatile.Read(ref ana.FastHolds) == 1, 3000), "both poll fast while the tunnel is open");

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
    static void TheServersMapComesThroughTheTunnelOverTcp()
    {
        // The game's server serves its map (when it isn't on mod.io) over TCP on its own port,
        // and the joining game downloads it from the address it joined: 127.0.0.1 through the
        // tunnel. Without this the download failed with "Connection refused".
        var (ana, ben) = Party();
        using var udp = Loopback.Udp();
        var port = ((IPEndPoint)udp.LocalEndPoint!).Port;
        using var mapServer = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        mapServer.Bind(new IPEndPoint(IPAddress.Loopback, port));
        mapServer.Listen(4);
        var map = new byte[3 * 1024 * 1024 + 123];
        new Random(7).NextBytes(map);
        var served = new Thread(() =>
        {
            using var c = mapServer.Accept();
            var request = new byte[64];
            var n = c.Receive(request);
            if (Encoding.ASCII.GetString(request, 0, n) == "GET /map/ruka") c.Send(map);
            c.Shutdown(SocketShutdown.Send);
            while (c.Receive(request) > 0) { }
        }) { IsBackground = true };
        served.Start();

        ana.Session.Update(new ServerInfo(true, true, port, ""));
        ben.Session.Join(ana.Me);
        // Steam's buffer fills up now and then: the stream waits and carries on.
        ana.RefuseReliable = 40;
        ben.RefuseReliable = 3;

        using var game = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { ReceiveTimeout = 10000 };
        game.Connect(new IPEndPoint(IPAddress.Loopback, ben.Connected!.Value.Port));
        game.Send(Encoding.ASCII.GetBytes("GET /map/ruka"));
        var got = new MemoryStream();
        var buffer = new byte[65536];
        int read;
        while ((read = game.Receive(buffer)) > 0) got.Write(buffer, 0, read);
        Assert.Equal(map.Length, (int)got.Length);
        Assert.True(got.ToArray().AsSpan().SequenceEqual(map), "the map arrives intact");
        game.Dispose();
        Assert.True(served.Join(5000), "the server's side closes too");
        ana.Session.Dispose();
        ben.Session.Dispose();
    }

    /// <summary>A fake game server on TCP: answers "GET /map/ruka" with the map and "GET /status" with a line, then closes, as often as asked.</summary>
    sealed class MapServer : IDisposable
    {
        readonly Socket listener = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        public readonly Socket Udp = Loopback.Udp();
        public int Port => ((IPEndPoint)Udp.LocalEndPoint!).Port;
        public int MapsServed;
        public MapServer(byte[] map)
        {
            listener.Bind(new IPEndPoint(IPAddress.Loopback, Port));
            listener.Listen(8);
            new Thread(() =>
            {
                while (true)
                {
                    Socket c;
                    try { c = listener.Accept(); }
                    catch { return; }
                    new Thread(() =>
                    {
                        using (c)
                        {
                            var request = new byte[256];
                            var n = c.Receive(request);
                            var text = Encoding.ASCII.GetString(request, 0, n);
                            if (text == "GET /map/ruka") { Interlocked.Increment(ref MapsServed); c.Send(map); }
                            else if (text == "GET /status") c.Send("ruka 1234"u8.ToArray());
                            c.Shutdown(SocketShutdown.Send);
                            try { while (c.Receive(request) > 0) { } } catch (SocketException) { }
                        }
                    }) { IsBackground = true }.Start();
                }
            }) { IsBackground = true }.Start();
        }
        public void Dispose()
        {
            listener.Dispose();
            Udp.Dispose();
        }
    }

    static byte[] Fetch(int port, string request)
    {
        using var game = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { ReceiveTimeout = 15000 };
        game.Connect(new IPEndPoint(IPAddress.Loopback, port));
        game.Send(Encoding.ASCII.GetBytes(request));
        var got = new MemoryStream();
        var buffer = new byte[65536];
        int read;
        while ((read = game.Receive(buffer)) > 0) got.Write(buffer, 0, read);
        return got.ToArray();
    }

    /// <summary>A joining player with a maps folder (and hash cache) of their own; the host serves <paramref name="map"/>.</summary>
    static void WithLocalMaps(byte[] map, byte[]? localCopy, Action<FakePlayer, FakePlayer, MapServer, string> test)
    {
        var dir = Path.Combine(Path.GetTempPath(), "jibbers-playmap-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "Maps"));
        try
        {
            File.WriteAllBytes(Path.Combine(dir, "Maps", "other.jbrmap"), new byte[1000]);
            if (localCopy is not null) File.WriteAllBytes(Path.Combine(dir, "Maps", "Ruka copy.jbrmap"), localCopy);
            var maps = new LocalMapIndex([Path.Combine(dir, "Maps")], Path.Combine(dir, "cache", "map-hashes.json"));
            var (ana, ben) = Party(maps);
            using var server = new MapServer(map);
            ana.Session.Update(new ServerInfo(true, true, server.Port, ""));
            ben.Session.Join(ana.Me);
            try { test(ana, ben, server, dir); }
            finally
            {
                ana.Session.Dispose();
                ben.Session.Dispose();
            }
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    }

    static byte[] RandomMap(int seed)
    {
        var map = new byte[2 * 1024 * 1024 + 77];
        new Random(seed).NextBytes(map);
        return map;
    }

    [Test]
    static void AMapAlreadyOnDiskIsServedLocally()
    {
        var map = RandomMap(11);
        WithLocalMaps(map, map, (ana, ben, server, dir) =>
        {
            var got = Fetch(ben.Connected!.Value.Port, "GET /map/ruka");
            Assert.True(got.AsSpan().SequenceEqual(map), "the game gets the map, byte for byte");
            Assert.True(Interlocked.Read(ref ana.StreamBytes) == 0 && Interlocked.Read(ref ben.StreamBytes) == 0, "nothing went through the tunnel");
            Assert.Equal(1, server.MapsServed); // the host reading it over loopback
            Assert.True(ben.Logged.Any(l => l.StartsWith("map served locally: ruka 2.0 MB from ") && l.EndsWith("Ruka copy.jbrmap")), "the decision is logged");
            Assert.True(ana.Logged.Any(l => l.StartsWith("map ruka as the server serves it: 2.0 MB, sha256 ")));
            Assert.True(File.Exists(Path.Combine(dir, "cache", "map-hashes.json")), "hashes are kept for next time");

            // Everything else still goes to the host's server.
            Assert.Equal("ruka 1234", Encoding.ASCII.GetString(Fetch(ben.Connected!.Value.Port, "GET /status")));
            Assert.True(Interlocked.Read(ref ben.StreamBytes) == "GET /status".Length);

            // A second download (another join) uses the cached hash, and still matches.
            Assert.True(Fetch(ben.Connected!.Value.Port, "GET /map/ruka").AsSpan().SequenceEqual(map));
            Assert.True(Interlocked.Read(ref ana.StreamBytes) == "ruka 1234".Length, "only the status came back through the tunnel");
        });
    }

    [Test]
    static void ADifferentLocalMapIsNotServed()
    {
        // Same size, different bytes (an edited copy): the hash tells them apart.
        var map = RandomMap(12);
        var edited = (byte[])map.Clone();
        edited[^5] ^= 1;
        WithLocalMaps(map, edited, (ana, ben, server, _) =>
        {
            var got = Fetch(ben.Connected!.Value.Port, "GET /map/ruka");
            Assert.True(got.AsSpan().SequenceEqual(map), "the server's map arrives through the tunnel");
            Assert.True(Interlocked.Read(ref ana.StreamBytes) == map.Length, "downloaded from the host");
            Assert.True(ben.Logged.Any(l => l == "no local copy of ruka (2.0 MB): downloading it from the host"));
            Assert.Equal(2, server.MapsServed);
        });
    }

    [Test]
    static void NoWordFromTheHostMeansDownloading()
    {
        var map = RandomMap(13);
        var wait = ClientRelay.OfferWait;
        ClientRelay.OfferWait = TimeSpan.FromMilliseconds(300);
        try
        {
            // The host's answer never arrives: the join must not hang on it.
            WithLocalMaps(map, map, (ana, ben, server, _) =>
            {
                ana.Drop = p => p[0] == ServerSession.Control && Encoding.UTF8.GetString(p, 1, p.Length - 1).Contains("\"t\":\"map\"");
                var got = Fetch(ben.Connected!.Value.Port, "GET /map/ruka");
                Assert.True(got.AsSpan().SequenceEqual(map));
                Assert.True(Interlocked.Read(ref ana.StreamBytes) == map.Length, "downloaded from the host");
                Assert.True(ben.Logged.Any(l => l.StartsWith("no word on the map ruka from the host within")));
            });
        }
        finally { ClientRelay.OfferWait = wait; }

        // A host with an older SDK doesn't offer map info in its welcome: nothing is asked or awaited.
        var dir = Path.Combine(Path.GetTempPath(), "jibbers-playmap-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllBytes(Path.Combine(dir, "ruka.jbrmap"), map);
            var (ana, ben) = Party(new LocalMapIndex([dir], null));
            var asked = 0;
            ana.Drop = p =>
            {
                if (p[0] != ServerSession.Control) return false;
                var text = Encoding.UTF8.GetString(p, 1, p.Length - 1);
                if (!text.Contains("\"t\":\"welcome\"")) return false;
                ben.Session.Receive(ben.Party.Single(m => m.Id == ana.Me), Encoding.UTF8.GetBytes("\u0001{\"t\":\"welcome\",\"pw\":\"\"}"));
                return true;
            };
            ben.Drop = p => p[0] == ServerSession.Control && Encoding.UTF8.GetString(p, 1, p.Length - 1).Contains("mapq") && Interlocked.Increment(ref asked) > 0;
            using var server = new MapServer(map);
            ana.Session.Update(new ServerInfo(true, true, server.Port, ""));
            ben.Session.Join(ana.Me);
            var clock = System.Diagnostics.Stopwatch.StartNew();
            Assert.True(Fetch(ben.Connected!.Value.Port, "GET /map/ruka").AsSpan().SequenceEqual(map));
            Assert.True(asked == 0 && Interlocked.Read(ref ana.StreamBytes) == map.Length, "downloaded without asking");
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(3), "without waiting");
            ana.Session.Dispose();
            ben.Session.Dispose();
        }
        finally { Directory.Delete(dir, true); }
    }

    [Test]
    static void TheHostMeasuresWhatItsServerServes()
    {
        // Raw bytes: the whole answer is the map.
        var map = RandomMap(14);
        using var server = new MapServer(map);
        var offer = MapRequests.Probe(server.Port, "GET /map/ruka", CancellationToken.None)!;
        Assert.True(offer.Size == map.Length && offer.Head.Length == 0 && offer.Name == "ruka");
        Assert.Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(map)).ToLowerInvariant(), offer.Sha256);
        Assert.True(MapRequests.Probe(server.Port, "GET /map/nothing", CancellationToken.None) is null, "no answer, no offer");

        // Round trip through the party message, and junk is refused.
        var back = MapOffer.FromJson(System.Text.Json.Nodes.JsonNode.Parse(offer.ToJson().ToJsonString()))!;
        Assert.True(back.Sha256 == offer.Sha256 && back.Size == offer.Size && back.Request == offer.Request);
        Assert.True(MapOffer.FromJson(System.Text.Json.Nodes.JsonNode.Parse("{\"t\":\"map\",\"req\":\"GET /map/ruka\"}")) is null);
        Assert.True(MapOffer.FromJson(System.Text.Json.Nodes.JsonNode.Parse("{\"t\":\"map\",\"req\":\"GET /status\",\"size\":5,\"sha256\":\"" + offer.Sha256 + "\"}")) is null);
        Assert.True(MapRequests.TryParse("GET /map/MLNZ Park\r\n", out var name) && name == "MLNZ Park");
        Assert.True(!MapRequests.TryParse("GET /status", out _));
    }

    static void Bring(FakePlayer leader) => leader.Session.PanelActions().Single(a => a.Label == "Bring everyone to this server").Run();

    [Test]
    static void TheLeaderBringsThePartyToAPublicServer()
    {
        var (ana, ben) = Party();
        Assert.True(ana.Session.PanelActions().All(a => a.Label != "Bring everyone to this server"), "not on a server: nothing to bring anyone to");
        ana.Session.Update(new ServerInfo(false, true, 7777, "letmein", "203.0.113.5"));
        Assert.True(ben.Session.PanelActions().All(a => a.Label != "Bring everyone to this server"), "only the leader brings the party");
        Bring(ana);
        Assert.True(ben.Connected == ("203.0.113.5", 7777, "letmein"), "Ben's game connects straight to the server, with its password");
        Assert.True(ben.Lines.Contains("Following Ana to their server"));
        Assert.True(ana.Lines.Contains("Bringing the party to your server"));
        Assert.True(!ana.Logged.Any(l => l.Contains("letmein")) && !ben.Logged.Any(l => l.Contains("letmein")), "the password stays out of the logs");
        ana.Session.Dispose();
        ben.Session.Dispose();
    }

    [Test]
    static void TheLeaderBringsThePartyFromPublicToTheirOwnServer()
    {
        var (ana, ben) = Party();
        ben.Session.Update(new ServerInfo(false, true, 7777, "", "203.0.113.5"));
        ana.Session.Update(new ServerInfo(true, true, 7770, "secret"));
        Bring(ana);
        Assert.True(ben.Connected is { Address: "127.0.0.1", Password: "secret" }, "Ben joins Ana's server through the tunnel");
        Assert.True(ben.Lines.Contains("Following Ana to their server"));
        ana.Session.Dispose();
        ben.Session.Dispose();
    }

    [Test]
    static void ServerHoppingLeavesTheTunnelFirst()
    {
        var (ana, ben) = Party();
        ana.Session.Update(new ServerInfo(true, true, 7770, ""));
        ben.Session.Join(ana.Me);
        Assert.Equal("Joining Ana's server…", ben.Session.Status);
        // Ana moves to a public server and brings Ben along.
        ana.Session.Update(new ServerInfo(false, true, 7777, "", "203.0.113.5"));
        Bring(ana);
        Assert.True(ben.Connected == ("203.0.113.5", 7777, ""), "on to the public server");
        Assert.Equal("", ben.Session.Status);
        Assert.True(ben.Logged.Any(l => l == "leaving Ana's server to follow Ana"));
        ana.Session.Dispose();
        ben.Session.Dispose();
    }

    [Test]
    static void FollowingTheLeaderCanBeTurnedOff()
    {
        var (ana, ben) = Party();
        ben.FollowLeader = false;
        ana.Session.Update(new ServerInfo(false, true, 7777, "", "203.0.113.5"));
        Bring(ana);
        Assert.True(ben.Connected is null && ben.Lines.Any(l => l.Contains("following the leader is off")));

        // Someone hosting their own server isn't pulled off it.
        var (cat, dan) = Party();
        dan.Session.Update(new ServerInfo(true, true, 7771, ""));
        cat.Session.Update(new ServerInfo(false, true, 7777, "", "203.0.113.5"));
        Bring(cat);
        Assert.True(dan.Connected is null && dan.Lines.Contains("Ana wants the party on their server: stop hosting yours to join them"));
        foreach (var p in new[] { ana, ben, cat, dan }) p.Session.Dispose();
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
