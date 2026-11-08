using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Jibbers.Sdk.Multiplayer;

/// <summary>
/// What the host's server answers to one map request, measured by the host from the exact bytes
/// its server serves: <see cref="Head"/> (anything before the map, usually nothing) then
/// <see cref="Size"/> bytes whose SHA-256 is <see cref="Sha256"/>. A joiner holding a file with that
/// hash can answer the request itself, byte for byte as the server would.
/// </summary>
internal sealed record MapOffer(string Request, string Name, long Size, string Sha256, byte[] Head)
{
    public JsonObject ToJson() => new()
    {
        ["t"] = "map",
        ["req"] = Request,
        ["name"] = Name,
        ["size"] = Size,
        ["sha256"] = Sha256,
        ["head"] = Convert.ToBase64String(Head),
    };

    /// <summary>The offer in a "map" message, or null (the host has none for that request, or the message is malformed).</summary>
    public static MapOffer? FromJson(JsonNode? json)
    {
        try
        {
            var req = json?["req"]?.GetValue<string>();
            var sha = json?["sha256"]?.GetValue<string>();
            var size = json?["size"]?.GetValue<long>() ?? 0;
            if (req is null || !MapRequests.TryParse(req, out var name) || sha is not { Length: 64 } || !sha.All(Uri.IsHexDigit) || size <= 0) return null;
            var head = Convert.FromBase64String(json?["head"]?.GetValue<string>() ?? "");
            if (head.Length > MapRequests.MaxHead) return null;
            return new MapOffer(req, name, size, sha.ToLowerInvariant(), head);
        }
        catch (Exception e) when (e is FormatException or InvalidOperationException) { return null; }
    }
}

/// <summary>The game's map download: a joining game sends <c>GET /map/&lt;name&gt;</c> on a TCP connection to the server's port.</summary>
internal static class MapRequests
{
    public const string Prefix = "GET /map/";
    public const int MaxRequest = 1024;
    /// <summary>Most bytes before the map body taken as framing (an HTTP-style header).</summary>
    public const int MaxHead = 16 * 1024;

    /// <summary>Whether <paramref name="request"/> is a map request; <paramref name="name"/> is the map it names (for logs).</summary>
    public static bool TryParse(string request, out string name)
    {
        name = "";
        if (request.Length > MaxRequest || !request.StartsWith(Prefix, StringComparison.Ordinal)) return false;
        var rest = request[Prefix.Length..];
        var end = rest.IndexOfAny(['\r', '\n', '\0']);
        name = (end < 0 ? rest : rest[..end]).Trim();
        return name.Length > 0;
    }

    /// <summary>The request as text, byte for byte (Latin-1 maps every byte to one char and back).</summary>
    public static string Text(ReadOnlySpan<byte> bytes) => Encoding.Latin1.GetString(bytes);
    public static byte[] Bytes(string request) => Encoding.Latin1.GetBytes(request);

    public static string Megabytes(long bytes) => bytes >= 10 << 20 ? $"{bytes / (1024.0 * 1024):0} MB" : $"{bytes / (1024.0 * 1024):0.0} MB";

    /// <summary>
    /// Sends <paramref name="request"/> to the game's server on this PC and measures the whole
    /// answer, up to the server closing the connection. Null if it didn't answer completely.
    /// An answer that starts like an HTTP response has its header kept apart as <see cref="MapOffer.Head"/>.
    /// </summary>
    public static MapOffer? Probe(int port, string request, CancellationToken cancel, TimeSpan? timeout = null)
    {
        if (!TryParse(request, out var name)) return null;
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        using var stop = cancel.Register(() => socket.Dispose());
        var limit = Stopwatch.StartNew();
        var giveUp = timeout ?? TimeSpan.FromMinutes(2);
        socket.ReceiveTimeout = (int)Math.Min(giveUp.TotalMilliseconds, 30000);
        socket.Connect(new IPEndPoint(IPAddress.Loopback, port));
        socket.Send(Bytes(request));
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var first = new MemoryStream();
        byte[]? head = null;
        long body = 0;
        var buffer = new byte[1 << 20];
        while (true)
        {
            if (limit.Elapsed > giveUp) return null;
            var n = socket.Receive(buffer);
            if (n <= 0) break; // the server closed: that's the whole answer
            if (head is null)
            {
                first.Write(buffer, 0, n);
                if (first.Length < MaxHead) continue;
                (head, body) = Split(first.ToArray(), sha);
            }
            else
            {
                sha.AppendData(buffer, 0, n);
                body += n;
            }
        }
        if (head is null) (head, body) = Split(first.ToArray(), sha);
        if (body <= 0) return null;
        return new MapOffer(request, name, body, Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant(), head);
    }

