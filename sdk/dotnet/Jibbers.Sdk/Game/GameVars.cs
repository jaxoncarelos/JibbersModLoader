using System.Runtime.InteropServices;
using Jibbers.Sdk.Interop;
using Jibbers.Sdk.Symbols;

namespace Jibbers.Sdk;

/// <summary>
/// The game's named settings (every option its menus bind by name: <c>shadow-distance</c>,
/// <c>follow-cam-distance</c>, <c>trick-tracker</c>, ...), in four tables with change listeners.
/// Main thread only.
/// </summary>
/// <remarks><see cref="Set(string, float)"/> and friends notify the game's listeners like its menus do but do not save; the game saves when the player changes an option.</remarks>
public static unsafe class GameVars
{
    static readonly FieldRef bools = new("game.vars.bools"), floats = new("game.vars.floats"), ints = new("game.vars.ints"), strings = new("game.vars.strings");
    static readonly FieldRef boolL = new("game.vars.bool_listeners"), floatL = new("game.vars.float_listeners"), intL = new("game.vars.int_listeners"), stringL = new("game.vars.string_listeners");

    static nint Table(FieldRef f)
    {
        var d = f.GetObject(0);
        return d == 0 ? throw new InvalidOperationException("game settings not loaded yet") : d;
    }

    static string[] Names(FieldRef f)
    {
        var dict = Table(f);
        var keys = Unity.Call(dict, "get_Keys");
        var n = Unity.Call<int>(dict, "get_Count");
        var arr = Il2Cpp.NewArray(Il2Cpp.RequireClass("mscorlib.dll", "System.String"), n);
        var zero = 0;
        Unity.Call(keys, "CopyTo", arr, (nint)(&zero));
        return Il2Cpp.ArrayObjects(arr).Select(Il2Cpp.ReadString).Order().ToArray();
    }

    static bool Has(nint dict, nint key) => Unity.Call<bool>(dict, "ContainsKey", key);

    /// <summary>Names of the bool settings.</summary>
    public static string[] BoolNames => Names(bools);
    /// <summary>Names of the float settings.</summary>
    public static string[] FloatNames => Names(floats);
    /// <summary>Names of the int settings.</summary>
    public static string[] IntNames => Names(ints);
    /// <summary>Names of the string settings.</summary>
    public static string[] StringNames => Names(strings);

    static T? GetValue<T>(FieldRef f, string name) where T : unmanaged
    {
        var dict = Table(f);
        var key = Il2Cpp.String(name);
        return Has(dict, key) ? Il2Cpp.Unbox<T>(Unity.Call(dict, "get_Item", key)) : null;
    }

    /// <summary>A bool setting, or null if there is none by that name.</summary>
    public static bool? GetBool(string name) => GetValue<bool>(bools, name);
    /// <summary>A float setting, or null.</summary>
    public static float? GetFloat(string name) => GetValue<float>(floats, name);
    /// <summary>An int setting, or null.</summary>
    public static int? GetInt(string name) => GetValue<int>(ints, name);

    /// <summary>A string setting, or null.</summary>
    public static string? GetString(string name)
    {
        var dict = Table(strings);
        var key = Il2Cpp.String(name);
        return Has(dict, key) ? Il2Cpp.ReadString(Unity.Call(dict, "get_Item", key)) : null;
    }

    static nint? ListenerList(FieldRef f, string name, bool create)
    {
        var dict = Table(f);
        var key = Il2Cpp.String(name);
        if (Has(dict, key))
        {
            return Unity.Call(dict, "get_Item", key);
        }
        if (!create)
        {
            return null;
        }
        var list = Delegates.GenericArgs(Il2Cpp.ClassOf(dict))[1].Construct();
        Unity.Call(dict, "set_Item", key, list);
        return list;
    }

    static int SetAndNotify(FieldRef table, FieldRef listeners, string name, nint arg)
    {
        Unity.Call(Table(table), "set_Item", Il2Cpp.String(name), arg);
        if (ListenerList(listeners, name, false) is not { } list)
        {
            return 0;
        }
        var n = Unity.Call<int>(list, "get_Count");
        for (var i = 0; i < n; i++)
        {
            var idx = i;
            var pair = (nint*)Il2CppApi.object_unbox(Unity.Call(list, "get_Item", (nint)(&idx)));
            Delegates.Invoke(pair[1], arg);
        }
        return n;
    }

    /// <summary>Sets a bool setting and notifies the game. Returns the number of listeners notified.</summary>
    public static int Set(string name, bool value)
    {
        var v = value;
        return SetAndNotify(bools, boolL, name, (nint)(&v));
    }

