using System.Runtime.InteropServices;
using Jibbers.Sdk.Interop;

namespace Jibbers.Sdk;

/// <summary>Windows virtual-key codes (keyboard and mouse buttons).</summary>
public enum Key
{
    MouseLeft = 0x01, MouseRight = 0x02, MouseMiddle = 0x04, Mouse4 = 0x05, Mouse5 = 0x06,
    Backspace = 0x08, Tab = 0x09, Enter = 0x0D, Shift = 0x10, Control = 0x11, Alt = 0x12, Pause = 0x13, CapsLock = 0x14,
    Escape = 0x1B, Space = 0x20, PageUp = 0x21, PageDown = 0x22, End = 0x23, Home = 0x24,
    Left = 0x25, Up = 0x26, Right = 0x27, Down = 0x28, Insert = 0x2D, Delete = 0x2E,
    D0 = 0x30, D1, D2, D3, D4, D5, D6, D7, D8, D9,
    A = 0x41, B, C, D, E, F, G, H, I, J, K, L, M, N, O, P, Q, R, S, T, U, V, W, X, Y, Z,
    Num0 = 0x60, Num1, Num2, Num3, Num4, Num5, Num6, Num7, Num8, Num9, Multiply, Add, Separator, Subtract, Decimal, Divide,
    F1 = 0x70, F2, F3, F4, F5, F6, F7, F8, F9, F10, F11, F12,
    LeftShift = 0xA0, RightShift, LeftControl, RightControl, LeftAlt, RightAlt,
    Semicolon = 0xBA, Plus, Comma, Minus, Period, Slash, Backquote,
    LeftBracket = 0xDB, Backslash, RightBracket, Quote,
}

