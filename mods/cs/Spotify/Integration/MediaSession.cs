using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Spotify.Integration;

/// <summary>What's playing.</summary>
/// <param name="Shuffle">Shuffle on, or null when the player doesn't say.</param>
/// <param name="Repeat">0 off, 1 the song, 2 the playlist, or null when the player doesn't say.</param>
internal sealed record NowPlaying(string Title, string Artist, string Album, bool Playing, double Position, double Duration, long At, byte[]? Art, string ArtKey, bool? Shuffle, int? Repeat)
{
    /// <summary>The position now (it moves on while playing).</summary>
    public double PositionNow
    {
        get
        {
            var p = Playing ? Position + (Stopwatch.GetTimestamp() - At) / (double)Stopwatch.Frequency : Position;
            return Duration > 0 ? Math.Min(p, Duration) : p;
        }
    }
}

/// <summary>
/// What's playing, from Windows' own media controls (the system behind the volume flyout and the media
/// keys: GlobalSystemMediaTransportControls). Spotify's desktop app reports its song, artist, album
/// art, play state and position there and takes skip / back / play-pause from it, so no Spotify
/// account is needed. WinRT is reached directly (its COM interfaces by method slot, from the Windows
/// SDK's metadata), on a thread of its own, polled twice a second.
/// </summary>
internal sealed unsafe class MediaSession : IDisposable
{
    readonly Action<string> log;
    readonly Func<bool> spotifyOnly;
    readonly ConcurrentQueue<(Command Command, double Value)> commands = new();
    readonly Thread thread;
    volatile bool stop;
    volatile NowPlaying? now;

    public MediaSession(Action<string> log, Func<bool> spotifyOnly)
    {
        this.log = log;
        this.spotifyOnly = spotifyOnly;
        thread = new Thread(Run) { IsBackground = true, Name = "Spotify media" };
        thread.Start();
    }

    public NowPlaying? Now => now;

    /// <summary>Asks the player to skip / go back / play-pause / seek / shuffle / repeat. Any thread.</summary>
    public void Send(Command c, double value = 0) => commands.Enqueue((c, value));

    /// <summary>Carries out one command on the session (the media thread).</summary>
    void Do(nint s, Command c, double value)
    {
        var last = now;
        nint op = 0;
        switch (c)
        {
            case Command.Next: op = GetObject(s, 16); break; // TrySkipNextAsync
            case Command.Previous: op = GetObject(s, 17); break; // TrySkipPreviousAsync
            case Command.PlayPause: op = GetObject(s, 20); break; // TryTogglePlayPauseAsync
            case Command.SeekBy or Command.SeekTo:
                if (last is null) return;
                var to = c == Command.SeekTo ? value : last.PositionNow + value;
                if (last.Duration > 0) to = Math.Min(to, last.Duration - 1);
                Check(((delegate* unmanaged[Stdcall]<nint, long, nint*, int>)Vt(s)[24])(s, (long)(Math.Max(to, 0) * 1e7), &op), "seeking"); // TryChangePlaybackPositionAsync
                break;
            case Command.Shuffle:
                Check(((delegate* unmanaged[Stdcall]<nint, byte, nint*, int>)Vt(s)[23])(s, (byte)(last?.Shuffle == true ? 0 : 1), &op), "shuffle"); // TryChangeShuffleActiveAsync
                break;
            case Command.Repeat:
                // Off -> the playlist -> the song -> off.
                var mode = (last?.Repeat ?? 0) switch { 0 => 2, 2 => 1, _ => 0 };
                Check(((delegate* unmanaged[Stdcall]<nint, int, nint*, int>)Vt(s)[21])(s, mode, &op), "repeat"); // TryChangeAutoRepeatModeAsync
                break;
        }
        if (op == 0) return;
        try { Await(op, o => GetValue<byte>(o, 8)); }
        finally { Release(op); }
    }

    /// <summary>An IReference's value (a nullable property), or null.</summary>
    static T? Optional<T>(nint o, int slot) where T : unmanaged
    {
        nint reference;
        if (((delegate* unmanaged[Stdcall]<nint, nint*, int>)Vt(o)[slot])(o, &reference) < 0 || reference == 0) return null;
        try { return GetValue<T>(reference, 6); }
        finally { Release(reference); }
    }

    public void Dispose() => stop = true;

    // ---- WinRT plumbing ----

    [DllImport("combase.dll")] static extern int RoInitialize(int type);
    [DllImport("combase.dll")] static extern int RoGetActivationFactory(nint classId, Guid* iid, nint* factory);
    [DllImport("combase.dll", CharSet = CharSet.Unicode)] static extern int WindowsCreateString(string s, int length, nint* hstring);
    [DllImport("combase.dll")] static extern int WindowsDeleteString(nint hstring);
    [DllImport("combase.dll")] static extern char* WindowsGetStringRawBuffer(nint hstring, uint* length);