    /// <summary>Sets a float setting and notifies the game.</summary>
    public static int Set(string name, float value) => SetAndNotify(floats, floatL, name, (nint)(&value));
    /// <summary>Sets an int setting and notifies the game.</summary>
    public static int Set(string name, int value) => SetAndNotify(ints, intL, name, (nint)(&value));
    /// <summary>Sets a string setting and notifies the game.</summary>
    public static int Set(string name, string value) => SetAndNotify(strings, stringL, name, Il2Cpp.String(value));

    sealed class WatchEntry
    {
        public required string Name;
        public required Delegate Callback;
        public required string? Owner;
        public GameRef? List, Del;
        public bool Live = true;
    }

    static readonly List<WatchEntry> watches = new();
    static GameRef? owner;

    static void Deliver(nint ctx, Action<WatchEntry> call)
    {
        WatchEntry? w;
        lock (watches)
        {
            w = (int)ctx < watches.Count ? watches[(int)ctx] : null;
        }
        if (w is null || !w.Live || !ModContext.Enabled(w.Owner))
        {
            return;
        }
        try
        {
            MainThread.Enter(() =>
            {
                ModContext.Run(w.Owner, () => call(w));
                return 0;
            });
        }
        catch (Exception e)
        {
            Log.Error($"{w.Owner}: watch {w.Name}", e);
        }
    }

    [UnmanagedCallersOnly] static void OnBool(nint ctx, byte v, nint m) => Deliver(ctx, w => ((Action<bool>)w.Callback)(v != 0));
    [UnmanagedCallersOnly] static void OnFloat(nint ctx, float v, nint m) => Deliver(ctx, w => ((Action<float>)w.Callback)(v));
    [UnmanagedCallersOnly] static void OnInt(nint ctx, int v, nint m) => Deliver(ctx, w => ((Action<int>)w.Callback)(v));
    [UnmanagedCallersOnly] static void OnString(nint ctx, nint v, nint m) => Deliver(ctx, w => ((Action<string>)w.Callback)(Il2Cpp.ReadString(v)));

    static void AddWatch(FieldRef listeners, string name, Delegate cb, nint fn)
    {
        var mod = ModContext.CurrentId;
        void Add()
        {
            var list = ListenerList(listeners, name, true)!.Value;
            var tuple = Delegates.GenericArgs(Il2Cpp.ClassOf(list))[0];
            var action = Delegates.GenericArgs(tuple)[1];
            int index;
            var w = new WatchEntry { Name = name, Callback = cb, Owner = mod };
            lock (watches)
            {
                index = watches.Count;
                watches.Add(w);
            }
            var d = Delegates.Native(action, fn, index);
            owner ??= new GameRef(Delegates.NewOwner());
            var pair = stackalloc nint[2] { owner.Target, d };
            Unity.Call(list, "Add", (nint)pair);
            w.List = new GameRef(list);
            w.Del = new GameRef(d);
        }
        if (MainThread.IsCurrent)
        {
            Add();
        }
        else
        {
            MainThread.Later(Add);
        }
    }

    /// <summary>Calls <paramref name="onChange"/> whenever the bool setting changes.</summary>
    public static void Watch(string name, Action<bool> onChange) => AddWatch(boolL, name, onChange, (nint)(delegate* unmanaged<nint, byte, nint, void>)&OnBool);
    /// <summary>Calls <paramref name="onChange"/> whenever the float setting changes.</summary>
    public static void Watch(string name, Action<float> onChange) => AddWatch(floatL, name, onChange, (nint)(delegate* unmanaged<nint, float, nint, void>)&OnFloat);
    /// <summary>Calls <paramref name="onChange"/> whenever the int setting changes.</summary>
    public static void Watch(string name, Action<int> onChange) => AddWatch(intL, name, onChange, (nint)(delegate* unmanaged<nint, int, nint, void>)&OnInt);
    /// <summary>Calls <paramref name="onChange"/> whenever the string setting changes.</summary>
    public static void Watch(string name, Action<string> onChange) => AddWatch(stringL, name, onChange, (nint)(delegate* unmanaged<nint, nint, nint, void>)&OnString);

    internal static void RemoveOwner(string mod)
    {
        WatchEntry[] mine;
        lock (watches)
        {
            mine = watches.Where(w => w.Owner == mod && w.Live).ToArray();
            foreach (var w in mine)
            {
                w.Live = false;
            }
        }
        foreach (var w in mine)
        {
            if (w.List is null || w.Del is null)
            {
                continue;
            }
            var list = w.List.Target;
            var d = w.Del.Target;
            var n = Unity.Call<int>(list, "get_Count");
            for (var i = n - 1; i >= 0; i--)
            {
                var idx = i;
                var pair = (nint*)Il2CppApi.object_unbox(Unity.Call(list, "get_Item", (nint)(&idx)));
                if (pair[1] == d)
                {
                    Unity.Call(list, "RemoveAt", (nint)(&idx));
                }
            }
            w.List.Dispose();
            w.Del.Dispose();
        }
    }
}
