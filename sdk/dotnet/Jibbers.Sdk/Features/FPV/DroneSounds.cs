// Drone sounds, synthesised so the mod ships no audio files. Loops are exactly one second long with
// whole-number frequencies, so they repeat without a click; noise loops are crossfaded. Pitch and
// volume follow the motors at runtime. Port of the Rust FPV's synth.rs (the bundle's clips are
// rendered from these; see the native src/bin/write_sounds.rs asset utility).
namespace Jibbers.Sdk.Features.Fpv;

internal static class DroneSounds
{
    public const int Rate = 44_100;
    /// <summary>Deterministic white noise in -1..1.</summary>
    struct Noise(uint seed)
    {
        uint randomState = seed;
        public float Next()
        {
            randomState ^= randomState << 13;
            randomState ^= randomState >> 17;
            randomState ^= randomState << 5;
            return (float)randomState / uint.MaxValue * 2 - 1;
        }
    }

    /// <summary><paramref name = "sampleCount"/> samples of one-pole low-passed noise that loops without a seam.</summary>
    static float[] NoiseLoop(int sampleCount, uint seed, float cutoff)
    {
        var fade = sampleCount / 8;
        var noise = new Noise(seed);
        var filterBlend = 1 - MathF.Exp(-MathF.Tau * cutoff / Rate);
        var filteredSample = 0f;
        var raw = new float[sampleCount + fade];
        for (var sampleIndex = 0; sampleIndex < raw.Length; sampleIndex++)
        {
            filteredSample += (noise.Next() - filteredSample) * filterBlend;
            raw[sampleIndex] = filteredSample;
        }

        // The tail past len blends into the head, so the last sample leads into the first.
        var output = raw[..sampleCount];
        for (var sampleIndex = 0; sampleIndex < fade; sampleIndex++)
        {
            var blend = (float)sampleIndex / fade;
            output[sampleIndex] = raw[sampleCount + sampleIndex] * (1 - blend) + raw[sampleIndex] * blend;
        }

        return output;
    }

    static float[] Normalize(float[] samples, float peak)
    {
        var maximum = samples.Max(MathF.Abs);
        if (maximum > 0)
        {
            for (var sampleIndex = 0; sampleIndex < samples.Length; sampleIndex++)
            {
                samples[sampleIndex] *= peak / maximum;
            }
        }

        return samples;
    }

    /// <summary>Four slightly detuned motors (the beating makes a quad sound like a quad) plus prop wash; idles around 190 Hz at pitch 1.</summary>
    public static float[] MotorLoop()
    {
        float[] motors =
        {
            188,
            191,
            186,
            194
        };
        var wash = NoiseLoop(Rate, 0x9e37_79b9, 2_500);
        var output = new float[Rate];
        for (var sampleIndex = 0; sampleIndex < Rate; sampleIndex++)
        {
            var elapsedSeconds = (float)sampleIndex / Rate;
            var tone = 0f;
            foreach (var frequency in motors)
            {
                for (var harmonic = 1; harmonic <= 12; harmonic++)
                {
                    tone += MathF.Sin(MathF.Tau * frequency * harmonic * elapsedSeconds) / harmonic;
                }
            }

            output[sampleIndex] = tone * 0.2f + wash[sampleIndex] * 0.9f;
        }

        return Normalize(output, 0.8f);
    }

    /// <summary>Rushing air: darker noise, louder with speed.</summary>
    public static float[] WindLoop() => Normalize(NoiseLoop(Rate, 0x85eb_ca6b, 600), 0.8f);
    /// <summary>A short plastic-and-carbon thump for a crash.</summary>
    public static float[] CrashClip()
    {
        var sampleCount = Rate * 35 / 100;
        var noise = new Noise(0xc2b2_ae35);
        var phase = 0f;
        var output = new float[sampleCount];
        for (var sampleIndex = 0; sampleIndex < sampleCount; sampleIndex++)
        {
            var elapsedSeconds = (float)sampleIndex / Rate;
            phase += MathF.Tau * MathF.Max(140 - 260 * elapsedSeconds, 45) / Rate;
            output[sampleIndex] = MathF.Sin(phase) * MathF.Exp(-elapsedSeconds * 14) + noise.Next() * 0.6f * MathF.Exp(-elapsedSeconds * 30);
        }

        return Normalize(output, 0.9f);
    }

    /// <summary>Pitch and volume of the motor loop for a motor output of 0..1. Motors stopped are silent.</summary>
    public static (float Pitch, float Volume) MotorVoice(float power, bool armed)
    {
        if (!armed)
        {
            return (0.4f, 0);
        }

        var motorOutput = Math.Clamp(power, 0, 1);
        return (0.7f + 1.5f * MathF.Sqrt(motorOutput), 0.35f + 0.65f * motorOutput);
    }

    /// <summary>Pitch and volume of the wind loop at a speed in m/s.</summary>
    public static (float Pitch, float Volume) WindVoice(float speed)
    {
        var speedFraction = Math.Clamp(speed / 40, 0, 1);
        return (0.8f + 0.6f * speedFraction, MathF.Pow(speedFraction, 1.5f) * 0.8f);
    }
}
