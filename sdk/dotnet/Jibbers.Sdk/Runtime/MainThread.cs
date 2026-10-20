using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Jibbers.Sdk;

/// <summary>
/// Runs code on Unity's main thread. Every call that touches game objects (IL2CPP, Unity) belongs
/// there: inside a hook, detour or event callback (already on the main thread), or in
/// <see cref="Run{T}(Func{T})"/>.
/// </summary>
public static unsafe class MainThread
{
    [ThreadStatic] static bool onMain;

    /// <summary>True inside a hook, event or <see cref="Run{T}"/> callback.</summary>
    public static bool IsCurrent => onMain;

    internal static T Enter<T>(Func<T> f)
    {
        var was = onMain;
        onMain = true;
        try
        {
            return f();
        }
        finally
        {
            onMain = was;
        }
    }

    sealed class Job
    {
        public required Action Body;
        public Exception? Error;
    }

    [UnmanagedCallersOnly]
    static void RunJob(nint ctx)
    {
        var job = (Job)GCHandle.FromIntPtr(ctx).Target!;
        try
        {
            Enter(() =>
            {
                job.Body();
                return 0;
            });
        }
        catch (Exception e)
        {
            job.Error = e;
        }
    }

    /// <summary>
    /// Runs <paramref name="f"/> on the main thread, waits for it and returns its result. Throws
    /// what <paramref name="f"/> throws, or <see cref="TimeoutException"/> if the main thread did not
    /// pick it up (game minimised or loading). Never call it from the main thread while that thread
    /// waits for you.
    /// </summary>
    public static T Run<T>(Func<T> f)
    {
        if (onMain)
        {
            return f();
        }
        if (!Runtime.Native.Ready)
        {
            throw new InvalidOperationException("the SDK is not running inside the game");
        }
        T result = default!;
        var job = new Job { Body = () => result = f() };
        var h = GCHandle.Alloc(job);
        try
        {
            var rc = Runtime.Native.Api->Host->RunMain(&RunJob, GCHandle.ToIntPtr(h));
            if (rc != 0)
            {
                throw new TimeoutException("timed out waiting for the game's main thread (is the game minimised or loading?)");
            }
        }
        finally
        {
            h.Free();
        }
        if (job.Error is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(job.Error).Throw();
        }
        return result;
    }

    /// <summary>Runs <paramref name="a"/> on the main thread and waits for it.</summary>
    public static void Run(Action a) => Run(() =>
    {
        a();
        return 0;
    });

    /// <summary>
    /// Runs <paramref name="a"/> on the main thread from a background thread, without blocking the
    /// caller; retries while the game is busy for up to <paramref name="patience"/> (default one
    /// minute), then logs. Use it in <see cref="JibbersMod.OnLoad"/> for setup that needs the main thread.
    /// </summary>
    public static void Later(Action a, TimeSpan? patience = null, [CallerMemberName] string caller = "")
    {
        var limit = patience ?? TimeSpan.FromMinutes(1);
        new Thread(() =>
        {
            var start = DateTime.UtcNow;
            while (true)
            {
                try
                {
                    Run(a);
                    return;
                }
                catch (TimeoutException) when (DateTime.UtcNow - start < limit)
                {
                    Thread.Sleep(500);
                }
                catch (Exception e)
                {
                    Log.Error(caller, e);
                    return;
                }
            }
        }) { IsBackground = true, Name = "Jibbers MainThread.Later" }.Start();
    }
}
