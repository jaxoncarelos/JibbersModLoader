using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Jibbers.Sdk;
using Jibbers.Sdk.Interop;

namespace PhoneCamera.Integration;

/// <summary>
/// Records what the phone films into an MP4 (H.264, no sound) in Videos\Jibbers Clips, with Windows'
/// own encoder (Media Foundation, reached through its COM interfaces). The main thread copies the
/// phone's screen texture out up to 30 times a second (<see cref="Capture"/>); a thread of its own
/// encodes the frames.
/// </summary>
internal sealed unsafe class ClipRecorder : IDisposable
{
    public const int Fps = 30;
    readonly Action<string> log;
    BlockingCollection<(byte[] Rgba, long Time)>? frames;
    Thread? encoder;
    Stopwatch? clock;
    long lastCapture = long.MinValue;
    GameRef? readback;

    public ClipRecorder(Action<string> log) => this.log = log;

    public bool Recording => frames is not null;

    public static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Videos", "Jibbers Clips");

    /// <summary>Starts a clip <paramref name="w"/> by <paramref name="h"/> (the phone's screen texture's size). Returns its path.</summary>
    public string Start(int w, int h)
    {
        if (Recording) throw new InvalidOperationException("already recording");
        Directory.CreateDirectory(Folder);
        var path = Path.Combine(Folder, $"Jibbers {DateTime.Now:yyyy-MM-dd HH-mm-ss}.mp4");
        var queue = new BlockingCollection<(byte[], long)>(6);
        var ready = new TaskCompletionSource<string?>();
        var thread = new Thread(() =>
        {
            try
            {
                var n = Encode(path, w, h, queue, ready);
                log($"clip saved: {path} ({n} frames)");
            }
            catch (Exception e)
            {
                ready.TrySetResult(e.Message);
                log($"recording failed: {e.Message}");
            }
        }) { IsBackground = true, Name = "PhoneCamera encoder" };
        thread.Start();
        if (!ready.Task.Wait(5000)) throw new InvalidOperationException("the video encoder didn't start");
        if (ready.Task.Result is { } error) throw new InvalidOperationException(error);
        frames = queue;
        encoder = thread;
        clock = Stopwatch.StartNew();
        lastCapture = long.MinValue;
        return path;
    }

    /// <summary>Stops the clip (the encoder finishes the file on its own thread).</summary>
    public void Stop()
    {
        frames?.CompleteAdding();
        frames = null;
        encoder = null;
        readback?.Dispose();
        readback = null;
    }

    /// <summary>Copies the phone's screen into the clip (main thread, every frame while recording).</summary>
    public void Capture(nint feed)
    {
        if (frames is null || clock is null) return;
        var now = clock.ElapsedTicks;
        if (lastCapture != long.MinValue && (now - lastCapture) * Fps * 10 < Stopwatch.Frequency * 9) return;
        lastCapture = now;
        int w = Unity.Call<int>(feed, "get_width"), h = Unity.Call<int>(feed, "get_height");

        var tk = Unity.Class("UnityEngine.Texture2D");
        if (readback is null || Unity.IsNull(readback.Target))
        {
            var t = tk.New();
            int tw = w, th = h, format = 4; // RGBA32
            var mips = (byte)0;
            tk.Method(".ctor", "System.Int32", "System.Int32", "UnityEngine.TextureFormat", "System.Boolean").Invoke(t, (nint)(&tw), (nint)(&th), (nint)(&format), (nint)(&mips));
            readback = new GameRef(t);
        }
        // RenderTexture.active = the screen; ReadPixels; the active one put back.
        var rtk = Unity.Class("UnityEngine.RenderTexture");
        var before = Unity.CallStatic(rtk, "get_active");
        Unity.CallStatic(rtk, "set_active", feed);
        try
        {
            var rect = stackalloc float[4] { 0, 0, w, h };
            int x = 0, y = 0;
            var recalc = (byte)0;
            var read = tk.Method("ReadPixels", "UnityEngine.Rect", "System.Int32", "System.Int32");
            if (!read.IsNull) read.Invoke(readback.Target, (nint)rect, (nint)(&x), (nint)(&y));
            else tk.Method("ReadPixelsImpl", "UnityEngine.Rect", "System.Int32", "System.Int32", "System.Boolean").Invoke(readback.Target, (nint)rect, (nint)(&x), (nint)(&y), (nint)(&recalc));
        }
        finally
        {
            Unity.CallStatic(rtk, "set_active", before);
        }
        var pixels = tk.Method("GetPixels32", 0).Invoke(readback.Target);
        var n = w * h * 4;
        if (pixels == 0 || Il2Cpp.ArrayLength(pixels) * 4 < n) throw new InvalidOperationException("the screen came back empty");
        var rgba = new byte[n];
        Marshal.Copy(Il2Cpp.ArrayData(pixels), rgba, 0, n);
        // A frame is dropped if the encoder is behind.
        frames.TryAdd((rgba, now * 10_000_000 / Stopwatch.Frequency));
    }

