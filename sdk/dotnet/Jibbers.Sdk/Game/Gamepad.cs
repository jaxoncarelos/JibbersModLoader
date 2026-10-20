using System.Runtime.InteropServices;

namespace Jibbers.Sdk;

/// <summary>XInput gamepad buttons (flags).</summary>
[Flags]
public enum PadButton : ushort
{
    None = 0,
    DpadUp = 0x0001, DpadDown = 0x0002, DpadLeft = 0x0004, DpadRight = 0x0008,
    Start = 0x0010, Back = 0x0020, LeftStick = 0x0040, RightStick = 0x0080,
    LeftShoulder = 0x0100, RightShoulder = 0x0200,
    A = 0x1000, B = 0x2000, X = 0x4000, Y = 0x8000,
}

/// <summary>One gamepad reading. Sticks are -1..1 (+Y up), triggers 0..1.</summary>
public readonly record struct PadState(bool Connected, int Slot, float LeftX, float LeftY, float RightX, float RightY, float LeftTrigger, float RightTrigger, PadButton Buttons)
{
    /// <summary>Whether every button in <paramref name="b"/> is held.</summary>
    public bool Held(PadButton b) => (Buttons & b) == b && b != 0;

    /// <summary>The sticks with a radial deadzone (rescaled so its edge is 0) and the triggers with a small one.</summary>
    public PadState WithDeadzone(float stick, float trigger = 0.03f)
    {
        var (lx, ly) = Gamepad.Deadzone(LeftX, LeftY, stick);
        var (rx, ry) = Gamepad.Deadzone(RightX, RightY, stick);
        float T(float v) => Math.Clamp((v - trigger) / (1 - trigger), 0, 1);
        return this with { LeftX = lx, LeftY = ly, RightX = rx, RightY = ry, LeftTrigger = T(LeftTrigger), RightTrigger = T(RightTrigger) };
    }
}

/// <summary>
/// XInput gamepads (Xbox and compatible), read directly: works whatever the game's own bindings are.
/// Any thread. Empty slots are probed only every couple of seconds, since asking an empty slot is slow
/// on some systems.
/// </summary>
/// <example><code>
/// var pad = Gamepad.Read().WithDeadzone(0.1f);
/// if (pad.Held(PadButton.LeftStick | PadButton.RightStick)) { ... }
/// </code></example>
public static unsafe class Gamepad
{
    [StructLayout(LayoutKind.Sequential)]
    struct XInputState
    {
        public uint Packet;
        public ushort Buttons;
        public byte Lt, Rt;
        public short Lx, Ly, Rx, Ry;
    }

    static delegate* unmanaged<uint, XInputState*, uint> getState;
    static bool loaded;
    static int slot = -1;
    static long nextProbe;
    static readonly object Lock = new();

    static bool Load()
    {
        if (!loaded)
        {
            loaded = true;
            foreach (var dll in new[] { "xinput1_4.dll", "xinput1_3.dll", "xinput9_1_0.dll" })
            {
                if (NativeLibrary.TryLoad(dll, out var lib) && NativeLibrary.TryGetExport(lib, "XInputGetState", out var f))
                {
                    getState = (delegate* unmanaged<uint, XInputState*, uint>)f;
                    break;
                }
            }
        }
        return getState != null;
    }

    static PadState Convert(int s, in XInputState x) =>
        new(true, s, x.Lx / 32767f, x.Ly / 32767f, x.Rx / 32767f, x.Ry / 32767f, x.Lt / 255f, x.Rt / 255f, (PadButton)x.Buttons);

    /// <summary>The first connected pad (raw, no deadzone), or a disconnected state.</summary>
    public static PadState Read()
    {
        lock (Lock)
        {
            if (!Load())
            {
                return default;
            }
            XInputState st;
            if (slot >= 0)
            {
                if (getState((uint)slot, &st) == 0)
                {
                    return Convert(slot, st);
                }
                slot = -1;
            }
            var now = Environment.TickCount64;
            if (now < nextProbe)
            {
                return default;
            }
            nextProbe = now + 2000;
            for (var i = 0; i < 4; i++)
            {
                if (getState((uint)i, &st) == 0)
                {
                    slot = i;
                    return Convert(i, st);
                }
            }
            return default;
        }
    }

    /// <summary>Pad <paramref name="index"/> (0-3), polled now.</summary>
    public static PadState Read(int index)
    {
        if (index is < 0 or > 3 || !Load())
        {
            return default;
        }
        XInputState st;
        return getState((uint)index, &st) == 0 ? Convert(index, st) : default;
    }

    /// <summary>Radial deadzone, rescaled so the edge of the deadzone is zero and full tilt is 1.</summary>
    public static (float X, float Y) Deadzone(float x, float y, float deadzone)
    {
        var m = MathF.Sqrt(x * x + y * y);
        if (m <= deadzone || m < 1e-6f)
        {
            return (0, 0);
        }
        var scale = MathF.Min((m - deadzone) / MathF.Max(1 - deadzone, 1e-3f), 1) / m;
        return (x * scale, y * scale);
    }
}
