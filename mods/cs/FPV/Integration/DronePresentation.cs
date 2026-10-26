using Jibbers.Sdk;

namespace Fpv;

/// <summary>Optional HUD/audio facade. Either component may fail without interrupting flight or the other component.</summary>
internal sealed class DronePresentation(Action<string> log) : IDronePresentation
{
    DroneHud? hud;
    DroneAudio? audio;
    public void Show(bool hudEnabled, bool soundEnabled)
    {
        Hide();
        if (hudEnabled)
        {
            try
            {
                hud = new DroneHud();
            }
            catch (Exception exception)
            {
                log("no HUD: " + exception.Message);
            }
        }

        if (soundEnabled)
        {
            try
            {
                audio = new DroneAudio();
            }
            catch (Exception exception)
            {
                log("no audio: " + exception.Message);
            }
        }
    }

    public void Update(float elapsedSeconds, DroneTelemetry telemetry, float volume, bool crashed)
    {
        try
        {
            hud?.Update(elapsedSeconds, telemetry);
        }
        catch (Exception exception)
        {
            var failed = hud;
            hud = null;
            log("HUD stopped: " + exception.Message);
            failed?.Dispose();
        }

        try
        {
            audio?.Update(elapsedSeconds, telemetry, volume, crashed);
        }
        catch (Exception exception)
        {
            var failed = audio;
            audio = null;
            log("audio stopped: " + exception.Message);
            failed?.Dispose();
        }
    }

    public void Hide()
    {
        var previousHud = hud;
        var previousAudio = audio;
        hud = null;
        audio = null;
        Cleanup.Run(() => previousHud?.Dispose(), () => previousAudio?.Dispose());
    }

    public void Dispose() => Hide();
}