    static readonly Guid ManagerStatics = new("2050c4ee-11a0-57de-aed7-c97c70338245");
    static readonly Guid AsyncInfo = new("00000036-0000-0000-c000-000000000046");
    static readonly Guid RandomAccessStream = new("905a0fe1-bc53-11df-8c49-001e4fc686da");
    static readonly Guid InputStream = new("905a0fe2-bc53-11df-8c49-001e4fc686da");
    static readonly Guid DataReaderFactory = new("d7527847-57da-4e15-914c-06806699a098");

    static void** Vt(nint o) => *(void***)o;
    static void Check(int hr, string what) { if (hr < 0) throw new InvalidOperationException($"{what} failed (0x{hr:X8})"); }
    static void Release(nint o) { if (o != 0) ((delegate* unmanaged[Stdcall]<nint, uint>)Vt(o)[2])(o); }

    static nint Query(nint o, Guid iid)
    {
        nint result;
        Check(((delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)Vt(o)[0])(o, &iid, &result), "QueryInterface");
        return result;
    }

    /// <summary>Calls a method in <paramref name="slot"/> that returns one interface pointer.</summary>
    static nint GetObject(nint o, int slot)
    {
        nint result;
        Check(((delegate* unmanaged[Stdcall]<nint, nint*, int>)Vt(o)[slot])(o, &result), "a call");
        return result;
    }

    /// <summary>
    /// Calls a method in <paramref name="slot"/> that writes one value. The call itself is typed with
    /// <c>void*</c>, not <c>T*</c>: the game's .NET runtime rejects generic function-pointer
    /// signatures (BadImageFormatException) even where a plain test run accepts them.
    /// </summary>
    static T GetValue<T>(nint o, int slot) where T : unmanaged
    {
        T result;
        Check(((delegate* unmanaged[Stdcall]<nint, void*, int>)Vt(o)[slot])(o, &result), "a call");
        return result;
    }

    static string GetString(nint o, int slot)
    {
        nint h;
        if (((delegate* unmanaged[Stdcall]<nint, nint*, int>)Vt(o)[slot])(o, &h) < 0 || h == 0) return "";
        try
        {
            uint n;
            var p = WindowsGetStringRawBuffer(h, &n);
            return new string(p, 0, (int)n);
        }
        finally
        {
            WindowsDeleteString(h);
        }
    }

    static nint Factory(string runtimeClass, Guid iid)
    {
        nint name, factory;
        Check(WindowsCreateString(runtimeClass, runtimeClass.Length, &name), "WindowsCreateString");
        try { Check(RoGetActivationFactory(name, &iid, &factory), runtimeClass); }
        finally { WindowsDeleteString(name); }
        return factory;
    }

    /// <summary>Waits for an IAsyncOperation (polling its IAsyncInfo status) and gets its result with <paramref name="get"/>.</summary>
    static T Await<T>(nint op, Func<nint, T> get, int timeoutMs = 3000)
    {
        var info = Query(op, AsyncInfo);
        try
        {
            var deadline = Environment.TickCount64 + timeoutMs;
            while (true)
            {
                var status = GetValue<int>(info, 7); // Started 0, Completed 1, Canceled 2, Error 3
                if (status == 1) return get(op);
                if (status > 1) throw new InvalidOperationException(status == 2 ? "cancelled" : "failed");
                if (Environment.TickCount64 > deadline) throw new TimeoutException();
                Thread.Sleep(5);
            }
        }
        finally
        {
            Release(info);
        }
    }

    /// <summary>Awaits an IAsyncOperation of an object (its GetResults is slot 8) and releases the operation.</summary>
    static nint AwaitObject(nint op)
    {
        try { return Await(op, o => GetObject(o, 8)); }
        finally { Release(op); }
    }

    // ---- polling ----

    void Run()
    {
        RoInitialize(1); // multithreaded
        nint manager = 0;
        var artKey = "";
        byte[]? art = null;
        var said = false;
        while (!stop)
        {
            try
            {
                if (manager == 0)
                {
                    var statics = Factory("Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager", ManagerStatics);
                    try { manager = AwaitObject(GetObject(statics, 6)); } // RequestAsync
                    finally { Release(statics); }
                }
                Poll(manager, ref artKey, ref art);
                said = false;
            }
            catch (Exception e)
            {
                // Anything at all: an exception escaping this thread would end the game.
                now = null;
                if (!said)
                {
                    log($"media: {e.GetType().Name}: {e.Message}");
                    said = true;
                }
                if (e is not (InvalidOperationException or TimeoutException or COMException))
                {
                    // Unexpected: start again with a fresh manager after a pause.
                    manager = 0;
                    Thread.Sleep(2000);
                }
            }
            // Commands get a quick turn; the state is refreshed twice a second.
            for (var i = 0; i < 5 && commands.IsEmpty && !stop; i++)
                Thread.Sleep(100);
        }
        Release(manager);
    }