    public void Dispose() => Stop();

    // ---- the encoder thread: Media Foundation's sink writer ----

    [DllImport("ole32.dll")] static extern int CoInitializeEx(nint reserved, uint flags);
    [DllImport("ole32.dll")] static extern void CoUninitialize();
    [DllImport("mfplat.dll")] static extern int MFStartup(uint version, uint flags);
    [DllImport("mfplat.dll")] static extern int MFShutdown();
    [DllImport("mfplat.dll")] static extern int MFCreateMediaType(out nint type);
    [DllImport("mfplat.dll")] static extern int MFCreateMemoryBuffer(uint length, out nint buffer);
    [DllImport("mfplat.dll")] static extern int MFCreateSample(out nint sample);
    [DllImport("mfreadwrite.dll", CharSet = CharSet.Unicode)] static extern int MFCreateSinkWriterFromURL(string url, nint stream, nint attributes, out nint writer);

    const uint MfVersion = 0x00020070;
    static readonly Guid MajorType = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f"), Video = new("73646976-0000-0010-8000-00aa00389b71");
    static readonly Guid SubType = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5"), H264 = new("34363248-0000-0010-8000-00aa00389b71"), Rgb32 = new("00000016-0000-0010-8000-00aa00389b71");
    static readonly Guid AvgBitrate = new("20332624-fb0d-4d9e-bd0d-cbf6786c102e"), Interlace = new("e2724bb8-e676-4806-b4b2-a8d6efb44ccd");
    static readonly Guid FrameSize = new("1652c33d-d6b2-4012-b834-72030849a37d"), FrameRate = new("c459a2e8-3d2c-4e44-b132-fee5156c7bb0");
    static readonly Guid PixelAspect = new("c6376a1e-8d0a-4027-be45-6d9a0ad39bb6"), DefaultStride = new("644b4e48-1e02-4516-b0eb-c01ca9d49ac6");
    const uint Progressive = 2;

    // COM method slots (IUnknown 0-2; IMFAttributes 3-32; IMFSample from 33; IMFMediaBuffer and IMFSinkWriter from 3).
    static void** Vt(nint o) => *(void***)o;
    static void Check(int hr, string what) { if (hr < 0) throw new InvalidOperationException($"{what} failed (0x{hr:X8})"); }
    static void Release(nint o) { if (o != 0) ((delegate* unmanaged[Stdcall]<nint, uint>)Vt(o)[2])(o); }
    static void SetU32(nint a, Guid key, uint v) => Check(((delegate* unmanaged[Stdcall]<nint, Guid*, uint, int>)Vt(a)[21])(a, &key, v), "SetUINT32");
    static void SetU64(nint a, Guid key, ulong v) => Check(((delegate* unmanaged[Stdcall]<nint, Guid*, ulong, int>)Vt(a)[22])(a, &key, v), "SetUINT64");
    static void SetGuid(nint a, Guid key, Guid v) => Check(((delegate* unmanaged[Stdcall]<nint, Guid*, Guid*, int>)Vt(a)[24])(a, &key, &v), "SetGUID");

    static nint VideoType(Guid subtype, int w, int h)
    {
        Check(MFCreateMediaType(out var t), "MFCreateMediaType");
        SetGuid(t, MajorType, Video);
        SetGuid(t, SubType, subtype);
        SetU32(t, Interlace, Progressive);
        SetU64(t, FrameSize, ((ulong)w << 32) | (uint)h);
        SetU64(t, FrameRate, ((ulong)Fps << 32) | 1);
        SetU64(t, PixelAspect, (1UL << 32) | 1);
        return t;
    }

