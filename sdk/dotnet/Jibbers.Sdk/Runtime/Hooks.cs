using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Jibbers.Sdk.Interop;

namespace Jibbers.Sdk;

/// <summary>
/// Pre-hooks: run your code on the main thread just before a game method runs, through the
/// loader's shared hook slots.
/// </summary>
/// <remarks>
/// The loader's limits apply: instance methods returning <c>void</c> or <c>bool</c>, at most two
/// parameters, none of them <c>float</c>/<c>double</c>; 12 hooked methods in the whole game. Install
/// hooks in <see cref="JibbersMod.OnLoad"/>. For anything else (any signature, replacing or
/// skipping the original, return values) use <see cref="Detour"/>.
/// </remarks>
public static unsafe class Hooks
{
    /// <summary>A pre-hook callback: <c>(this, first argument)</c>. The argument is a pointer for reference types and structs over 8 bytes.</summary>
    public delegate void PreHook(nint self, nint arg0);

    const int Slots = 12;

    sealed class Slot
    {
        public nint Method;
        public string Label = "";
        public readonly List<(string Owner, PreHook Callback)> Callbacks = new();
        public readonly HashSet<string> Failed = new();
    }

    static readonly Slot?[] slots = new Slot?[Slots];
    static readonly object gate = new();

    /// <summary>Why the loader can't pre-hook this method, or null if it can.</summary>
    public static string? Unsupported(Il2CppMethod m)
    {
        if (m.IsNull)
        {
            return "method not found";
        }
        if (m.IsStatic)
        {
            return "static method (pre-hooks take instance methods)";
        }
        if (m.ReturnType is not ("System.Void" or "System.Boolean"))
        {
            return $"returns {m.ReturnType} (pre-hooks take void or bool methods)";
        }
        if (m.ParameterCount > 2)
        {
            return $"{m.ParameterCount} parameters (pre-hooks take at most two)";
        }
        if (m.ParameterTypes.Any(t => t is "System.Single" or "System.Double"))
        {
            return "float parameter (the loader forwards integer registers only)";
        }
        return null;
    }

    /// <summary>
    /// Runs <paramref name="callback"/> before every call of <paramref name="method"/>. Throws if the
    /// method can't be pre-hooked or the loader has no free slot. Exceptions from the callback are
    /// caught and logged once per mod.
    /// </summary>
    public static void Before(Il2CppMethod method, PreHook callback, [CallerFilePath] string owner = "")
    {
        if (Unsupported(method) is { } why)
        {
            throw new InvalidOperationException($"cannot pre-hook {method}: {why}");
        }
        var mod = ModContext.CurrentId ?? Path.GetFileNameWithoutExtension(owner);
        lock (gate)
        {
            for (var i = 0; i < Slots; i++)
            {
                if (slots[i] is { } s && s.Method == method.Ptr)
                {
                    s.Callbacks.Add((mod, callback));
                    return;
                }
            }
            var free = Array.IndexOf(slots, null);
            if (free < 0)
            {
                throw new InvalidOperationException("all C# pre-hook slots are in use; use Detour instead");
            }
            var slot = new Slot { Method = method.Ptr, Label = method.DeclaringClass.FullName + "." + method.Name };
            slot.Callbacks.Add((mod, callback));
            var rc = Runtime.Native.Api->Host->PreHook(method.Ptr, Trampolines[free]);
            if (rc != 0)
            {
                throw new InvalidOperationException($"pre-hook {slot.Label} failed ({rc}: 1 null method, 2 no free loader slot, 3 enable failed, 4 detour failed, 5 too many plugins on this method, 6 unsupported signature)");
            }
            slots[free] = slot;
        }
    }

    /// <summary>Removes every pre-hook callback of a mod (the loader keeps the detour; it just stops calling the mod).</summary>
    internal static void RemoveOwner(string mod)
    {
        lock (gate)
        {
            foreach (var s in slots)
            {
                s?.Callbacks.RemoveAll(c => c.Owner == mod);
            }
        }
    }

    static void Dispatch(int i, nint self, nint arg)
    {
        var s = slots[i];
        if (s is null)
        {
            return;
        }
        (string, PreHook)[] cbs;
        lock (gate)
        {
            cbs = s.Callbacks.ToArray();
        }
        MainThread.Enter(() =>
        {
            foreach (var (owner, cb) in cbs)
            {
                if (!ModContext.Enabled(owner))
                {
                    continue;
                }
                try
                {
                    cb(self, arg);
                }
                catch (Exception e)
                {
                    bool first;
                    lock (gate)
                    {
                        first = s.Failed.Add(owner);
                    }
                    if (first)
                    {
                        Log.Error($"{owner}: hook {s.Label}", e);
                    }
                }
            }
            return 0;
        });
    }

    [UnmanagedCallersOnly] static void T0(nint a, nint b) => Dispatch(0, a, b);
    [UnmanagedCallersOnly] static void T1(nint a, nint b) => Dispatch(1, a, b);
    [UnmanagedCallersOnly] static void T2(nint a, nint b) => Dispatch(2, a, b);
    [UnmanagedCallersOnly] static void T3(nint a, nint b) => Dispatch(3, a, b);
    [UnmanagedCallersOnly] static void T4(nint a, nint b) => Dispatch(4, a, b);
    [UnmanagedCallersOnly] static void T5(nint a, nint b) => Dispatch(5, a, b);
    [UnmanagedCallersOnly] static void T6(nint a, nint b) => Dispatch(6, a, b);
    [UnmanagedCallersOnly] static void T7(nint a, nint b) => Dispatch(7, a, b);
    [UnmanagedCallersOnly] static void T8(nint a, nint b) => Dispatch(8, a, b);
    [UnmanagedCallersOnly] static void T9(nint a, nint b) => Dispatch(9, a, b);
    [UnmanagedCallersOnly] static void T10(nint a, nint b) => Dispatch(10, a, b);
    [UnmanagedCallersOnly] static void T11(nint a, nint b) => Dispatch(11, a, b);