    /// <summary>The start of an answer: its header (empty unless it looks like HTTP), the rest hashed as body.</summary>
    static (byte[] Head, long Body) Split(byte[] start, IncrementalHash sha)
    {
        var headLength = 0;
        if (start.AsSpan().StartsWith("HTTP/"u8))
        {
            var end = start.AsSpan(0, Math.Min(start.Length, MaxHead)).IndexOf("\r\n\r\n"u8);
            if (end >= 0) headLength = end + 4;
        }
        sha.AppendData(start, headLength, start.Length - headLength);
        return (start[..headLength], start.Length - headLength);
    }

    /// <summary>
    /// Answers the game's map request from a local file, as the server would: the offer's head,
    /// the file's bytes, then the connection closed. The file is hashed as it goes out and the last
    /// chunk is only sent once the whole file matches, so the game never gets a complete wrong map;
    /// on a mismatch the connection is reset instead. True if the map went out.
    /// </summary>
    public static bool Serve(Socket game, string path, MapOffer offer, Action<string> log)
    {
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, FileOptions.SequentialScan);
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            if (file.Length < offer.Size) return Abort(game, log, $"{path} is shorter than the server's map");
            if (offer.Head.Length > 0) game.Send(offer.Head);
            var buffer = new byte[1 << 20];
            long left = offer.Size;
            while (left > 0)
            {
                var n = file.Read(buffer, 0, (int)Math.Min(buffer.Length, left));
                if (n <= 0) return Abort(game, log, $"{path} ended early");
                sha.AppendData(buffer, 0, n);
                left -= n;
                if (left == 0 && Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant() != offer.Sha256)
                    return Abort(game, log, $"{path} changed while it was being sent");
                SendAll(game, buffer.AsSpan(0, n));
            }
            // Done, as the server is: the game reads to the end and closes its side.
            game.Shutdown(SocketShutdown.Send);
            game.ReceiveTimeout = 30000;
            var drain = new byte[256];
            try { while (game.Receive(drain) > 0) { } }
            catch (SocketException) { }
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or SocketException or ObjectDisposedException)
        {
            return Abort(game, log, $"serving {path} failed: {e.Message}");
        }
        finally { game.Dispose(); }
    }

    static void SendAll(Socket s, ReadOnlySpan<byte> data)
    {
        while (data.Length > 0) data = data[s.Send(data)..];
    }

    static bool Abort(Socket game, Action<string> log, string why)
    {
        log($"map not served locally: {why} (connection reset)");
        try
        {
            game.LingerState = new LingerOption(true, 0);
            game.Close();
        }
        catch (Exception e) when (e is SocketException or ObjectDisposedException) { }
        return false;
    }
}

/// <summary>
/// On the host: what the game's server answers to each map request, measured once and reused for
/// a little while (several friends joining together don't make it read the map again each).
/// </summary>
internal sealed class MapOffers(int serverPort, Action<string> log) : IDisposable
{
    static readonly TimeSpan Reuse = TimeSpan.FromSeconds(10);
    readonly object gate = new();
    readonly Dictionary<string, (Task<MapOffer?> Task, long At)> known = new();
    readonly CancellationTokenSource stop = new();

    public Task<MapOffer?> Get(string request)
    {
        lock (gate)
        {
            if (known.TryGetValue(request, out var k) && (!k.Task.IsCompleted || (k.Task.Result is not null && Environment.TickCount64 - k.At < Reuse.TotalMilliseconds)))
                return k.Task;
            var task = Task.Run(() => Measure(request));
            known[request] = (task, Environment.TickCount64);
            if (known.Count > 16) foreach (var old in known.Where(kv => kv.Value.Task.IsCompleted).Select(kv => kv.Key).ToList()) if (old != request) known.Remove(old);
            return task;
        }
    }

    MapOffer? Measure(string request)
    {
        var clock = Stopwatch.StartNew();
        MapRequests.TryParse(request, out var name);
        try
        {
            var offer = MapRequests.Probe(serverPort, request, stop.Token);
            log(offer is null
                ? $"the server gave no map for {name}"
                : $"map {name} as the server serves it: {MapRequests.Megabytes(offer.Size)}, sha256 {offer.Sha256[..12]}…{(offer.Head.Length > 0 ? $" after a {offer.Head.Length}-byte header" : "")} (read over loopback in {clock.Elapsed.TotalSeconds:0.0} s)");
            return offer;
        }
        catch (Exception e) // never in the way: no offer means the map downloads as before
        {
            if (!stop.IsCancellationRequested) log($"couldn't read the map {name} from the server: {e.Message}");
            return null;
        }
    }

    public void Dispose() => stop.Cancel();
}

