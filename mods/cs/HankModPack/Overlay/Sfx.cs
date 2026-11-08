namespace RickBackflip;

/// <summary>Trois petits sons d'arcade synthétisés en code (aucun échantillon externe), écrits en .wav au premier lancement.</summary>
internal static class Sfx
{
    const int Rate = 44100;

    public static string PathFor(string dir, int tier) => Path.Combine(dir, $"banner_tier{tier}.wav");

    public static void EnsureFiles(string dir)
    {
        Directory.CreateDirectory(dir);
        for (var tier = 1; tier <= 3; tier++)
        {
            var path = PathFor(dir, tier);
            if (!File.Exists(path)) File.WriteAllBytes(path, Wav(Make(tier)));
        }
    }

    static float Tone(double t, double freq, double shape)
    {
        var p = t * freq;
        var tri = 2 * Math.Abs(2 * (p - Math.Floor(p + 0.5))) - 1;
        var sq = Math.Sin(2 * Math.PI * p) >= 0 ? 1.0 : -1.0;
        return (float)(tri * (1 - shape) + sq * shape * 0.6);
    }

    static float[] Make(int tier)
    {
        var seconds = tier == 1 ? 0.55 : tier == 2 ? 0.9 : 1.5;
        var buf = new float[(int)(seconds * Rate)];
        var rnd = new Random(7 * tier);

        void Note(double start, double len, double freq, double gain, double shape)
        {
            for (var i = (int)(start * Rate); i < Math.Min(buf.Length, (int)((start + len) * Rate)); i++)
            {
                var t = i / (double)Rate - start;
                var env = Math.Min(1, t / 0.004) * Math.Exp(-t * 5.5 / len);
                buf[i] += (float)(Tone(i / (double)Rate, freq, shape) * env * gain);
            }
        }

        if (tier == 1)
        {
            Note(0, 0.25, 1046.5, 0.5, 0.2);   // do6
            Note(0.09, 0.40, 1318.5, 0.5, 0.2); // mi6
        }
        else if (tier == 2)
        {
            double[] arp = { 523.25, 659.25, 783.99, 1046.5 };
            for (var i = 0; i < arp.Length; i++) Note(i * 0.07, 0.5, arp[i], 0.4, 0.4);
            Note(0.28, 0.55, 2093, 0.18, 0.0);
        }
        else
        {
            // « whoosh » : bruit filtré dont la fréquence monte, puis accord plein.
            float lp = 0;
            for (var i = 0; i < (int)(0.40 * Rate); i++)
            {
                var t = i / (double)Rate;
                var cutoff = 0.02 + 0.45 * (t / 0.40);
                lp += (float)(cutoff * ((rnd.NextDouble() * 2 - 1) - lp));
                buf[i] += lp * (float)(Math.Sin(Math.PI * t / 0.40) * 1.6);
            }
            double[] chord = { 523.25, 659.25, 783.99, 1046.5, 1318.5 };
            for (var i = 0; i < chord.Length; i++) Note(0.30 + i * 0.045, 1.0, chord[i], 0.30, 0.5);
            Note(0.55, 0.9, 2093, 0.14, 0.0);
            // écho
            for (var i = buf.Length - 1; i >= (int)(0.18 * Rate); i--) buf[i] += buf[i - (int)(0.18 * Rate)] * 0.28f;
        }

        var peak = buf.Max(Math.Abs);
        if (peak > 0) for (var i = 0; i < buf.Length; i++) buf[i] *= 0.85f / peak;
        for (var i = 0; i < Math.Min(buf.Length, 200); i++) buf[buf.Length - 1 - i] *= i / 200f; // pas de clic final
        return buf;
    }

    static byte[] Wav(float[] samples)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write("RIFF"u8); w.Write(36 + samples.Length * 2); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1);
        w.Write(Rate); w.Write(Rate * 2); w.Write((short)2); w.Write((short)16);
        w.Write("data"u8); w.Write(samples.Length * 2);
        foreach (var s in samples) w.Write((short)(Math.Clamp(s, -1f, 1f) * 32767));
        w.Flush();
        return ms.ToArray();
    }
}
