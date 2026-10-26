using System.Runtime.CompilerServices;
using Fpv;

// The game's build strips AudioClip.Create/SetData, so the drone sounds ship as WAVs in
// jibbers_fpv.bundle (mods/JibbersFPV/unity). They are rendered from DroneSounds; this test keeps
// them in sync. To re-render after changing the synth, run the FPV tests with JIBBERS_WRITE_SOUNDS=1,
// then rebuild the bundle.
static class SoundAssetTests
{
    static string? AudioDirectory([CallerFilePath] string source = "")
    {
        var directory = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!, "../../JibbersFPV/unity/Assets/Audio"));
        return Directory.Exists(directory) ? directory : null; // The SDK kit ships without the Unity project.
    }

    static byte[] Wav(float[] samples)
    {
        var data = samples.SelectMany(s => BitConverter.GetBytes((short)(Math.Clamp(s, -1f, 1f) * 32767f))).ToArray();
        using var stream = new MemoryStream();
        using var w = new BinaryWriter(stream);
        w.Write("RIFF"u8); w.Write(36 + data.Length); w.Write("WAVEfmt "u8);
        w.Write(16); w.Write((short)1); w.Write((short)1); // PCM, mono
        w.Write(DroneSounds.Rate); w.Write(DroneSounds.Rate * 2); w.Write((short)2); w.Write((short)16);
        w.Write("data"u8); w.Write(data.Length); w.Write(data);
        w.Flush();
        return stream.ToArray();
    }

    [Test]
    static void BundledClipsMatchTheSynth()
    {
        if (AudioDirectory() is not { } directory)
            return;
        var write = Environment.GetEnvironmentVariable("JIBBERS_WRITE_SOUNDS") == "1";
        foreach (var (name, samples) in new[] { ("Motor", DroneSounds.MotorLoop()), ("Wind", DroneSounds.WindLoop()), ("Crash", DroneSounds.CrashClip()) })
        {
            var path = Path.Combine(directory, name + ".wav");
            if (write)
                File.WriteAllBytes(path, Wav(samples));
            else
                Assert.True(Matches(File.ReadAllBytes(path), Wav(samples)), $"{name}.wav is stale; re-render with JIBBERS_WRITE_SOUNDS=1");
        }
    }

    /// <summary>Same header, and samples within 1 LSB: the shipped clips were rendered by the Rust synth,
    /// whose sine rounds differently from .NET's in a few dozen samples.</summary>
    static bool Matches(byte[] shipped, byte[] rendered)
    {
        const int header = 44;
        if (shipped.Length != rendered.Length || !shipped.AsSpan(0, header).SequenceEqual(rendered.AsSpan(0, header)))
            return false;
        for (var i = header; i < shipped.Length; i += 2)
            if (Math.Abs(BitConverter.ToInt16(shipped, i) - BitConverter.ToInt16(rendered, i)) > 1)
                return false;
        return true;
    }
}
