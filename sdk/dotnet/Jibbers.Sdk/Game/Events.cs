using System.Runtime.InteropServices;
using Jibbers.Sdk.Interop;
using Jibbers.Sdk.Symbols;

namespace Jibbers.Sdk;

/// <summary>A received game event. Read fields with the event's generated properties or <see cref="Get{T}(FieldRef)"/>.</summary>
public sealed unsafe class GameEvent
{
    /// <summary>Event symbol id (<c>event.trick_performed</c>).</summary>
    public string Id { get; }
    /// <summary>The struct's data (no object header). Valid only during the callback.</summary>
    public nint Data { get; }
    /// <summary>The event's class.</summary>
    public Il2CppClass Class { get; }

    internal GameEvent(string id, nint data, Il2CppClass k)
    {
        Id = id;
        Data = data;
        Class = k;
    }

    /// <summary>A value field.</summary>
    public T Get<T>(FieldRef f) where T : unmanaged => *(T*)(Data + f.StructOffset);
    /// <summary>An object field.</summary>
    public nint GetObject(FieldRef f) => *(nint*)(Data + f.StructOffset);
    /// <summary>A string field.</summary>
    public string GetString(FieldRef f) => Il2Cpp.ReadString(GetObject(f));
    /// <summary>A value field by member path.</summary>
    public T Get<T>(string path) where T : unmanaged => *(T*)(Data + SymbolTable.Field(path).Offset - 16);
    /// <summary>The whole event as JSON (for logs).</summary>
    public string Describe(int depth = 1) => Inspect.DescribeValue(Class.Type, Data, depth).ToJsonString();
}

/// <summary>A subscription; dispose to remove it.</summary>
public sealed class Subscription : IDisposable
{
    internal int Index = -1;
    internal bool Cancelled;

    /// <summary>Removes the subscription (main thread, not from inside an event callback).</summary>
    public void Dispose()
    {
        Cancelled = true;
        if (Index >= 0)
        {
            if (MainThread.IsCurrent)
            {
                Events.Remove(Index);
            }
            else
            {
                MainThread.Later(() => Events.Remove(Index));
            }
        }
    }
}

/// <summary>
/// The game's own events (spawn, map load, tricks, replay, pause, ...) through its event bus. No
/// hook slot is used. The generated <c>Game.Event.*</c> types give typed access: prefer those.
/// </summary>
/// <remarks>
/// Callbacks run on the main thread inside the game's dispatch: keep them short. Exceptions are
/// logged once per subscription. Subscriptions are removed when the mod unloads.
/// </remarks>
public static unsafe class Events
{
    sealed class Sub
    {
        public required string Id;
        public required Il2CppClass Class;
        public required int Size;
        public required Action<GameEvent> Callback;
        public required string? Owner;
        public GameRef? List;
        public GameRef? Delegate;
        public bool Live = true;
        public bool Failed;
    }

    static readonly List<Sub> subs = new();
    static GameRef? owner;
    static readonly FieldRef handlers = new("events.bus.handlers");

    [UnmanagedCallersOnly]
    static void Trampoline(nint ctx, nint arg, nint method)
    {
        Sub? s;
        lock (subs)
        {
            s = (int)ctx < subs.Count ? subs[(int)ctx] : null;
        }
        if (s is null || !s.Live || !ModContext.Enabled(s.Owner))
        {
            return;
        }
        try
        {
            var copy = arg;
            var data = Delegates.InRegister(s.Size) ? (nint)(&copy) : arg;
            MainThread.Enter(() =>
            {
                s.Callback(new GameEvent(s.Id, data, s.Class));
                return 0;
            });
        }
        catch (Exception e)
        {
            if (!s.Failed)
            {
                s.Failed = true;
                Log.Error($"{s.Owner}: event {s.Id}", e);
            }
        }
    }

    static nint Owner()
    {
        owner ??= new GameRef(Delegates.NewOwner());
        return owner.Target;
    }

