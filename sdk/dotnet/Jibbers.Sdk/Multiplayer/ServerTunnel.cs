using System.Net;
using System.Net.Sockets;

namespace Jibbers.Sdk.Multiplayer;

/// <summary>Loopback UDP sockets for the tunnel.</summary>
internal static class Loopback
{
    /// <summary>
    /// A UDP socket on 127.0.0.1. On Windows a datagram to a closed port makes the next receive
    /// fail with "connection reset"; that is turned off, so a server restarting doesn't kill the relay.
    /// </summary>
    public static Socket Udp(int port = 0)
    {
        var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        try
        {
            if (OperatingSystem.IsWindows()) s.IOControl(-1744830452 /* SIO_UDP_CONNRESET */, new byte[4], null);
            s.Bind(new IPEndPoint(IPAddress.Loopback, port));
            return s;
        }
        catch
        {
            s.Dispose();
            throw;
        }
    }

    /// <summary>Reads datagrams until the socket closes; <paramref name="got"/> gets a copy of each.</summary>
    public static void Pump(Socket socket, Action<byte[], EndPoint> got)
    {
        var buffer = new byte[65536];
        while (true)
        {
            EndPoint from = new IPEndPoint(IPAddress.Any, 0);
            int n;
            try { n = socket.ReceiveFrom(buffer, ref from); }
            catch (SocketException e) when (e.SocketErrorCode is SocketError.ConnectionReset or SocketError.MessageSize) { continue; }
            catch (Exception e) when (e is SocketException or ObjectDisposedException) { return; }
            if (n > 0) got(buffer.AsSpan(0, n).ToArray(), from);
        }
    }
}

/// <summary>
/// On the PC running the game's server: each remote player gets their own loopback socket that
/// talks to the server's port, so the server sees every player as a separate address
/// (127.0.0.1 with different ports), exactly as if they had connected directly.
/// </summary>
internal sealed class HostRelay : IDisposable
{
    sealed class Peer(Socket socket)
    {
        public readonly Socket Socket = socket;
        public long LastSeen = Environment.TickCount64;
        public long In, Out;
    }

    readonly object gate = new();
    readonly Dictionary<SteamId, Peer> peers = new();
    readonly Action<SteamId, byte[]> toPeer;
    readonly StreamTunnel streams;
    bool disposed;

    /// <param name="toPeer">Sends a datagram to a player (unreliably).</param>
    /// <param name="reliable">Sends a stream frame to a player (reliably); false when Steam won't take it right now.</param>
    public HostRelay(int serverPort, Action<SteamId, byte[]> toPeer, Func<SteamId, byte[], bool>? reliable = null, Action<string>? log = null)
    {
        ServerPort = serverPort;
        this.toPeer = toPeer;
        streams = new StreamTunnel(reliable ?? ((_, _) => false), log ?? (_ => { }));
        offers = new MapOffers(serverPort, log ?? (_ => { }));
    }

    readonly MapOffers offers;

    /// <summary>What the server answers to a map request, read from it over loopback (null if it gave no map).</summary>
    public Task<MapOffer?> MapOffer(string request) => offers.Get(request);

    /// <summary>TCP connections (the map download, status) open through the tunnel.</summary>
    public int Streams => streams.Count;

    /// <summary>A stream frame from a player: their game opened, sent on, or closed a TCP connection to the server.</summary>
    public void FromPeerStream(SteamId peer, uint id, byte op, ReadOnlySpan<byte> data)
    {
        lock (gate)
        {
            if (disposed) return;
            if (peers.TryGetValue(peer, out var p)) p.LastSeen = Environment.TickCount64;
        }
        streams.Receive(peer, id, op, data, () =>
        {
            // The game serves its map and status over TCP on its own port.
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try { socket.Connect(new IPEndPoint(IPAddress.Loopback, ServerPort)); }
            catch { socket.Dispose(); throw; }
            return socket;
        });
    }

    public int ServerPort { get; }

    /// <summary>The players currently tunnelled in.</summary>
    public IReadOnlyList<SteamId> Players
    {
        get { lock (gate) return peers.Keys.ToList(); }
    }

    /// <summary>A datagram from a remote player: on to the server.</summary>
    public void FromPeer(SteamId peer, ReadOnlySpan<byte> datagram)
    {
        Peer? p;
        lock (gate)
        {
            if (disposed) return;
            if (!peers.TryGetValue(peer, out p))
            {
                var socket = Loopback.Udp();
                socket.Connect(new IPEndPoint(IPAddress.Loopback, ServerPort));
                peers[peer] = p = new Peer(socket);
                var made = p;
                var reader = new Thread(() => Loopback.Pump(socket, (data, _) =>
                {
                    Interlocked.Increment(ref made.Out);
                    toPeer(peer, data);
                })) { IsBackground = true, Name = $"Jibbers tunnel {peer}" };
                reader.Start();
            }
            p.LastSeen = Environment.TickCount64;
            p.In++;
        }
        try { p.Socket.Send(datagram); }
        catch (Exception e) when (e is SocketException or ObjectDisposedException) { }
    }

