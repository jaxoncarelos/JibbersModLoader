using Jibbers.Sdk;

namespace Jibbers.Sdk.Features.Fpv;

internal enum PilotBinding
{
    Toggle,
    CycleMode,
    Lock,
    Recall,
    TiltUp,
    TiltDown,
    WidenView,
    NarrowView
}

internal enum FlightAxis
{
    Pitch,
    Roll,
    Yaw,
    Climb
}

internal enum Throttle
{
    CentreHover,
    FullRange,
    Trigger
}

internal sealed record PilotSettings
{
    public Throttle Throttle { get; init; }
    public bool InvertPitch { get; init; }
    public bool MouseLook { get; init; } = true;
    public float MouseSensitivity { get; init; } = 0.03f;
    public float Deadzone { get; init; } = 0.08f;
    public int[] Keys { get; init; } =
    {
        (int)Key.F9,
        (int)Key.F11,
        (int)Key.MouseMiddle,
        (int)Key.Home,
        (int)Key.PageUp,
        (int)Key.PageDown,
        (int)Key.Add,
        (int)Key.Subtract
    };
}

internal struct PilotActions
{
    public bool Toggle, CycleMode, Lock, Unlock, Recall;
    public int Tilt, Fov;
}

internal readonly record struct MouseInput(float X, float Y, bool Right);
internal readonly record struct GamepadInput(float LeftX, float LeftY, float RightX, float RightY, float LeftTrigger, float RightTrigger, ushort Buttons);
