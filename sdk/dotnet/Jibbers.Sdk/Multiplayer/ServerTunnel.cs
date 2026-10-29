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
    }

    readonly object gate = new();
    readonly Dictionary<SteamId, Peer> peers = new();
    readonly Action<SteamId, byte[]> toPeer;
    bool disposed;

    public HostRelay(int serverPort, Action<SteamId, byte[]> toPeer)
    {
        ServerPort = serverPort;
        this.toPeer = toPeer;
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
                var reader = new Thread(() => Loopback.Pump(socket, (data, _) => toPeer(peer, data))) { IsBackground = true, Name = $"Jibbers tunnel {peer}" };
                reader.Start();
            }
            p.LastSeen = Environment.TickCount64;
        }
        try { p.Socket.Send(datagram); }
        catch (Exception e) when (e is SocketException or ObjectDisposedException) { }
    }

    /// <summary>Closes a player's socket (they left the party or the server).</summary>
    public void Drop(SteamId peer)
    {
        lock (gate)
            if (peers.Remove(peer, out var p)) p.Socket.Dispose();
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
    }
}

/// <summary>
/// On a joining player's PC: a loopback socket the game connects to as if it were the server.
/// Whatever the game sends goes to the host over Steam, and the host's replies come back to it.
/// </summary>
internal sealed class ClientRelay : IDisposable
{
    readonly Socket socket;
    EndPoint? game;

    public ClientRelay(SteamId host, Action<SteamId, byte[]> toHost, int preferredPort = 0)
    {
        Host = host;
        try { socket = Loopback.Udp(preferredPort); }
        catch (SocketException) when (preferredPort != 0) { socket = Loopback.Udp(); }
        var reader = new Thread(() => Loopback.Pump(socket, (data, from) =>
        {
            // Only the game on this PC talks to the relay.
            if (from is not IPEndPoint ip || !IPAddress.IsLoopback(ip.Address)) return;
            Volatile.Write(ref game, from);
            toHost(Host, data);
        })) { IsBackground = true, Name = "Jibbers tunnel client" };
        reader.Start();
    }

    public SteamId Host { get; }
    /// <summary>The port the game should connect to on 127.0.0.1.</summary>
    public int Port => ((IPEndPoint)socket.LocalEndPoint!).Port;
    /// <summary>True once the game has sent its first packet.</summary>
    public bool GameSeen => Volatile.Read(ref game) is not null;

    /// <summary>A datagram from the host: on to the game.</summary>
    public void FromHost(ReadOnlySpan<byte> datagram)
    {
        if (Volatile.Read(ref game) is not { } to) return;
        try { socket.SendTo(datagram, SocketFlags.None, to); }
        catch (Exception e) when (e is SocketException or ObjectDisposedException) { }
    }

    public void Dispose() => socket.Dispose();
}