    /// <summary>Writes frames until the queue completes, then finishes the file. Says on <paramref name="ready"/> once the file is open (null) or why not.</summary>
    static long Encode(string path, int w, int h, BlockingCollection<(byte[] Rgba, long Time)> queue, TaskCompletionSource<string?> ready)
    {
        Check(CoInitializeEx(0, 0), "COM"); // multithreaded
        nint writer = 0, output = 0, input = 0;
        try
        {
            Check(MFStartup(MfVersion, 0), "Media Foundation");
            Check(MFCreateSinkWriterFromURL(path, 0, 0, out writer), "opening the file");
            output = VideoType(H264, w, h);
            SetU32(output, AvgBitrate, (uint)CamMath.Bitrate(w, h, Fps));
            uint stream;
            Check(((delegate* unmanaged[Stdcall]<nint, nint, uint*, int>)Vt(writer)[3])(writer, output, &stream), "adding the video");
            input = VideoType(Rgb32, w, h);
            SetU32(input, DefaultStride, (uint)(w * 4)); // rows top-down
            Check(((delegate* unmanaged[Stdcall]<nint, uint, nint, nint, int>)Vt(writer)[4])(writer, stream, input, 0), "the encoder");
            Check(((delegate* unmanaged[Stdcall]<nint, int>)Vt(writer)[5])(writer), "starting the file");
            ready.TrySetResult(null);

            var row = w * 4;
            var len = row * h;
            long written = 0, last = long.MinValue;
            foreach (var (rgba, time) in queue.GetConsumingEnumerable())
            {
                if (rgba.Length < len || time <= last) continue;
                Check(MFCreateMemoryBuffer((uint)len, out var buffer), "frame");
                nint sample = 0;
                try
                {
                    byte* dst;
                    Check(((delegate* unmanaged[Stdcall]<nint, byte**, uint*, uint*, int>)Vt(buffer)[3])(buffer, &dst, null, null), "frame");
                    // Unity's rows are bottom-up RGBA; the encoder takes top-down BGRA.
                    fixed (byte* src0 = rgba)
                        for (var y = 0; y < h; y++)
                        {
                            var s = src0 + (h - 1 - y) * row;
                            var d = dst + y * row;
                            for (var x = 0; x < row; x += 4)
                            {
                                d[x] = s[x + 2];
                                d[x + 1] = s[x + 1];
                                d[x + 2] = s[x];
                                d[x + 3] = 255;
                            }
                        }
                    Check(((delegate* unmanaged[Stdcall]<nint, int>)Vt(buffer)[4])(buffer), "frame");
                    Check(((delegate* unmanaged[Stdcall]<nint, uint, int>)Vt(buffer)[6])(buffer, (uint)len), "frame");
                    Check(MFCreateSample(out sample), "frame");
                    Check(((delegate* unmanaged[Stdcall]<nint, nint, int>)Vt(sample)[42])(sample, buffer), "frame");
                    Check(((delegate* unmanaged[Stdcall]<nint, long, int>)Vt(sample)[36])(sample, time), "frame");
                    Check(((delegate* unmanaged[Stdcall]<nint, long, int>)Vt(sample)[38])(sample, 10_000_000 / Fps), "frame");
                    Check(((delegate* unmanaged[Stdcall]<nint, uint, nint, int>)Vt(writer)[6])(writer, stream, sample), "writing a frame");
                }
                finally
                {
                    Release(sample);
                    Release(buffer);
                }
                last = time;
                written++;
            }
            Check(((delegate* unmanaged[Stdcall]<nint, int>)Vt(writer)[11])(writer), "finishing the file");
            return written;
        }
        finally
        {
            Release(input);
            Release(output);
            Release(writer);
            MFShutdown();
            CoUninitialize();
        }
    }

    /// <summary>For tests: encodes <paramref name="count"/> generated frames without the game.</summary>
    internal static long EncodeForTest(string path, int w, int h, int count)
    {
        var queue = new BlockingCollection<(byte[], long)>(count + 1);
        for (var i = 0; i < count; i++)
        {
            var px = new byte[w * h * 4];
            for (var k = 0; k < px.Length; k += 4)
            {
                px[k] = (byte)((k / 4 % w + i * 4) % 256);
                px[k + 1] = 80;
                px[k + 2] = 160;
                px[k + 3] = 255;
            }
            queue.Add((px, i * 10_000_000L / Fps));
        }
        queue.CompleteAdding();
        var ready = new TaskCompletionSource<string?>();
        return Encode(path, w, h, queue, ready);
    }
}