    /// <summary>Datagrams relayed for a player so far: from them to the server, and back; null if they have no socket.</summary>
    public (long ToServer, long ToPlayer)? Traffic(SteamId peer)
    {
        lock (gate)
            return peers.TryGetValue(peer, out var p) ? (p.In, Interlocked.Read(ref p.Out)) : null;
    }

    /// <summary>Closes a player's socket (they left the party or the server).</summary>
    public void Drop(SteamId peer)
    {
        lock (gate)
            if (peers.Remove(peer, out var p)) p.Socket.Dispose();
        streams.Drop(peer);
    }

    /// <summary>Closes the sockets of players who sent nothing for a while (the server has timed them out by then).</summary>
    public void DropIdle(TimeSpan after)
    {
        var cutoff = Environment.TickCount64 - (long)after.TotalMilliseconds;
        lock (gate)
            foreach (var (id, p) in peers.Where(kv => kv.Value.LastSeen < cutoff).ToList())
            {
                peers.Remove(id);
                p.Socket.Dispose();
            }
    }

    public void Dispose()
    {
        lock (gate)
        {
            disposed = true;
            foreach (var p in peers.Values) p.Socket.Dispose();
            peers.Clear();
        }
        streams.Dispose();
        offers.Dispose();
    }
}

/// <summary>
/// On a joining player's PC: a loopback socket the game connects to as if it were the server.
/// Whatever the game sends goes to the host over Steam, and the host's replies come back to it.
/// The same port also takes TCP connections (the game downloads the server's map that way) and
/// carries them to the host's server.
/// </summary>
internal sealed class ClientRelay : IDisposable
{
    /// <summary>How long a map request waits for the host's word on its map (it reads and hashes it first).</summary>
    internal static TimeSpan OfferWait = TimeSpan.FromSeconds(5);
    /// <summary>How long it then waits for a local copy to be found (hashing files not seen before).</summary>
    internal static TimeSpan FindWait = TimeSpan.FromSeconds(10);
    static readonly TimeSpan PeekWait = TimeSpan.FromSeconds(1);

    readonly Socket socket, listener;
    readonly StreamTunnel streams;
    readonly Action<string> log;
    readonly LocalMapIndex? maps;
    readonly Action<string>? askMap;
    readonly object offersGate = new();
    readonly Dictionary<string, TaskCompletionSource<MapOffer?>> offers = new();
    EndPoint? game;
    long toHost, fromHost;
    int nextStream;

    /// <param name="toHost">Sends a datagram to the host (unreliably).</param>
    /// <param name="reliable">Sends a stream frame to the host (reliably); false when Steam won't take it right now.</param>
    /// <param name="maps">This PC's map files: a map request is answered from one identical to the server's (null: always download).</param>
    /// <param name="askMap">Asks the host what its server answers to a map request (null: the host can't say, so don't ask).</param>
    public ClientRelay(SteamId host, Action<SteamId, byte[]> toHost, int preferredPort = 0, Func<SteamId, byte[], bool>? reliable = null, Action<string>? log = null,
        LocalMapIndex? maps = null, Action<string>? askMap = null)
    {
        Host = host;
        (socket, listener) = Bind(preferredPort);
        this.log = log ?? (_ => { });
        this.maps = maps;
        this.askMap = maps is null ? null : askMap;
        streams = new StreamTunnel(reliable ?? ((_, _) => false), this.log);
        new Thread(Accept) { IsBackground = true, Name = "Jibbers tunnel client streams" }.Start();
        var reader = new Thread(() => Loopback.Pump(socket, (data, from) =>
        {
            // Only the game on this PC talks to the relay.
            if (from is not IPEndPoint ip || !IPAddress.IsLoopback(ip.Address)) return;
            Volatile.Write(ref game, from);
            Interlocked.Increment(ref this.toHost);
            toHost(Host, data);
        })) { IsBackground = true, Name = "Jibbers tunnel client" };
        reader.Start();
    }

    /// <summary>A UDP socket and a TCP listener on the same loopback port (the game uses one port for both).</summary>
    static (Socket Udp, Socket Tcp) Bind(int preferredPort)
    {
        for (var attempt = 0; ; attempt++)
        {
            Socket? udp = null, tcp = null;
            try
            {
                udp = Loopback.Udp(attempt == 0 ? preferredPort : 0);
                tcp = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                tcp.Bind(new IPEndPoint(IPAddress.Loopback, ((IPEndPoint)udp.LocalEndPoint!).Port));
                tcp.Listen(16);
                return (udp, tcp);
            }
            catch (SocketException) when (attempt < 20)
            {
                udp?.Dispose();
                tcp?.Dispose();
            }
        }
    }

    void Accept()
    {
        while (true)
        {
            Socket connection;
            try { connection = listener.Accept(); }
            catch (Exception e) when (e is SocketException or ObjectDisposedException) { return; }
            connection.NoDelay = true;
            var id = (uint)Interlocked.Increment(ref nextStream);
            if (askMap is null) streams.Attach(Host, id, connection, announce: true);
            else new Thread(() => Handle(id, connection)) { IsBackground = true, Name = $"Jibbers stream {id}" }.Start();
        }
    }

