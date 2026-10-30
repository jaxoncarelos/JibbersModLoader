using System.Buffers.Binary;
using System.Net.Sockets;

namespace Jibbers.Sdk.Multiplayer;

/// <summary>
/// TCP connections carried over the party channel, beside the game's UDP traffic. The game's
/// server also serves its map (when it isn't on mod.io) and its status over TCP on the game's
/// port; a joining game fetches the map from the address it joined, which through the tunnel is
/// 127.0.0.1, so those connections have to travel through the tunnel too.
/// </summary>
/// <remarks>
/// A frame is <see cref="ServerSession.Stream"/>, the connection id (4 bytes), an op
/// (<see cref="Open"/>, <see cref="Data"/>, <see cref="Close"/>), then the bytes. Frames go
/// reliably and in order. When Steam's send buffer is full the send fails; the connection then
/// waits and tries again, and stops reading its socket meanwhile, so a large map streams at the
/// pace Steam takes it.
/// </remarks>
internal sealed class StreamTunnel : IDisposable
{
    public const byte Open = 0, Data = 1, Close = 2;
    /// <summary>Bytes per frame (a party message holds 64 KB).</summary>
    public const int Chunk = 16 * 1024;
    const int Header = 6;
    static readonly TimeSpan SendGiveUp = TimeSpan.FromSeconds(30);

    sealed class Pipe(Socket socket)
    {
        public readonly Socket Socket = socket;
        public long Sent, Received;
        public bool LocalDone, RemoteDone;
    }

    readonly object gate = new();
    readonly Dictionary<(SteamId Peer, uint Id), Pipe> pipes = new();
    readonly Func<SteamId, byte[], bool> send;
    readonly Action<string> log;
    bool disposed;

    /// <param name="send">Sends a frame reliably; false when Steam won't take it right now.</param>
    public StreamTunnel(Func<SteamId, byte[], bool> send, Action<string> log)
    {
        this.send = send;
        this.log = log;
    }

    public static byte[] Frame(uint id, byte op, ReadOnlySpan<byte> data = default)
    {
        var frame = new byte[Header + data.Length];
        frame[0] = ServerSession.Stream;
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(1), id);
        frame[5] = op;
        data.CopyTo(frame.AsSpan(Header));
        return frame;
    }

    public static bool TryParse(ReadOnlySpan<byte> payload, out uint id, out byte op, out ReadOnlySpan<byte> data)
    {
        id = 0;
        op = 0;
        data = default;
        if (payload.Length < Header || payload[0] != ServerSession.Stream) return false;
        id = BinaryPrimitives.ReadUInt32LittleEndian(payload[1..]);
        op = payload[5];
        data = payload[Header..];
        return op <= Close;
    }

    /// <summary>Connections open now.</summary>
    public int Count
    {
        get { lock (gate) return pipes.Count; }
    }

    /// <summary>
    /// Carries a connection made on this PC: tells <paramref name="peer"/> to open its end
    /// (unless <paramref name="announce"/> is false: they opened it), then pumps what the socket reads.
    /// </summary>
    public void Attach(SteamId peer, uint id, Socket socket, bool announce)
    {
        var pipe = new Pipe(socket);
        // Bytes from the other side are written on the party thread, which also carries the
        // game's UDP traffic: a roomy buffer keeps a slow reader from holding that up.
        try { socket.SendBufferSize = 4 * 1024 * 1024; }
        catch (SocketException) { }
        lock (gate)
        {
            if (disposed) { socket.Dispose(); return; }
            pipes[(peer, id)] = pipe;
        }
        if (announce && !Send(peer, Frame(id, Open), pipe)) { Remove(peer, id); return; }
        new Thread(() => Pump(peer, id, pipe)) { IsBackground = true, Name = $"Jibbers stream {id}" }.Start();
    }

    void Pump(SteamId peer, uint id, Pipe pipe)
    {
        var buffer = new byte[Chunk];
        while (true)
        {
            int n;
            try { n = pipe.Socket.Receive(buffer); }
            catch (Exception e) when (e is SocketException or ObjectDisposedException) { n = 0; }
            if (n <= 0) break;
            if (!Send(peer, Frame(id, Data, buffer.AsSpan(0, n)), pipe)) { Remove(peer, id); return; }
            Interlocked.Add(ref pipe.Sent, n);
        }
        Send(peer, Frame(id, Close), pipe);
        bool finished;
        lock (gate)
        {
            pipe.LocalDone = true;
            finished = pipe.RemoteDone;
        }
        if (finished) Remove(peer, id);
    }

    /// <summary>A frame from <paramref name="peer"/>. <paramref name="connect"/> opens this PC's end when they opened theirs (null if this side doesn't accept).</summary>
    public void Receive(SteamId peer, uint id, byte op, ReadOnlySpan<byte> data, Func<Socket?>? connect)
    {
        Pipe? pipe;
        lock (gate) pipes.TryGetValue((peer, id), out pipe);
        switch (op)
        {
            case Open when pipe is null:
                Socket? socket = null;
                try { socket = connect?.Invoke(); }
                catch (SocketException e) { log($"couldn't open a connection to the game's server for {peer}: {e.SocketErrorCode}"); }
                if (socket is null) { send(peer, Frame(id, Close)); return; }
                Attach(peer, id, socket, announce: false);
                break;
            case Data when pipe is not null:
                try
                {
                    pipe.Socket.Send(data);
                    Interlocked.Add(ref pipe.Received, data.Length);
                }
                catch (Exception e) when (e is SocketException or ObjectDisposedException) { Remove(peer, id); send(peer, Frame(id, Close)); }
                break;
            case Close when pipe is not null:
                bool finished;
                lock (gate)
                {
                    pipe.RemoteDone = true;
                    finished = pipe.LocalDone;
                }
                // Their side is done sending: this side's reader sees the end of the stream.
                try { pipe.Socket.Shutdown(SocketShutdown.Send); }
                catch (Exception e) when (e is SocketException or ObjectDisposedException) { }
                if (finished) Remove(peer, id);
                break;
        }
    }

    bool Send(SteamId peer, byte[] frame, Pipe pipe)
    {
        var giveUp = Environment.TickCount64 + (long)SendGiveUp.TotalMilliseconds;
        while (!send(peer, frame))
        {
            lock (gate)
                if (disposed || !pipes.ContainsValue(pipe)) return false;
            if (Environment.TickCount64 > giveUp)
            {
                log("a connection through the tunnel stalled for 30 s and was closed");
                return false;
            }
            Thread.Sleep(5);
        }
        return true;
    }

    void Remove(SteamId peer, uint id)
    {
        Pipe? pipe;
        lock (gate)
            if (!pipes.Remove((peer, id), out pipe)) return;
        log($"connection {id} through the tunnel closed ({pipe.Sent} bytes sent, {pipe.Received} received)");
        pipe.Socket.Dispose();
    }

    /// <summary>Closes every connection of <paramref name="peer"/>.</summary>
    public void Drop(SteamId peer)
    {
        List<(SteamId, uint)> theirs;
        lock (gate) theirs = pipes.Keys.Where(k => k.Peer == peer).ToList();
        foreach (var (p, id) in theirs) Remove(p, id);
    }

    public void Dispose()
    {
        List<Pipe> all;
        lock (gate)
        {
            disposed = true;
            all = pipes.Values.ToList();
            pipes.Clear();
        }
        foreach (var p in all) p.Socket.Dispose();
    }
}
