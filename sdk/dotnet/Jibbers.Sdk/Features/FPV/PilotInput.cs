using System.Runtime.InteropServices;
using Jibbers.Sdk;

namespace Jibbers.Sdk.Features.Fpv;

internal enum Throttle { CentreHover, FullRange, Trigger }
internal sealed record PilotConfig
{
    public Throttle Throttle { get; init; }
    public bool InvertPitch { get; init; }
    public bool MouseLook { get; init; } = true;
    public float MouseSensitivity { get; init; } = 0.03f;
    public float Deadzone { get; init; } = 0.08f;
    public int[] Keys { get; init; } = { (int)Key.F9, (int)Key.F10, (int)Key.MouseMiddle, (int)Key.Home,
        (int)Key.PageUp, (int)Key.PageDown, (int)Key.Add, (int)Key.Subtract };
}
internal struct Actions
{
    public bool Toggle, Mode, Lock, Unlock, Recall;
    public int Tilt, Fov;
}
internal readonly record struct Mouse(float X, float Y, bool Right);
internal readonly record struct Pad(float LX, float LY, float RX, float RY, float LT, float RT, ushort Buttons);

internal sealed class PilotInput
{
    [StructLayout(LayoutKind.Sequential)]
    struct Gamepad { public ushort Buttons; public byte LT, RT; public short LX, LY, RX, RY; }
    [StructLayout(LayoutKind.Sequential)]
    struct PadState { public uint Packet; public Gamepad Pad; }
    [DllImport("xinput1_4.dll")] static extern uint XInputGetState(uint slot, out PadState state);
    uint? slot;
    float rescan;
    ushort previousButtons;
    readonly bool[] previousKeys = new bool[8];
    readonly float[] ramps = new float[4];
    float? lockHeld;
    bool cleared;
    public bool PadConnected { get; private set; }

    public static (float X, float Y) Deadzone(float x, float y, float dz)
    {
        var m = MathF.Sqrt(x * x + y * y);
        if (m <= dz || m < 1e-6f) return (0, 0);
        var scale = MathF.Min((m - dz) / MathF.Max(1 - dz, 1e-3f), 1) / m;
        return (x * scale, y * scale);
    }
    public static float Ramp(float value, float target, float dt)
    {
        // Rust's f32::signum: +0 counts as positive, so a key pressed from rest eases in.
        static int Signum(float x) => float.IsNegative(x) ? -1 : 1;
        var step = (target == 0 || Signum(target) != Signum(value) ? 9 : 5) * dt;
        return MathF.Abs(target - value) <= step ? target : value + step * MathF.Sign(target - value);
    }
    Pad? Poll(float dt, float dz)
    {
        rescan -= dt;
        if (!slot.HasValue && rescan > 0) return null;
        var first = slot ?? 0;
        var end = slot.HasValue ? first + 1 : 4;
        rescan = 2;
        for (var i = first; i < end; i++)
        {
            if (XInputGetState(i, out var state) != 0) continue;
            slot = i;
            var p = state.Pad;
            var (lx, ly) = Deadzone(p.LX / 32767f, p.LY / 32767f, dz);
            var (rx, ry) = Deadzone(p.RX / 32767f, p.RY / 32767f, dz);
            static float Trigger(byte b) => Math.Clamp((b / 255f - 0.03f) / 0.97f, 0, 1);
            return new(lx, ly, rx, ry, Trigger(p.LT), Trigger(p.RT), p.Buttons);
        }
        slot = null;
        return null;
    }
    public (Controls, Actions) Read(float dt, bool focused, Mouse mouse, PilotConfig c)
        => Update(dt, focused, focused ? Poll(dt, c.Deadzone) : null, mouse, Input.IsDown, c);