    /// <summary>
    /// Looks at what the game asks first. A map request is answered from a local file when the
    /// host's server would send exactly that file; anything else (status, a map we don't have, no
    /// word from the host in time) goes to the host as before, the bytes read so far first.
    /// </summary>
    void Handle(uint id, Socket connection)
    {
        byte[] request;
        try { request = Peek(connection); }
        catch (Exception e) when (e is SocketException or ObjectDisposedException) { connection.Dispose(); return; }
        var text = MapRequests.Text(request);
        if (MapRequests.TryParse(text, out var name) && TryServeLocally(text, name, connection)) return;
        streams.Attach(Host, id, connection, announce: true, request);
    }

    /// <summary>The first bytes the game sends (a request fits in one segment; a moment more is allowed for the rest).</summary>
    static byte[] Peek(Socket connection)
    {
        var got = new MemoryStream();
        var buffer = new byte[MapRequests.MaxRequest];
        if (!connection.Poll((int)(PeekWait.TotalMilliseconds * 1000), SelectMode.SelectRead)) return [];
        do
        {
            var n = connection.Receive(buffer, 0, (int)Math.Min(buffer.Length, MapRequests.MaxRequest - got.Length), SocketFlags.None);
            if (n <= 0) break;
            got.Write(buffer, 0, n);
        } while (got.Length < MapRequests.MaxRequest && connection.Poll(20_000, SelectMode.SelectRead) && connection.Available > 0);
        return got.ToArray();
    }

    bool TryServeLocally(string request, string name, Socket connection)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        TaskCompletionSource<MapOffer?> pending;
        var ask = false;
        lock (offersGate)
        {
            if (!offers.TryGetValue(request, out pending!) || (pending.Task.IsCompleted && pending.Task.Result is null))
            {
                offers[request] = pending = new TaskCompletionSource<MapOffer?>(TaskCreationOptions.RunContinuationsAsynchronously);
                ask = true;
            }
        }
        if (ask) askMap!(request);
        if (!pending.Task.Wait(OfferWait))
        {
            log($"no word on the map {name} from the host within {OfferWait.TotalSeconds:0.#} s: downloading it from the host");
            return false;
        }
        if (pending.Task.Result is not { } offer)
        {
            log($"the host has no map info for {name}: downloading it from the host");
            return false;
        }
        var find = Task.Run(() => maps!.Find(offer.Size, offer.Sha256));
        string? path;
        try { path = find.Wait(FindWait) ? find.Result : null; }
        catch (AggregateException) { path = null; }
        if (path is null)
        {
            log(find.IsCompleted
                ? $"no local copy of {name} ({MapRequests.Megabytes(offer.Size)}): downloading it from the host"
                : $"still looking for a local copy of {name} after {FindWait.TotalSeconds:0.#} s: downloading it from the host");
            return false;
        }
        log($"map served locally: {name} {MapRequests.Megabytes(offer.Size)} from {path}");
        if (MapRequests.Serve(connection, path, offer, log)) log($"sent {name} from disk in {clock.Elapsed.TotalSeconds:0.0} s, nothing downloaded through the tunnel");
        return true;
    }

    /// <summary>The host's answer about a map request: what its server sends, or null if it can't say.</summary>
    public void MapOffered(string request, MapOffer? offer)
    {
        lock (offersGate)
        {
            if (!offers.TryGetValue(request, out var pending) || pending.Task.IsCompleted)
                offers[request] = pending = new TaskCompletionSource<MapOffer?>(TaskCreationOptions.RunContinuationsAsynchronously);
            pending.TrySetResult(offer);
        }
    }

    /// <summary>TCP connections (the map download) open through the tunnel.</summary>
    public int Streams => streams.Count;

    /// <summary>A stream frame from the host: their server's side of one of the game's TCP connections.</summary>
    public void FromHostStream(uint id, byte op, ReadOnlySpan<byte> data) => streams.Receive(Host, id, op, data, connect: null);

    public SteamId Host { get; }
    /// <summary>The port the game should connect to on 127.0.0.1.</summary>
    public int Port => ((IPEndPoint)socket.LocalEndPoint!).Port;
    /// <summary>True once the game has sent its first packet.</summary>
    public bool GameSeen => Volatile.Read(ref game) is not null;

    /// <summary>Datagrams relayed so far: from the game to the host, and from the host to the game.</summary>
    public (long ToHost, long FromHost) Traffic => (Interlocked.Read(ref toHost), Interlocked.Read(ref fromHost));

    /// <summary>A datagram from the host: on to the game.</summary>
    public void FromHost(ReadOnlySpan<byte> datagram)
    {
        Interlocked.Increment(ref fromHost);
        if (Volatile.Read(ref game) is not { } to) return;
        try { socket.SendTo(datagram, SocketFlags.None, to); }
        catch (Exception e) when (e is SocketException or ObjectDisposedException) { }
    }

    public void Dispose()
    {
        listener.Dispose();
        socket.Dispose();
        streams.Dispose();
        lock (offersGate)
            foreach (var p in offers.Values) p.TrySetResult(null);
    }
}
