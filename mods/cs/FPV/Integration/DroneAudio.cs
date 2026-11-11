using Jibbers.Sdk;
using Jibbers.Sdk.Interop;

namespace Fpv;

/// <summary>Owns drone audio sources; synthesis math and HUD presentation have independent lifetimes.</summary>
internal sealed unsafe class DroneAudio : IDisposable
{
    readonly OwnedGameObject root;
    GameRef? motor;
    GameRef? wind;
    GameRef? crash;
    float motorPitch = 0.4f;
    float motorLevel;
    float sinceStart;
    bool checkedOutput;
    readonly Action<string>? log;
    public DroneAudio(Action<string>? log = null)
    {
        this.log = log;
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
        Play(motor.Target);
        Play(wind.Target);
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
        if (!checkedOutput && (sinceStart += elapsedSeconds) > 2 && motorLevel * audibleVolume > 0.05f)
        {
            checkedOutput = true;
            log?.Invoke(OutputCheck(motor.Target));
        }
        if (crashed && audibleVolume > 0)
        {
            UnityProperties.Set(crash!.Target, "set_volume", audibleVolume);
            Play(crash.Target);
        }
    }

    static Il2CppMethod managedPlay;
    static delegate* unmanaged<nint, ulong, void> enginePlay;
    static Il2CppField cachedPtr;

    /// <summary>
    /// AudioSource.Play(). Game build 26596007 plays its own sounds through FMOD, so IL2CPP stripped
    /// the managed Play wrappers; the engine's native implementation (same UnityPlayer) is still
    /// registered as an internal call taking the source's native object, which is what Play called.
    /// </summary>
    static void Play(nint source)
    {
        if (managedPlay.IsNull && enginePlay == null)
        {
            managedPlay = Il2Cpp.ClassOf(source).Method("Play", 0);
            if (managedPlay.IsNull)
            {
                var native = Il2Cpp.ICall("UnityEngine.AudioSource::PlayHelper_Injected(System.IntPtr,System.UInt64)");
                if (native == 0)
                    native = Il2Cpp.ICall("UnityEngine.AudioSource::PlayHelper_Injected");
                if (native == 0)
                    throw new MissingMethodException("UnityEngine.AudioSource.Play/0 (stripped, and no engine internal call)");
                cachedPtr = Unity.Class("UnityEngine.Object").Fields.FirstOrDefault(f => f.Name == "m_CachedPtr" && !f.IsStatic);
                if (cachedPtr.IsNull)
                    throw new MissingFieldException("UnityEngine.Object.m_CachedPtr");
                enginePlay = (delegate* unmanaged<nint, ulong, void>)native;
            }
        }
        if (!managedPlay.IsNull)
        {
            managedPlay.Invoke(source);
            return;
        }
        var self = cachedPtr.Get<nint>(source);
        if (self == 0)
            throw new InvalidOperationException("AudioSource has no native object");
        enginePlay(self, 0);
    }

    /// <summary>One-time diagnostic: whether the motor source is producing sound (its output spectrum).</summary>
    static string OutputCheck(nint source)
    {
        try
        {
            var single = Il2Cpp.Class("mscorlib.dll", "System", "Single");
            using var samples = new GameRef(Il2Cpp.NewArray(single, 64));
            var window = 0; // FFTWindow.Rectangular
            var channel = 0;
            Il2Cpp.ClassOf(source).Method("GetSpectrumData", 3).Invoke(source, samples.Target, (nint)(&channel), (nint)(&window));
            var peak = 0f;
            for (var i = 0; i < 64; i++) peak = Math.Max(peak, ((float*)Il2Cpp.ArrayData(samples.Target))[i]);
            var how = managedPlay.IsNull ? "engine call" : "AudioSource.Play";
            return peak > 0 ? $"audio playing via {how} (motor spectrum peak {peak:0.0000})"
                : $"audio started via {how} but the motor is silent: this game build plays its sound through FMOD with Unity audio disabled, so the drone has no sound";
        }
        catch (Exception error) { return "audio output check unavailable: " + error.Message; }
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
