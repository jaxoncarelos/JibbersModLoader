// Drone sounds, synthesised so the mod ships no audio files. Loops are exactly one second long with
// whole-number frequencies, so they repeat without a click; noise loops are crossfaded. Pitch and
// volume follow the motors at runtime. Port of the Rust FPV's synth.rs (the bundle's clips are
// rendered from these; see the native src/bin/write_sounds.rs asset utility).

namespace Jibbers.Sdk.Features.Fpv;

internal static class Synth
{
    public const int Rate = 44_100;

    /// <summary>Deterministic white noise in -1..1.</summary>
    struct Noise(uint seed)
    {
        uint s = seed;

        public float Next()
        {
            s ^= s << 13;
            s ^= s >> 17;
            s ^= s << 5;
            return (float)s / uint.MaxValue * 2 - 1;
        }
    }

    /// <summary><paramref name="len"/> samples of one-pole low-passed noise that loops without a seam.</summary>
    static float[] NoiseLoop(int len, uint seed, float cutoff)
    {
        var fade = len / 8;
        var noise = new Noise(seed);
        var a = 1 - MathF.Exp(-MathF.Tau * cutoff / Rate);
        var y = 0f;
        var raw = new float[len + fade];
        for (var i = 0; i < raw.Length; i++)
        {
            y += (noise.Next() - y) * a;
            raw[i] = y;
        }
        // The tail past len blends into the head, so the last sample leads into the first.
        var o = raw[..len];
        for (var i = 0; i < fade; i++)
        {
            var t = (float)i / fade;
            o[i] = raw[len + i] * (1 - t) + raw[i] * t;
        }
        return o;
    }

    static float[] Normalize(float[] v, float peak)
    {
        var max = v.Max(MathF.Abs);
        if (max > 0)
        {
            for (var i = 0; i < v.Length; i++)
            {
                v[i] *= peak / max;
            }
        }
        return v;
    }

    /// <summary>Four slightly detuned motors (the beating makes a quad sound like a quad) plus prop wash; idles around 190 Hz at pitch 1.</summary>
    public static float[] MotorLoop()
    {
        float[] motors = { 188, 191, 186, 194 };
        var wash = NoiseLoop(Rate, 0x9e37_79b9, 2_500);
        var o = new float[Rate];
        for (var i = 0; i < Rate; i++)
        {
            var t = (float)i / Rate;
            var tone = 0f;
            foreach (var f in motors)
            {
                for (var h = 1; h <= 12; h++)
                {
                    tone += MathF.Sin(MathF.Tau * f * h * t) / h;
                }
            }
            o[i] = tone * 0.2f + wash[i] * 0.9f;
        }
        return Normalize(o, 0.8f);
    }

    /// <summary>Rushing air: darker noise, louder with speed.</summary>
    public static float[] WindLoop() => Normalize(NoiseLoop(Rate, 0x85eb_ca6b, 600), 0.8f);

    /// <summary>A short plastic-and-carbon thump for a crash.</summary>
    public static float[] CrashClip()
    {
        var len = Rate * 35 / 100;
        var noise = new Noise(0xc2b2_ae35);
        var phase = 0f;
        var o = new float[len];
        for (var i = 0; i < len; i++)
        {
            var t = (float)i / Rate;
            phase += MathF.Tau * MathF.Max(140 - 260 * t, 45) / Rate;
            o[i] = MathF.Sin(phase) * MathF.Exp(-t * 14) + noise.Next() * 0.6f * MathF.Exp(-t * 30);
        }
        return Normalize(o, 0.9f);
    }

    /// <summary>Pitch and volume of the motor loop for a motor output of 0..1. Motors stopped are silent.</summary>
    public static (float Pitch, float Volume) MotorVoice(float power, bool armed)
    {
        if (!armed)
        {
            return (0.4f, 0);
        }
        var p = Math.Clamp(power, 0, 1);
        return (0.7f + 1.5f * MathF.Sqrt(p), 0.35f + 0.65f * p);
    }

    /// <summary>Pitch and volume of the wind loop at a speed in m/s.</summary>
    public static (float Pitch, float Volume) WindVoice(float speed)
    {
        var s = Math.Clamp(speed / 40, 0, 1);
        return (0.8f + 0.6f * s, MathF.Pow(s, 1.5f) * 0.8f);
    }
}
