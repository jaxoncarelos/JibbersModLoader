using System.Runtime.InteropServices;
using Jibbers.Sdk.Interop;

namespace Jibbers.Sdk;

/// <summary>A native detour with a callable, typed <see cref="Original"/> delegate.</summary>
/// <remarks>
/// Use a concrete delegate type with the exact native signature and calling convention. For IL2CPP
/// methods include the instance pointer (unless static) and trailing MethodInfo pointer. Prefer
/// <c>method.Around(original =&gt; ...)</c> for zero-argument void instance methods.
/// </remarks>
public sealed class Detour<TDelegate> : IDisposable where TDelegate : Delegate
{
    readonly Detour detour;
    readonly TDelegate original;

    Detour(Detour detour)
    {
        this.detour = detour;
        try { original = detour.OriginalAs<TDelegate>(); }
        catch { detour.Dispose(); throw; }
    }

    /// <summary>Creates a disabled detour. Both the replacement and original delegates are kept alive.</summary>
    public static Detour<TDelegate> Create(nint target, TDelegate replacement) => new(Detour.Create(target, replacement));

    /// <summary>The original function, callable with the delegate's ordinary C# arguments.</summary>
    /// <remarks>Do not retain or invoke this delegate after disposing the detour.</remarks>
    public TDelegate Original
    {
        get
        {
            ObjectDisposedException.ThrowIf(detour.IsDisposed, this);
            return original;
        }
    }
    /// <summary>The native address being intercepted.</summary>
    public nint Target => detour.Target;
    /// <summary>Whether this detour is enabled.</summary>
    public bool Enabled => detour.Enabled;
    /// <summary>Enables interception.</summary>
    public void Enable() => detour.Enable();
    /// <summary>Disables interception.</summary>
    public void Disable() => detour.Disable();
    /// <summary>Disables and releases the detour.</summary>
    public void Dispose() => detour.Dispose();
}

/// <summary>
/// Intercepts a zero-argument void instance method using a normal C# callback. Call the supplied
/// <c>original()</c> to continue the game's method, or omit it to skip that invocation.
/// </summary>
/// <remarks>
/// Created enabled by <c>MethodRef.Around</c>. Callbacks run on the intercepted method's calling
/// thread. Use this with Unity main-thread methods when accessing Unity objects. Disabled mods
/// bypass the callback. An exception disables the callback and falls through to the original
/// (unless the callback already called it), preventing exceptions from escaping into native code.
/// </remarks>
public sealed class MethodHook : IDisposable
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate void NativeInstanceAction(nint instance, nint method);

    readonly Action<nint, Action> callback;
    readonly string? owner;
    readonly Detour<NativeInstanceAction> detour;
    int failed;

    MethodHook(nint target, Action<nint, Action> callback)
    {
        this.callback = callback;
        owner = ModContext.CurrentId;
        detour = Detour<NativeInstanceAction>.Create(target, Invoke);
        try { detour.Enable(); }
        catch { detour.Dispose(); throw; }
    }

    internal static void ValidateSignature(bool isStatic, string returnType, int parameters)
    {
        if (isStatic || returnType != "System.Void" || parameters != 0)
            throw new ArgumentException("Around requires a zero-argument void instance method. Use a typed Detour<TDelegate> for other signatures.");
    }
    internal static MethodHook Create(Il2CppMethod method, Action<nint, Action> callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        if (method.IsNull) throw new ArgumentException("Cannot intercept a missing method.", nameof(method));
        ValidateSignature(method.IsStatic, method.ReturnType, method.ParameterCount);
        return Create(method.NativeCode, callback);
    }
    internal static MethodHook Create(nint target, Action<nint, Action> callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        return new(target, callback);
    }

    void Invoke(nint instance, nint method)
    {
        var called = false;
        var active = true;
        var thread = Environment.CurrentManagedThreadId;
        void Original()
        {
            if (!active || Environment.CurrentManagedThreadId != thread)
                throw new InvalidOperationException("original() can only be used synchronously on its Around callback's thread.");
            called = true;
            detour.Original(instance, method);
        }
        try
        {
            if (Volatile.Read(ref failed) != 0 || !ModContext.Enabled(owner)) Original();
            else ModContext.Run(owner, () => callback(instance, Original));
        }
        catch (Exception error)
        {
            if (Interlocked.Exchange(ref failed, 1) == 0)
            {
                try { Log.Error((owner ?? "mod") + ": Around hook disabled", error); }
                catch { /* Never let a logging failure cross the native callback boundary. */ }
            }
            if (!called)
            {
                try { Original(); }
                catch (Exception fallback)
                {
                    try { Log.Error((owner ?? "mod") + ": original method failed", fallback); }
                    catch { }
                }
            }
        }
        finally { active = false; }
    }

    /// <summary>Whether this interception is enabled. A failed callback stays bypassed.</summary>
    public bool Enabled => detour.Enabled;
    /// <summary>Enables interception.</summary>
    public void Enable() => detour.Enable();
    /// <summary>Disables interception.</summary>
    public void Disable() => detour.Disable();
    /// <summary>Disables and releases interception. The SDK also removes it when its mod unloads.</summary>
    public void Dispose() => detour.Dispose();
}
