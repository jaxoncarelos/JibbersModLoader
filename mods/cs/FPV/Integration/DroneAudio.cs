using Jibbers.Sdk;
using Jibbers.Sdk.Interop;

namespace Fpv;

/// <summary>Owns drone audio sources; synthesis math and HUD presentation have independent lifetimes.</summary>
internal sealed class DroneAudio : IDisposable
{
    readonly OwnedGameObject root;
    GameRef? motor;
    GameRef? wind;
    GameRef? crash;
    float motorPitch = 0.4f;
    float motorLevel;
    public DroneAudio()
    {
        root = OwnedGameObject.Create("JibbersFPV.Audio");
        try
        {
            LoadSources();
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    void LoadSources()
    {
        using var bundle = AssetBundles.Load(Path.Combine(Bootstrap.ModsDir, "jibbers_fpv.bundle"));
        var prefab = bundle.Instantiate("JibbersFPVAudio", root.Transform);
        var transform = Unity.Transform(prefab);
        GameRef FindSource(string name)
        {
            var child = Unity.Call(transform, "Find", Il2Cpp.String(name));
            if (Unity.IsNull(child))
                throw new InvalidOperationException("FPV audio source missing: " + name);
            var source = Unity.GetComponent(child, Unity.Class("UnityEngine.AudioSource"));
            if (Unity.IsNull(source))
                throw new InvalidOperationException("No AudioSource on " + name);
            return new(source);
        }

        motor = FindSource("Motor");
        wind = FindSource("Wind");
        crash = FindSource("Crash");
        UnityProperties.Set(motor.Target, "set_volume", 0f);
        UnityProperties.Set(wind.Target, "set_volume", 0f);
        Unity.Call(motor.Target, "Play");
        Unity.Call(wind.Target, "Play");
    }

    public void Update(float elapsedSeconds, DroneTelemetry telemetry, float volume, bool crashed)
    {
        var(pitch, level) = DroneSounds.MotorVoice(telemetry.Power, !telemetry.MotorsDisarmed);
        var motorBlend = DroneMath.Blend(elapsedSeconds, telemetry.MotorsDisarmed ? 0.35f : 0.06f);
        motorPitch += (pitch - motorPitch) * motorBlend;
        motorLevel += (level - motorLevel) * motorBlend;
        var audibleVolume = telemetry.Paused ? 0 : Math.Clamp(volume, 0, 1);
        UnityProperties.Set(motor!.Target, "set_pitch", motorPitch);
        UnityProperties.Set(motor.Target, "set_volume", motorLevel * audibleVolume * 0.6f);
        var(windPitch, windLevel) = DroneSounds.WindVoice(telemetry.Speed);
        UnityProperties.Set(wind!.Target, "set_pitch", windPitch);
        UnityProperties.Set(wind.Target, "set_volume", windLevel * audibleVolume * 0.5f);
        if (crashed && audibleVolume > 0)
        {
            UnityProperties.Set(crash!.Target, "set_volume", audibleVolume);
            Unity.Call(crash.Target, "Play");
        }
    }

    public void Dispose()
    {
        var sources = new[]
        {
            motor,
            wind,
            crash
        };
        motor = wind = crash = null;
        Cleanup.Run(new Action[] { root.Dispose }.Concat(sources.OfType<GameRef>().Select(source => (Action)source.Dispose)).ToArray());
    }
}