    static readonly nint[] Trampolines =
    {
        (nint)(delegate* unmanaged<nint, nint, void>)&T0, (nint)(delegate* unmanaged<nint, nint, void>)&T1,
        (nint)(delegate* unmanaged<nint, nint, void>)&T2, (nint)(delegate* unmanaged<nint, nint, void>)&T3,
        (nint)(delegate* unmanaged<nint, nint, void>)&T4, (nint)(delegate* unmanaged<nint, nint, void>)&T5,
        (nint)(delegate* unmanaged<nint, nint, void>)&T6, (nint)(delegate* unmanaged<nint, nint, void>)&T7,
        (nint)(delegate* unmanaged<nint, nint, void>)&T8, (nint)(delegate* unmanaged<nint, nint, void>)&T9,
        (nint)(delegate* unmanaged<nint, nint, void>)&T10, (nint)(delegate* unmanaged<nint, nint, void>)&T11,
    };
}

/// <summary>
/// A detour: replaces a native function (a game method's code, an engine or MuJoCo export) with
/// yours. Any signature, any time, no slot limit. Call <see cref="Original"/> to run the game's code.
/// </summary>
/// <example>
/// <code>
/// // A C# function with the exact native signature: (this, args..., MethodInfo*) for a game method.
/// [UnmanagedCallersOnly]
/// static void StepScene(nint self, nint method)
/// {
///     if (!paused) ((delegate* unmanaged&lt;nint, nint, void&gt;)step.Original)(self, method);
/// }
/// step = Detour.Create(Game.Physics.Scene.Methods.StepScene.NativeCode, (nint)(delegate* unmanaged&lt;nint, nint, void&gt;)&amp;StepScene);
/// step.Enable();
/// </code>
/// </example>
public sealed unsafe class Detour : IDisposable
{
    nint handle;
    readonly Delegate? keepAlive;
    /// <summary>Address that was detoured.</summary>
    public nint Target { get; }
    /// <summary>Calls the original code (cast to a <c>delegate* unmanaged</c> of the right signature).</summary>
    public nint Original { get; }
    /// <summary>Whether the detour is active.</summary>
    public bool Enabled { get; private set; }

    Detour(nint handle, nint target, nint original, Delegate? keepAlive)
    {
        this.handle = handle;
        Target = target;
        Original = original;
        this.keepAlive = keepAlive;
        lock (all)
        {
            all.Add(this);
        }
        owner = ModContext.CurrentId;
    }

    readonly string? owner;
    static readonly List<Detour> all = new();

    /// <summary>
    /// Creates a (disabled) detour of <paramref name="target"/> to <paramref name="replacement"/>,
    /// a pointer to an <c>[UnmanagedCallersOnly]</c> function with the target's exact signature.
    /// </summary>
    public static Detour Create(nint target, nint replacement)
    {
        if (!Runtime.Native.Ready)
        {
            throw new InvalidOperationException("the SDK is not running inside the game");
        }
        nint tramp;
        var h = Runtime.Native.Api->DetourCreate(target, replacement, &tramp);
        if (h == 0)
        {
            throw new InvalidOperationException(Runtime.Native.LastError());
        }
        return new Detour(h, target, tramp, null);
    }

    /// <summary>
    /// Creates a detour to a managed delegate (marshalled to a native function pointer and kept
    /// alive by the detour). Simpler than function pointers; slightly slower per call. Use
    /// <see cref="OriginalAs{T}"/> to call the original.
    /// </summary>
    public static Detour Create<T>(nint target, T replacement) where T : Delegate
    {
        var fp = Marshal.GetFunctionPointerForDelegate(replacement);
        nint tramp;
        var h = Runtime.Native.Api->DetourCreate(target, fp, &tramp);
        if (h == 0)
        {
            throw new InvalidOperationException(Runtime.Native.LastError());
        }
        return new Detour(h, target, tramp, replacement);
    }

    /// <summary>The original as a delegate of type <typeparamref name="T"/>.</summary>
    public T OriginalAs<T>() where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(Original);

    /// <summary>Activates the detour.</summary>
    public void Enable()
    {
        if (Runtime.Native.Api->DetourEnable(handle) != 0)
        {
            throw new InvalidOperationException(Runtime.Native.LastError());
        }
        Enabled = true;
    }

    /// <summary>Deactivates the detour (the original runs unchanged).</summary>
    public void Disable()
    {
        if (handle != 0 && Runtime.Native.Api->DetourDisable(handle) != 0)
        {
            throw new InvalidOperationException(Runtime.Native.LastError());
        }
        Enabled = false;
    }

    /// <summary>Removes the detour for good.</summary>
    public void Dispose()
    {
        if (handle != 0)
        {
            Runtime.Native.Api->DetourDestroy(handle);
            handle = 0;
            Enabled = false;
        }
        lock (all)
        {
            all.Remove(this);
        }
        GC.KeepAlive(keepAlive);
    }

    internal static void RemoveOwner(string mod)
    {
        Detour[] mine;
        lock (all)
        {
            mine = all.Where(d => d.owner == mod).ToArray();
        }
        foreach (var d in mine)
        {
            d.Dispose();
        }
    }
}