/// <summary>
/// On a joining player's PC: the map files they already have, to find one byte-identical to the
/// server's map. Files are matched by size first, then SHA-256. Hashes are kept in a small JSON
/// file keyed by path, size and modification time, so a big maps folder isn't read again every join.
/// </summary>
internal sealed class LocalMapIndex
{
    const int MaxFiles = 20000, MaxEntries = 5000;
    readonly IReadOnlyList<string> folders;
    readonly string? cachePath;
    readonly object gate = new(), saving = new();
    Dictionary<string, (long Size, long Mtime, string Sha)>? cache;

    /// <param name="folders">Folders searched (with their subfolders) for map files.</param>
    /// <param name="cachePath">Where computed hashes are kept (null: in memory only).</param>
    public LocalMapIndex(IReadOnlyList<string> folders, string? cachePath)
    {
        this.folders = folders;
        this.cachePath = cachePath;
    }

    /// <summary>The game's maps folder and the hash cache for this PC.</summary>
    public static LocalMapIndex ForThisPc()
    {
        var maps = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Jibbers", "Maps");
        var cache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Jibbers", "PlayTogether", "map-hashes.json");
        return new LocalMapIndex([maps], cache);
    }

    /// <summary>A file of exactly <paramref name="size"/> bytes hashing to <paramref name="sha256"/> (lowercase hex), or null. Hashes files as needed: call off the main thread.</summary>
    public string? Find(long size, string sha256, CancellationToken cancel = default)
    {
        Load();
        var candidates = new List<FileInfo>();
        foreach (var folder in folders)
        {
            if (!Directory.Exists(folder)) continue;
            try
            {
                var opts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.Hidden | FileAttributes.System };
                foreach (var f in new DirectoryInfo(folder).EnumerateFiles("*", opts).Take(MaxFiles))
                    if (f.Length == size) candidates.Add(f);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        // Files already known to match first, then .jbrmap, then the rest.
        candidates = candidates.OrderBy(f => Known(f) == sha256 ? 0 : f.Extension.Equals(".jbrmap", StringComparison.OrdinalIgnoreCase) ? 1 : 2).ToList();
        var hashed = false;
        try
        {
            foreach (var f in candidates)
            {
                cancel.ThrowIfCancellationRequested();
                var sha = Known(f);
                if (sha is null)
                {
                    sha = Hash(f, cancel);
                    if (sha is null) continue;
                    lock (gate) cache![f.FullName] = (f.Length, f.LastWriteTimeUtc.Ticks, sha);
                    hashed = true;
                }
                if (sha == sha256) return f.FullName;
            }
            return null;
        }
        finally
        {
            if (hashed) Save();
        }
    }

    string? Known(FileInfo f)
    {
        lock (gate)
            return cache!.TryGetValue(f.FullName, out var e) && e.Size == f.Length && e.Mtime == f.LastWriteTimeUtc.Ticks ? e.Sha : null;
    }

    static string? Hash(FileInfo f, CancellationToken cancel)
    {
        try
        {
            using var file = new FileStream(f.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, FileOptions.SequentialScan);
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[1 << 20];
            int n;
            while ((n = file.Read(buffer)) > 0)
            {
                cancel.ThrowIfCancellationRequested();
                sha.AppendData(buffer, 0, n);
            }
            return Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    void Load()
    {
        lock (gate)
        {
            if (cache is not null) return;
            cache = new(StringComparer.OrdinalIgnoreCase);
            if (cachePath is null || !File.Exists(cachePath)) return;
            try
            {
                if (JsonNode.Parse(File.ReadAllText(cachePath))?["files"] is not JsonArray files) return;
                foreach (var e in files)
                {
                    var path = e?["path"]?.GetValue<string>();
                    var sha = e?["sha256"]?.GetValue<string>();
                    if (path is null || sha is not { Length: 64 }) continue;
                    cache[path] = (e!["size"]?.GetValue<long>() ?? -1, e["mtime"]?.GetValue<long>() ?? -1, sha);
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException or FormatException) { }
        }
    }

    void Save()
    {
        if (cachePath is null) return;
        JsonArray files;
        lock (gate)
        {
            foreach (var gone in cache!.Keys.Where(p => !File.Exists(p)).ToList()) cache.Remove(gone);
            files = new JsonArray(cache.Take(MaxEntries).Select(kv => (JsonNode)new JsonObject
            {
                ["path"] = kv.Key, ["size"] = kv.Value.Size, ["mtime"] = kv.Value.Mtime, ["sha256"] = kv.Value.Sha,
            }).ToArray());
        }
        lock (saving)
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
            var temp = cachePath + ".tmp";
            File.WriteAllText(temp, new JsonObject { ["files"] = files }.ToJsonString());
            File.Move(temp, cachePath, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