    /// <summary>The bus's listener list for an event class, created if missing.</summary>
    static nint Listeners(Il2CppClass k)
    {
        var dict = handlers.GetObject(0);
        if (dict == 0)
        {
            throw new InvalidOperationException("the game's event bus is not initialised yet");
        }
        var key = k.SystemType;
        if (Unity.Call<bool>(dict, "ContainsKey", key))
        {
            return Unity.Call(dict, "get_Item", key);
        }
        var listK = Delegates.GenericArgs(Il2Cpp.ClassOf(dict))[1];
        var list = listK.Construct();
        Unity.Call(dict, "set_Item", key, list);
        return list;
    }

    /// <summary>
    /// Calls <paramref name="callback"/> every time the game raises event <paramref name="eventId"/>.
    /// From the main thread it subscribes now; from elsewhere (e.g. <see cref="JibbersMod.OnLoad"/>)
    /// it subscribes as soon as the main thread is free.
    /// </summary>
    public static Subscription Subscribe(string eventId, Action<GameEvent> callback)
    {
        var sub = new Subscription();
        var mod = ModContext.CurrentId;
        void Add()
        {
            if (sub.Cancelled)
            {
                return;
            }
            var k = SymbolTable.Class(eventId);
            if (!k.IsValueType)
            {
                throw new ArgumentException($"{eventId} is not an event struct");
            }
            var list = Listeners(k);
            int index;
            var s = new Sub { Id = eventId, Class = k, Size = k.ValueSize, Callback = callback, Owner = mod };
            lock (subs)
            {
                index = subs.Count;
                subs.Add(s);
            }
            var d = Delegates.Native(Delegates.ActionOf(k), (nint)(delegate* unmanaged<nint, nint, nint, void>)&Trampoline, index);
            // List<(object owner, Delegate)>.Add takes the tuple by pointer.
            var tuple = stackalloc nint[2] { Owner(), d };
            Unity.Call(list, "Add", (nint)tuple);
            s.List = new GameRef(list);
            s.Delegate = new GameRef(d);
            sub.Index = index;
        }
        if (MainThread.IsCurrent)
        {
            Add();
        }
        else
        {
            MainThread.Later(Add);
        }
        return sub;
    }

    static void RemoveFromList(nint list, nint d)
    {
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
    }

    internal static void Remove(int index)
    {
        Sub s;
        lock (subs)
        {
            s = subs[index];
            if (!s.Live)
            {
                return;
            }
            s.Live = false;
        }
        if (s.List is not null && s.Delegate is not null)
        {
            RemoveFromList(s.List.Target, s.Delegate.Target);
            s.List.Dispose();
            s.Delegate.Dispose();
        }
    }

    internal static void RemoveOwner(string mod)
    {
        int[] mine;
        lock (subs)
        {
            mine = Enumerable.Range(0, subs.Count).Where(i => subs[i].Owner == mod && subs[i].Live).ToArray();
        }
        foreach (var i in mine)
        {
            Remove(i);
        }
    }

    /// <summary>Number of listeners (the game's and every mod's) for an event. Main thread.</summary>
    public static int ListenerCount(string eventId) => Unity.Call<int>(Listeners(SymbolTable.Class(eventId)), "get_Count");

    /// <summary>
    /// Sends an event to every listener, as the game's dispatch does (only listeners run; whatever else
    /// the game does when raising it does not). <paramref name="data"/> is the struct's bytes. Main thread.
    /// </summary>
    public static int Raise(string eventId, ReadOnlySpan<byte> data = default)
    {
        var k = SymbolTable.Class(eventId);
        var buf = new byte[Math.Max(8, k.ValueSize)];
        data.CopyTo(buf);
        var list = Listeners(k);
        var n = Unity.Call<int>(list, "get_Count");
        fixed (byte* b = buf)
        {
            for (var i = 0; i < n; i++)
            {
                var idx = i;
                var pair = (nint*)Il2CppApi.object_unbox(Unity.Call(list, "get_Item", (nint)(&idx)));
                Delegates.Invoke(pair[1], (nint)b);
            }
        }
        return n;
    }
}
