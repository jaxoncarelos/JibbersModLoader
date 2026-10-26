using Jibbers.Sdk;

namespace Fpv;

internal sealed class PilotInput
{
    ushort previousButtons;
    readonly bool[] previousKeys = new bool[8];
    readonly float[] ramps = new float[4];
    float? lockHeldSeconds;
    bool lockReleasedByHold;
    public bool PadConnected { get; private set; }

    public static (float X, float Y) Deadzone(float x, float y, float deadzone)
    {
        var magnitude = MathF.Sqrt(x * x + y * y);
        if (magnitude <= deadzone || magnitude < 1e-6f)
            return (0, 0);
        var scale = MathF.Min((magnitude - deadzone) / MathF.Max(1 - deadzone, 1e-3f), 1) / magnitude;
        return (x * scale, y * scale);
    }

    public static float Ramp(float value, float target, float deltaTime)
    {
        // Rust's f32::signum: +0 counts as positive, so a key pressed from rest eases in.
        static int Signum(float x) => float.IsNegative(x) ? -1 : 1;
        var step = (target == 0 || Signum(target) != Signum(value) ? 9 : 5) * deltaTime;
        return MathF.Abs(target - value) <= step ? target : value + step * MathF.Sign(target - value);
    }

    // Pure input processing lets controller edges, focus loss and lock holds be tested without a game.
    public (PilotControls, PilotActions) Update(float deltaTime, bool focused, GamepadInput? pad, MouseInput mouse, Func<int, bool> isKeyDown, PilotSettings settings)
    {
        deltaTime = float.IsFinite(deltaTime) ? Math.Clamp(deltaTime, 0, 0.1f) : 0;
        if (!focused)
        {
            previousButtons = 0;
            Array.Clear(previousKeys);
            Array.Clear(ramps);
            lockHeldSeconds = null;
            lockReleasedByHold = false;
            PadConnected = false;
            return default;
        }

        PadConnected = pad.HasValue;
        var actions = ReadActions(deltaTime, pad?.Buttons ?? 0, isKeyDown, settings);
        var controls = ReadMovement(deltaTime, pad, mouse, isKeyDown, settings);
        return (controls, actions);
    }

    PilotActions ReadActions(float deltaTime, ushort buttons, Func<int, bool> isKeyDown, PilotSettings settings)
    {
        var pressed = buttons & ~previousButtons;
        const int chord = (int)(PadButton.LeftStick | PadButton.RightStick);
        var actions = new PilotActions
        {
            Toggle = (buttons & chord) == chord && (previousButtons & chord) != chord,
            CycleMode = (pressed & (int)PadButton.LeftShoulder) != 0,
            Recall = (pressed & (int)PadButton.Y) != 0,
            Tilt = Bit(pressed, 1) - Bit(pressed, 2),
            Fov = Bit(pressed, 8) - Bit(pressed, 4),
        };
        previousButtons = buttons;
        var pressedKeys = new bool[8];
        for (var index = 0; index < 8; index++)
        {
            var held = settings.Keys[index] > 0 && isKeyDown(settings.Keys[index]);
            pressedKeys[index] = held && !previousKeys[index];
            previousKeys[index] = held;
        }

        actions.Toggle |= pressedKeys[(int)PilotBinding.Toggle];
        actions.CycleMode |= pressedKeys[(int)PilotBinding.CycleMode];
        actions.Recall |= pressedKeys[(int)PilotBinding.Recall];
        actions.Tilt += (pressedKeys[(int)PilotBinding.TiltUp] ? 1 : 0) - (pressedKeys[(int)PilotBinding.TiltDown] ? 1 : 0);
        actions.Fov += (pressedKeys[(int)PilotBinding.WidenView] ? 1 : 0) - (pressedKeys[(int)PilotBinding.NarrowView] ? 1 : 0);
        if ((buttons & (int)PadButton.RightShoulder) != 0 || previousKeys[(int)PilotBinding.Lock])
        {
            lockHeldSeconds = (lockHeldSeconds ?? 0) + deltaTime;
            if (lockHeldSeconds >= 0.45f && !lockReleasedByHold)
            {
                actions.Unlock = true;
                lockReleasedByHold = true;
            }
        }
        else if (lockHeldSeconds is { } held)
        {
            actions.Lock = held < 0.45f && !lockReleasedByHold;
            lockHeldSeconds = null;
            lockReleasedByHold = false;
        }

        return actions;
    }

    PilotControls ReadMovement(float deltaTime, GamepadInput? pad, MouseInput mouse, Func<int, bool> isKeyDown, PilotSettings settings)
    {
        var buttons = pad?.Buttons ?? 0;
        bool IsKeyDown(Key key) => isKeyDown((int)key);
        float Axis(bool negative, bool positive) => (positive ? 1 : 0) - (negative ? 1 : 0);
        float[] values =
        {
            Axis(IsKeyDown(Key.S) || IsKeyDown(Key.Down), IsKeyDown(Key.W) || IsKeyDown(Key.Up)),
            Axis(IsKeyDown(Key.A), IsKeyDown(Key.D)),
            Axis(IsKeyDown(Key.Q) || IsKeyDown(Key.Left), IsKeyDown(Key.E) || IsKeyDown(Key.Right)),
            Axis(IsKeyDown(Key.LeftControl) || IsKeyDown(Key.C), IsKeyDown(Key.Space))
        };
        for (var index = 0; index < 4; index++)
            ramps[index] = Ramp(ramps[index], values[index], deltaTime);
        var controls = new PilotControls();
        if (pad is { } gamepad)
        {
            controls.Yaw = gamepad.LeftX;
            controls.Roll = gamepad.RightX;
            controls.Pitch = gamepad.RightY;
            controls.Climb = settings.Throttle == Throttle.Trigger ? gamepad.RightTrigger - gamepad.LeftTrigger : gamepad.LeftY;
            controls.Throttle = settings.Throttle switch
            {
                Throttle.FullRange => (gamepad.LeftY + 1) * 0.5f,
                Throttle.Trigger => gamepad.RightTrigger,
                _ => null
            };
            controls.Precision = settings.Throttle != Throttle.Trigger && gamepad.LeftTrigger > 0.5f;
            controls.GimbalInput = Bit(buttons, 2) - Bit(buttons, 1);
        }

        if (values.Any(value => value != 0))
            controls.Throttle = null;
        var invert = settings.InvertPitch ? -1 : 1;
        controls.Pitch = Math.Clamp(controls.Pitch + ramps[(int)FlightAxis.Pitch], -1, 1) * invert;
        controls.Roll = Math.Clamp(controls.Roll + ramps[(int)FlightAxis.Roll], -1, 1);
        controls.Yaw = Math.Clamp(controls.Yaw + ramps[(int)FlightAxis.Yaw], -1, 1);
        controls.Climb = Math.Clamp(controls.Climb + ramps[(int)FlightAxis.Climb], -1, 1);
        controls.Precision |= mouse.Right || IsKeyDown(Key.LeftShift);
        controls.GimbalInput += (previousKeys[(int)PilotBinding.TiltDown] ? 1 : 0) - (previousKeys[(int)PilotBinding.TiltUp] ? 1 : 0);
        if (settings.MouseLook)
        {
            var sensitivity = DroneMath.Radians(settings.MouseSensitivity);
            controls.LookYaw = float.IsFinite(mouse.X) ? mouse.X * sensitivity : 0;
            controls.LookPitch = float.IsFinite(mouse.Y) ? -mouse.Y * sensitivity * invert : 0;
        }

        return controls;
    }

    static int Bit(int value, int mask) => (value & mask) != 0 ? 1 : 0;
}