    // Pure input processing lets controller edges, focus loss and lock holds be tested without a game.
    public (Controls, Actions) Update(float dt, bool focused, Pad? pad, Mouse mouse, Func<int, bool> down, PilotConfig c)
    {
        dt = float.IsFinite(dt) ? Math.Clamp(dt, 0, 0.1f) : 0;
        if (!focused)
        {
            previousButtons = 0;
            Array.Clear(previousKeys);
            Array.Clear(ramps);
            lockHeld = null;
            cleared = false;
            PadConnected = false;
            return default;
        }
        PadConnected = pad.HasValue;
        var buttons = pad?.Buttons ?? 0;
        var pressed = buttons & ~previousButtons;
        const int chord = 0x40 | 0x80;
        var a = new Actions
        {
            Toggle = (buttons & chord) == chord && (previousButtons & chord) != chord,
            Mode = (pressed & 0x100) != 0, Recall = (pressed & 0x8000) != 0,
            Tilt = Bit(pressed, 1) - Bit(pressed, 2), Fov = Bit(pressed, 8) - Bit(pressed, 4),
        };
        previousButtons = buttons;
        var edge = new bool[8];
        for (var i = 0; i < 8; i++)
        {
            var held = c.Keys[i] > 0 && down(c.Keys[i]);
            edge[i] = held && !previousKeys[i];
            previousKeys[i] = held;
        }
        a.Toggle |= edge[0]; a.Mode |= edge[1]; a.Recall |= edge[3];
        a.Tilt += (edge[4] ? 1 : 0) - (edge[5] ? 1 : 0);
        a.Fov += (edge[6] ? 1 : 0) - (edge[7] ? 1 : 0);
        if ((buttons & 0x200) != 0 || previousKeys[2])
        {
            lockHeld = (lockHeld ?? 0) + dt;
            if (lockHeld >= 0.45f && !cleared) { a.Unlock = true; cleared = true; }
        }
        else if (lockHeld is { } held)
        {
            a.Lock = held < 0.45f && !cleared;
            lockHeld = null; cleared = false;
        }
        bool K(Key key) => down((int)key);
        float Axis(bool neg, bool pos) => (pos ? 1 : 0) - (neg ? 1 : 0);
        float[] values = { Axis(K(Key.S) || K(Key.Down), K(Key.W) || K(Key.Up)), Axis(K(Key.A), K(Key.D)),
            Axis(K(Key.Q) || K(Key.Left), K(Key.E) || K(Key.Right)), Axis(K(Key.LeftControl) || K(Key.C), K(Key.Space)) };
        for (var i = 0; i < 4; i++) ramps[i] = Ramp(ramps[i], values[i], dt);
        var ctl = new Controls();
        if (pad is { } p)
        {
            ctl.Yaw = p.LX; ctl.Roll = p.RX; ctl.Pitch = p.RY;
            ctl.Climb = c.Throttle == Throttle.Trigger ? p.RT - p.LT : p.LY;
            ctl.Throttle = c.Throttle switch { Throttle.FullRange => (p.LY + 1) * 0.5f, Throttle.Trigger => p.RT, _ => null };
            ctl.Precision = c.Throttle != Throttle.Trigger && p.LT > 0.5f;
            ctl.Gimbal = Bit(buttons, 2) - Bit(buttons, 1);
        }
        if (values.Any(v => v != 0)) ctl.Throttle = null;
        var invert = c.InvertPitch ? -1 : 1;
        ctl.Pitch = Math.Clamp(ctl.Pitch + ramps[0], -1, 1) * invert;
        ctl.Roll = Math.Clamp(ctl.Roll + ramps[1], -1, 1);
        ctl.Yaw = Math.Clamp(ctl.Yaw + ramps[2], -1, 1);
        ctl.Climb = Math.Clamp(ctl.Climb + ramps[3], -1, 1);
        ctl.Precision |= mouse.Right || K(Key.LeftShift);
        ctl.Gimbal += (previousKeys[5] ? 1 : 0) - (previousKeys[4] ? 1 : 0);
        if (c.MouseLook)
        {
            var sensitivity = DroneMath.Radians(c.MouseSensitivity);
            ctl.LookYaw = float.IsFinite(mouse.X) ? mouse.X * sensitivity : 0;
            ctl.LookPitch = float.IsFinite(mouse.Y) ? -mouse.Y * sensitivity * invert : 0;
        }
        return (ctl, a);
    }
    static int Bit(int value, int mask) => (value & mask) != 0 ? 1 : 0;
}