    /// <summary>The session to show: Spotify's, or (when allowed) whatever Windows thinks is current.</summary>
    nint Pick(nint manager)
    {
        var sessions = GetObject(manager, 7); // GetSessions: IVectorView of sessions
        try
        {
            var count = GetValue<uint>(sessions, 7); // Size
            for (uint i = 0; i < count; i++)
            {
                nint s;
                Check(((delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)Vt(sessions)[6])(sessions, i, &s), "GetAt");
                if (GetString(s, 6).Contains("spotify", StringComparison.OrdinalIgnoreCase)) return s; // SourceAppUserModelId
                Release(s);
            }
        }
        finally
        {
            Release(sessions);
        }
        return spotifyOnly() ? 0 : GetObject(manager, 6); // GetCurrentSession
    }

    void Poll(nint manager, ref string artKey, ref byte[]? art)
    {
        var s = Pick(manager);
        if (s == 0)
        {
            commands.Clear();
            now = null;
            return;
        }
        try
        {
            while (commands.TryDequeue(out var c))
            {
                try { Do(s, c.Command, c.Value); }
                catch (Exception e) when (e is InvalidOperationException or TimeoutException) { log($"{c.Command} didn't work: {e.Message}"); }
            }
            var props = AwaitObject(GetObject(s, 7)); // TryGetMediaPropertiesAsync
            try
            {
                string title = GetString(props, 6), artist = GetString(props, 9), album = GetString(props, 10);
                if (title.Length == 0)
                {
                    now = null;
                    return;
                }
                var key = $"{title}\u0001{artist}\u0001{album}";
                if (key != artKey)
                {
                    artKey = key;
                    art = LoadArt(props);
                }
                var playback = GetObject(s, 9); // GetPlaybackInfo
                bool playing;
                bool? shuffle;
                int? repeat;
                try
                {
                    playing = GetValue<int>(playback, 7) == 4; // PlaybackStatus: Playing
                    repeat = Optional<int>(playback, 9); // AutoRepeatMode
                    shuffle = Optional<byte>(playback, 11) is { } b ? b != 0 : null; // IsShuffleActive
                }
                finally { Release(playback); }
                double position = 0, duration = 0;
                var timeline = GetObject(s, 8); // GetTimelineProperties
                try
                {
                    var pos = GetValue<long>(timeline, 10) / 1e7; // Position
                    duration = GetValue<long>(timeline, 7) / 1e7; // EndTime
                    // Players report the position only now and then: move it on from when they did.
                    var updated = GetValue<long>(timeline, 11); // LastUpdatedTime (Windows ticks since 1601)
                    var nowTicks = DateTime.UtcNow.ToFileTimeUtc();
                    var since = playing && updated > 0 && nowTicks > updated ? (nowTicks - updated) / 1e7 : 0;
                    position = pos + Math.Min(since, 3600);
                }
                finally { Release(timeline); }
                now = new NowPlaying(title, artist, album, playing, position, duration, Stopwatch.GetTimestamp(), art, artKey, shuffle, repeat);
            }
            finally
            {
                Release(props);
            }
        }
        finally
        {
            Release(s);
        }
    }

    /// <summary>The album art's image file (JPEG or PNG), or null.</summary>
    static byte[]? LoadArt(nint props)
    {
        nint thumb = 0, stream = 0, random = 0, input = 0, factory = 0, reader = 0;
        try
        {
            thumb = GetObject(props, 15); // Thumbnail
            if (thumb == 0) return null;
            stream = AwaitObject(GetObject(thumb, 6)); // OpenReadAsync
            random = Query(stream, RandomAccessStream);
            var size = (uint)Math.Min(GetValue<ulong>(random, 6), 8 << 20); // Size
            if (size == 0) return null;
            input = Query(stream, InputStream);
            factory = Factory("Windows.Storage.Streams.DataReader", DataReaderFactory);
            Check(((delegate* unmanaged[Stdcall]<nint, nint, nint*, int>)Vt(factory)[6])(factory, input, &reader), "CreateDataReader");
            nint load;
            Check(((delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)Vt(reader)[29])(reader, size, &load), "LoadAsync");
            uint loaded;
            try { loaded = Await(load, o => GetValue<uint>(o, 8)); }
            finally { Release(load); }
            var bytes = new byte[loaded];
            fixed (byte* p = bytes)
                Check(((delegate* unmanaged[Stdcall]<nint, uint, byte*, int>)Vt(reader)[14])(reader, loaded, p), "ReadBytes");
            return bytes;
        }
        catch (Exception e) when (e is InvalidOperationException or TimeoutException)
        {
            return null;
        }
        finally
        {
            Release(reader); Release(factory); Release(input); Release(random); Release(stream); Release(thumb);
        }
    }
}
