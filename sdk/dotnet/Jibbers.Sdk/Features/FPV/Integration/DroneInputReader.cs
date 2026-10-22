namespace Jibbers.Sdk.Features.Fpv;

/// <summary>Hardware polling and optional mouse integration; pure control shaping stays in PilotInput.</summary>
internal sealed class DroneInputReader : IDroneInput
{
    readonly PilotInput pilot = new();
    readonly GamepadInputReader gamepad = new();
    readonly Action<string> log;
    RewiredMouseInput? mouse;
    public bool GamepadConnected => pilot.PadConnected;

    public DroneInputReader(Action<string> log)
    {
        this.log = log;
        try
        {
            mouse = new RewiredMouseInput();
        }
        catch (Exception exception)
        {
            log("no mouse look: " + exception.Message);
        }
    }

    public (PilotControls Controls, PilotActions Actions) Read(float elapsedSeconds, bool focused, bool flying, PilotSettings settings)
    {
        MouseInput movement = default;
        if (focused && flying && mouse is not null)
        {
            try
            {
                movement = mouse.Read();
            }
            catch (Exception exception)
            {
                mouse = null;
                log("mouse look stopped: " + exception.Message);
            }
        }

        var controller = focused ? gamepad.Read(elapsedSeconds, settings.Deadzone) : null;
        return pilot.Update(elapsedSeconds, focused, controller, movement, Input.IsDown, settings);
    }
}
