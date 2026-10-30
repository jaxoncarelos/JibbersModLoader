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
    }

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
    readonly Socket socket, listener;
    readonly StreamTunnel streams;
    EndPoint? game;
    long toHost, fromHost;
    int nextStream;

    /// <param name="toHost">Sends a datagram to the host (unreliably).</param>
    /// <param name="reliable">Sends a stream frame to the host (reliably); false when Steam won't take it right now.</param>
    public ClientRelay(SteamId host, Action<SteamId, byte[]> toHost, int preferredPort = 0, Func<SteamId, byte[], bool>? reliable = null, Action<string>? log = null)
    {
        Host = host;
        (socket, listener) = Bind(preferredPort);
        streams = new StreamTunnel(reliable ?? ((_, _) => false), log ?? (_ => { }));
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
            streams.Attach(Host, (uint)Interlocked.Increment(ref nextStream), connection, announce: true);
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
    }
}
