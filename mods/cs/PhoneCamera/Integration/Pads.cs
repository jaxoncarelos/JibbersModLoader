using Jibbers.Sdk;
using Jibbers.Sdk.Interop;

namespace PhoneCamera.Integration;

/// <summary>
/// Controllers: Xbox pads (and PlayStation pads through Steam Input) by XInput; PlayStation pads
/// without Steam Input through the game's own input system (Rewired), which knows their buttons by
/// name (Cross, Square, L1...). Both come back as XInput buttons and a right stick (-1..1, up +y).
/// Main thread only.
/// </summary>
internal sealed unsafe class Pads
{
    readonly Action<string> log;
    bool rewiredFailed, saidSony;
    Il2CppMethod ready, controllers;

    public Pads(Action<string> log) => this.log = log;

    /// <summary>The PlayStation names of each XInput button, as Rewired's controller maps call them.</summary>
    static readonly (string[] Names, PadButton Button)[] SonyButtons =
    {
        (new[] { "Cross" }, PadButton.A), (new[] { "Circle" }, PadButton.B), (new[] { "Square" }, PadButton.X), (new[] { "Triangle" }, PadButton.Y),
        (new[] { "L1" }, PadButton.LeftShoulder), (new[] { "R1" }, PadButton.RightShoulder),
        (new[] { "Share", "Create" }, PadButton.Back), (new[] { "Options" }, PadButton.Start),
        (new[] { "L3", "Left Stick Button" }, PadButton.LeftStick), (new[] { "R3", "Right Stick Button" }, PadButton.RightStick),
        (new[] { "D-Pad Up" }, PadButton.DpadUp), (new[] { "D-Pad Down" }, PadButton.DpadDown),
        (new[] { "D-Pad Left" }, PadButton.DpadLeft), (new[] { "D-Pad Right" }, PadButton.DpadRight),
    };

    public (PadButton Buttons, float StickX, float StickY) Read()
    {
        var pad = Gamepad.Read().WithDeadzone(0.18f);
        if (pad.Connected && (pad.Buttons != 0 || pad.RightX != 0 || pad.RightY != 0))
            return (pad.Buttons, pad.RightX, pad.RightY);
        var sony = Sony();
        return sony ?? (pad.Buttons, pad.RightX, pad.RightY);
    }

    static List<nint> Items(nint list)
    {
        var k = Il2Cpp.ClassOf(list);
        var count = k.Method("get_Count", 0).Invoke<int>(list);
        var get = k.Method("get_Item", 1);
        var items = new List<nint>();
        for (var i = 0; i < count; i++)
        {
            var index = i;
            items.Add(get.Invoke(list, (nint)(&index)));
        }
        return items;
    }

    static string Str(nint o, string getter) => Il2Cpp.ReadString(Unity.Call(o, getter));

    (PadButton, float, float)? Sony()
    {
        if (rewiredFailed) return null;
        try
        {
            if (ready.IsNull)
            {
                var input = Il2Cpp.RequireClass("Rewired_Core.dll", "Rewired.ReInput");
                ready = input.Method("get_isReady", 0);
                controllers = input.Method("get_controllers", 0);
            }
            if (!ready.Invoke<bool>(0)) return null;
            var helper = controllers.Invoke(0);
            var joysticks = helper == 0 ? 0 : Unity.Call(helper, "get_Joysticks");
            if (joysticks == 0) return null;
            foreach (var j in Items(joysticks))
            {
                var name = Str(j, "get_name") + " " + Str(j, "get_hardwareName");
                if (!name.Contains("Sony", StringComparison.OrdinalIgnoreCase) && !name.Contains("DualS", StringComparison.OrdinalIgnoreCase)
                    && !name.Contains("PlayStation", StringComparison.OrdinalIgnoreCase) && !name.Contains("Wireless Controller", StringComparison.OrdinalIgnoreCase))
                    continue;
                var ids = Items(Unity.Call(j, "get_ButtonElementIdentifiers")).ToDictionary(e => Str(e, "get_name"), e => Unity.Call<int>(e, "get_id"));
                var axes = Items(Unity.Call(j, "get_AxisElementIdentifiers")).ToDictionary(e => Str(e, "get_name"), e => Unity.Call<int>(e, "get_id"));
                if (!saidSony)
                {
                    saidSony = true;
                    log($"PlayStation controller found ({name.Trim()}): buttons {string.Join(", ", ids.Keys)}");
                }
                var getButton = Il2Cpp.ClassOf(j).Method("GetButtonById", 1);
                var getAxis = Il2Cpp.ClassOf(j).Method("GetAxisById", 1);
                PadButton held = 0;
                foreach (var (names, button) in SonyButtons)
                    foreach (var n in names)
                        if (ids.TryGetValue(n, out var id) && getButton.Invoke<bool>(j, (nint)(&id)))
                            held |= button;
                float Axis(string n) => axes.TryGetValue(n, out var id) ? getAxis.Invoke<float>(j, (nint)(&id)) : 0;
                var (x, y) = Gamepad.Deadzone(Axis("Right Stick X"), Axis("Right Stick Y"), 0.18f);
                return (held, x, y);
            }
            return null;
        }
        catch (Exception e) when (e is Il2CppException or InvalidOperationException or ArgumentException or MissingMethodException)
        {
            rewiredFailed = true;
            log($"PlayStation controllers without Steam Input aren't available: {e.Message}");
            return null;
        }
    }
}