/// <summary>
/// Input for mods: keys and mouse buttons (with press/release edges and live-rebindable bindings),
/// game focus, text-field focus, and the game's own Rewired actions (controller and keyboard
/// bindings by action name).
/// </summary>
/// <remarks>
/// Binding callbacks run on the main thread, only while the game window has focus, the player is not
/// typing in a text field, and the mod is enabled (unless the binding opts into
/// <see cref="Handle.AllowWhenDisabled"/>). Polling runs on a background thread at ~120 Hz;
/// call the Is* queries from anywhere.
/// </remarks>
public static unsafe class Input
{
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vk);
    [DllImport("user32.dll")] static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(nint hwnd, out uint pid);
    [DllImport("user32.dll")] static extern bool GetCursorPos(out Point p);

    [StructLayout(LayoutKind.Sequential)]
    struct Point
    {
        public int X, Y;
    }

    /// <summary>Whether the key or button is held now (any thread; ignores focus).</summary>
    public static bool IsDown(Key key) => IsDown((int)key);

    /// <summary>Whether a virtual-key code is held now.</summary>
    public static bool IsDown(int vk) => vk is > 0 and < 256 && (GetAsyncKeyState(vk) & 0x8000) != 0;

    /// <summary>Whether the game window is in the foreground.</summary>
    public static bool GameFocused
    {
        get
        {
            var w = GetForegroundWindow();
            return w != 0 && GetWindowThreadProcessId(w, out var pid) != 0 && pid == Environment.ProcessId;
        }
    }

    /// <summary>The mouse position on screen (pixels).</summary>
    public static (int X, int Y) MouseScreen => GetCursorPos(out var p) ? (p.X, p.Y) : (0, 0);

    static Il2CppClass inputField;
    static nint[] fields = Array.Empty<nint>();
    static DateTime fieldsAt;

    /// <summary>Whether the player is typing in a text field (chat, a name box). Main thread.</summary>
    public static bool Typing
    {
        get
        {
            if (inputField.IsNull)
            {
                inputField = Il2Cpp.Class("Unity.TextMeshPro.dll", "TMPro", "TMP_InputField");
                if (inputField.IsNull)
                {
                    return false;
                }
            }
            if (DateTime.UtcNow - fieldsAt > TimeSpan.FromSeconds(1))
            {
                fields = Unity.FindObjects(inputField);
                fieldsAt = DateTime.UtcNow;
            }
            foreach (var f in fields)
            {
                if (!Unity.IsNull(f) && Unity.Call<bool>(f, "get_isFocused"))
                {
                    return true;
                }
            }
            return false;
        }
    }

    /// <summary>When a binding fires.</summary>
    public enum Trigger
    {
        /// <summary>Once when pressed.</summary>
        Pressed,
        /// <summary>Once when released.</summary>
        Released,
        /// <summary>Every poll while held.</summary>
        Held,
    }

    internal sealed class Binding
    {
        public required Func<int> Key;
        public required Func<bool> Modifiers;
        public required Trigger Trigger;
        public required Action Callback;
        public required string? Owner;
        public bool WasDown;
        public bool Removed;
        public volatile bool RunWhenDisabled;

        internal void Dispatch()
        {
            if (!Removed && (RunWhenDisabled || ModContext.Enabled(Owner)))
                ModContext.Run(Owner, Callback);
        }
    }

    static readonly List<Binding> bindings = new();
    static Thread? poller;

    /// <summary>A binding; dispose to remove it.</summary>
    public sealed class Handle : IDisposable
    {
        internal Binding? B;

        /// <summary>
        /// Keeps this binding active while its mod is disabled, for example to turn it back on.
        /// Focus, text-field and unload checks still apply.
        /// </summary>
        public Handle AllowWhenDisabled()
        {
            if (B is not null)
                B.RunWhenDisabled = true;
            return this;
        }

        /// <summary>Removes the binding.</summary>
        public void Dispose()
        {
            if (B is not null)
            {
                B.Removed = true;
            }
        }
    }

    static Handle Add(Func<int> key, Trigger trigger, Action cb, Key[] mods)
    {
        var b = new Binding
        {
            Key = key,
            Modifiers = () => mods.All(m => IsDown(m)),
            Trigger = trigger,
            Callback = cb,
            Owner = ModContext.CurrentId,
        };
        lock (bindings)
        {
            bindings.Add(b);
            if (poller is null)
            {
                poller = new Thread(Poll) { IsBackground = true, Name = "Jibbers Input" };
                poller.Start();
            }
        }
        return new Handle { B = b };
    }

    /// <summary>Calls <paramref name="callback"/> on the main thread when <paramref name="key"/> is pressed (optionally with modifiers held).</summary>
    public static Handle OnPressed(Key key, Action callback, params Key[] modifiers) => Add(() => (int)key, Trigger.Pressed, callback, modifiers);

    /// <summary>Binds a typed key property; changes from the Mods menu are followed automatically.</summary>
    public static Handle OnPressed(Func<Key> key, Action callback, params Key[] modifiers)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(callback);
        return Add(() => (int)key(), Trigger.Pressed, callback, modifiers);
    }

    /// <summary>Calls <paramref name="callback"/> when <paramref name="key"/> is released.</summary>
    public static Handle OnReleased(Key key, Action callback, params Key[] modifiers) => Add(() => (int)key, Trigger.Released, callback, modifiers);

    /// <summary>Calls <paramref name="callback"/> every poll (~120 Hz) while <paramref name="key"/> is held.</summary>
    public static Handle WhileHeld(Key key, Action callback, params Key[] modifiers) => Add(() => (int)key, Trigger.Held, callback, modifiers);

    /// <summary>
    /// Binds a mod's key setting (<see cref="Setting.KeyBinding"/>): follows the setting live, so
    /// rebinding in the Mods tab works without a restart.
    /// </summary>
    public static Handle OnSetting(JibbersMod mod, string settingKey, Action callback, Trigger trigger = Trigger.Pressed) =>
        Add(() => mod.GetKey(settingKey), trigger, callback, Array.Empty<Key>());

    static void Poll()
    {
        while (true)
        {
            Thread.Sleep(8);
            Binding[] bs;
            lock (bindings)
            {
                bindings.RemoveAll(b => b.Removed);
                bs = bindings.ToArray();
            }
            if (bs.Length == 0)
            {
                continue;
            }
            var focused = GameFocused;
            var fire = new List<Binding>();
            foreach (var b in bs)
            {
                var down = focused && IsDown(b.Key());
                var hit = b.Trigger switch
                {
                    Trigger.Pressed => down && !b.WasDown,
                    Trigger.Released => !down && b.WasDown,
                    _ => down,
                };
                b.WasDown = down;
                if (hit && b.Modifiers())
                {
                    fire.Add(b);
                }
            }
            if (fire.Count == 0)
            {
                continue;
            }
            try
            {
                MainThread.Run(() =>
                {
                    if (Typing)
                    {
                        return;
                    }
                    foreach (var b in fire)
                    {
                        try
                        {
                            b.Dispatch();
                        }
                        catch (Exception e)
                        {
                            Log.Error($"{b.Owner}: key binding", e);
                        }
                    }
                });
            }
            catch (TimeoutException)
            {
                // Game busy (loading): drop this edge.
            }
            catch (Exception e)
            {
                Log.Error("input", e);
            }
        }
    }

    internal static void RemoveOwner(string mod)
    {
        lock (bindings)
        {
            foreach (var b in bindings.Where(b => b.Owner == mod))
            {
                b.Removed = true;
            }
        }
    }

    // ---- the game's Rewired actions (controller + keyboard bindings by action name) ----

    static Il2CppMethod getPlayers, getPlayer, getButton, getButtonDown, getButtonUp, getAxis, getMapping, getActions;

    static nint Player(int id)
    {
        if (getPlayers.IsNull)
        {
            var re = Il2Cpp.RequireClass("Rewired_Core.dll", "Rewired.ReInput");
            getPlayers = re.Method("get_players", 0);
            getMapping = re.Method("get_mapping", 0);
        }
        var helper = getPlayers.Invoke(0);
        if (getPlayer.IsNull)
        {
            getPlayer = Il2Cpp.ClassOf(helper).Method("GetPlayer", "System.Int32");
        }
        var p = getPlayer.Invoke(helper, (nint)(&id));
        if (getButton.IsNull)
        {
            var pk = Il2Cpp.ClassOf(p);
            getButton = pk.Method("GetButton", "System.String");
            getButtonDown = pk.Method("GetButtonDown", "System.String");
            getButtonUp = pk.Method("GetButtonUp", "System.String");
            getAxis = pk.Method("GetAxis", "System.String");
        }
        return p;
    }

    /// <summary>Whether a game action (Rewired action name, e.g. "Jump") is held by player <paramref name="player"/>. Main thread.</summary>
    public static bool Action(string action, int player = 0) => getButton.Invoke<bool>(Player(player), Il2Cpp.String(action)) is var b && b;

    /// <summary>Whether a game action was pressed this frame. Main thread.</summary>
    public static bool ActionDown(string action, int player = 0)
    {
        var p = Player(player);
        return getButtonDown.Invoke<bool>(p, Il2Cpp.String(action));
    }

    /// <summary>Whether a game action was released this frame. Main thread.</summary>
    public static bool ActionUp(string action, int player = 0)
    {
        var p = Player(player);
        return getButtonUp.Invoke<bool>(p, Il2Cpp.String(action));
    }

    /// <summary>A game axis action's value (-1..1), e.g. a stick. Main thread.</summary>
    public static float Axis(string action, int player = 0)
    {
        var p = Player(player);
        return getAxis.Invoke<float>(p, Il2Cpp.String(action));
    }

    /// <summary>Every game action name. Main thread.</summary>
    public static string[] ActionNames()
    {
        Player(0);
        var mapping = getMapping.Invoke(0);
        if (getActions.IsNull)
        {
            getActions = Il2Cpp.ClassOf(mapping).Method("get_Actions", 0);
        }
        var list = getActions.Invoke(mapping);
        var n = Unity.Call<int>(list, "get_Count");
        var names = new List<string>();
        for (var i = 0; i < n; i++)
        {
            var idx = i;
            var a = Unity.Call(list, "get_Item", (nint)(&idx));
            names.Add(Il2Cpp.ReadString(Unity.Call(a, "get_name")));
        }
        return names.ToArray();
    }
}
