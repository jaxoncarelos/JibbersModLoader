using System.Runtime.InteropServices;
using Jibbers.Sdk;
using Jibbers.Sdk.Interop;

namespace Rumble;

/// <summary>
/// Drives the vibration motors of every controller: XInput for Xbox-style pads (and PlayStation pads
/// through Steam Input), and the game's input system (Rewired, which has DualShock 4 and DualSense
/// drivers) for the rest. Both get the same levels, so a pad both can reach just gets them twice. A
/// PlayStation pad neither reaches is driven directly by USB (<see cref="SonyPads"/>). Stops
/// everything on release.
/// </summary>
internal sealed unsafe class ControllerMotors : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    struct XInputVibration
    {
        public ushort Left, Right;
    }

    delegate* unmanaged<uint, XInputVibration*, uint> setState;
    readonly Action<string> log;
    readonly List<GameRef> joysticks = new();
    readonly HashSet<string> reported = new();
    readonly Dictionary<nint, Il2CppMethod> setVibration = new();
    float rescanSeconds;
    bool loaded, rewiredFailed;
    Motors last = new(-1, -1);
    readonly bool[] connected = new bool[4];
    readonly SonyPads sony = new();
    bool rewiredSony;

    public ControllerMotors(Action<string> log) => this.log = log;

    public void Set(Motors motors, float seconds)
    {
        motors = motors.Felt();
        // Only send changes (and a refresh now and then, as some pads time out a held level).
        // Small changes are skipped, but reaching zero always goes out, so a motor is never left faintly on.
        var reachedZero = (motors.Low == 0 && last.Low != 0) || (motors.High == 0 && last.High != 0);
        var unchanged = !reachedZero && MathF.Abs(motors.Low - last.Low) < 0.01f && MathF.Abs(motors.High - last.High) < 0.01f;
        if ((rescanSeconds -= seconds) <= 0)
        {
            rescanSeconds = 3;
            for (var slot = 0; slot < connected.Length; slot++)
                connected[slot] = Gamepad.Read(slot).Connected;
            ScanRewired();
            // Steam Input shows a PlayStation pad as an XInput one; otherwise Rewired drives it if it can.
            sony.Active = !rewiredSony && !connected.Contains(true);
            unchanged = false;
        }
        foreach (var message in sony.Messages())
            log(message);
        if (unchanged)
            return;
        last = motors;
        SetXInput(motors);
        SetRewired(motors);
        sony.Set(motors);
    }

    void SetXInput(Motors motors)
    {
        if (!loaded)
        {
            loaded = true;
            foreach (var dll in new[] { "xinput1_4.dll", "xinput1_3.dll", "xinput9_1_0.dll" })
                if (NativeLibrary.TryLoad(dll, out var library) && NativeLibrary.TryGetExport(library, "XInputSetState", out var export))
                {
                    setState = (delegate* unmanaged<uint, XInputVibration*, uint>)export;
                    break;
                }
        }
        if (setState == null)
            return;
        var vibration = new XInputVibration { Left = (ushort)(Math.Clamp(motors.Low, 0, 1) * 65535), Right = (ushort)(Math.Clamp(motors.High, 0, 1) * 65535) };
        // Only connected slots: asking an empty one is slow on some systems.
        for (var slot = 0; slot < connected.Length; slot++)
            if (connected[slot])
                setState((uint)slot, &vibration);
    }

    void ScanRewired()
    {
        if (rewiredFailed)
            return;
        try
        {
            foreach (var joystick in joysticks)
                joystick.Dispose();
            joysticks.Clear();
            rewiredSony = false;
            var reInput = Il2Cpp.RequireClass("Rewired_Core.dll", "Rewired.ReInput");
            var ready = Unity.CallStatic(reInput, "get_isReady");
            if (ready == 0 || !Il2Cpp.Unbox<bool>(ready))
                return;
            var list = Unity.Call(Unity.CallStatic(reInput, "get_controllers"), "get_Joysticks");
            var count = list == 0 ? 0 : Unity.Call<int>(list, "get_Count");
            for (var i = 0; i < Math.Min(count, 16); i++)
            {
                var index = i;
                var joystick = Unity.Call(list, "get_Item", (nint)(&index));
                if (joystick == 0)
                    continue;
                var name = Il2Cpp.ReadString(Unity.Call(joystick, "get_name"));
                var supported = Unity.Call<bool>(joystick, "get_supportsVibration");
                if (reported.Add(name))
                    log($"Rumble: {name} {(supported ? "vibrates" : "has no vibration motors")}.");
                if (supported)
                    joysticks.Add(new GameRef(joystick));
                if (supported && IsPlayStation(name))
                    rewiredSony = true;
            }
        }
        catch (Exception exception)
        {
            rewiredFailed = true;
            log("Rumble: controllers through the game's input system are unavailable: " + exception.Message);
        }
    }

    /// <summary>Whether a Rewired joystick name is a PlayStation pad ("Sony DualSense", "Sony DualShock 4", ...).</summary>
    internal static bool IsPlayStation(string name)
    {
        var text = name.ToLowerInvariant();
        return text.Contains("sony") || text.Contains("dualsense") || text.Contains("dualshock") || text.Contains("playstation") || text.Contains("ps4") || text.Contains("ps5") || text == "wireless controller";
    }

    void SetRewired(Motors motors)
    {
        foreach (var joystick in joysticks)
        {
            try
            {
                if (Unity.IsNull(joystick.Target))
                    continue;
                // SetVibration(float, float) sets both motors; (int, float) is the per-motor overload.
                var type = Il2Cpp.ClassOf(joystick.Target);
                if (!setVibration.TryGetValue(type.Ptr, out var method))
                    setVibration[type.Ptr] = method = FindSetVibration(type);
                if (method.IsNull)
                    continue;
                var (low, high) = (Math.Clamp(motors.Low, 0, 1), Math.Clamp(motors.High, 0, 1));
                method.Invoke(joystick.Target, (nint)(&low), (nint)(&high));
            }
            catch (Exception)
            {
                // A controller unplugged between scans.
            }
        }
    }

    /// <summary>SetVibration(float, float) on the joystick's class or the first base class that declares it.</summary>
    static Il2CppMethod FindSetVibration(Il2CppClass type)
    {
        for (var c = type; !c.IsNull; c = c.Parent)
            if (c.Method("SetVibration", "System.Single", "System.Single") is { IsNull: false } method)
                return method;
        return default;
    }

    public void Stop()
    {
        last = new(-1, -1);
        SetXInput(Motors.Off);
        SetRewired(Motors.Off);
        sony.Set(Motors.Off);
        last = Motors.Off;
    }

    public void Dispose()
    {
        try
        {
            Stop();
        }
        finally
        {
            foreach (var joystick in joysticks)
                joystick.Dispose();
            joysticks.Clear();
            sony.Dispose();
        }
    }
}
