using Jibbers.Sdk;

namespace Fpv;

/// <summary>Finds a connected hardware slot and applies the SDK's shared stick/trigger deadzones.</summary>
internal sealed class GamepadInputReader
{
    uint? controllerSlot;
    float secondsUntilRescan;
    public GamepadInput? Read(float deltaTime, float deadzone)
    {
        secondsUntilRescan -= deltaTime;
        if (!controllerSlot.HasValue && secondsUntilRescan > 0)
            return null;
        var first = controllerSlot ?? 0;
        var end = controllerSlot.HasValue ? first + 1 : 4;
        secondsUntilRescan = 2;
        for (var controllerIndex = first; controllerIndex < end; controllerIndex++)
        {
            var state = Jibbers.Sdk.Gamepad.Read((int)controllerIndex).WithDeadzone(deadzone);
            if (!state.Connected)
                continue;
            controllerSlot = controllerIndex;
            return new(state.LeftX, state.LeftY, state.RightX, state.RightY, state.LeftTrigger, state.RightTrigger, (ushort)state.Buttons);
        }

        controllerSlot = null;
        return null;
    }

}
