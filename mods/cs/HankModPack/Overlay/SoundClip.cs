using System.Text;

namespace RickBackflip;

/// <summary>
/// Un son décodé en échantillons 16 bits (mp3 via NLayer, wav PCM). Le décodage se fait en C# pur :
/// aucun codec Windows n'est nécessaire (le pilote MCI « mpegvideo » n'existe pas pour les programmes 64 bits).
/// </summary>
internal sealed class SoundClip
{
    /// <summary>Échantillons entrelacés (gauche, droite, gauche...).</summary>
    public short[] Pcm { get; private init; } = Array.Empty<short>();
    public int SampleRate { get; private init; }
    public int Channels { get; private init; }
    public double Seconds => SampleRate <= 0 || Channels <= 0 ? 0 : Pcm.Length / (double)(Channels * SampleRate);

    /// <summary>On ne décode que le début : le réglage « Max duration » plafonne à 60 s.</summary>
    public const int MaxSeconds = 90;

    public static SoundClip Load(string path)
    {
        if (new FileInfo(path).Length > 64L * 1024 * 1024) throw new InvalidDataException("Audio files must be at most 64 MiB.");
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".mp3" => LoadMp3(path),
            ".wav" => LoadWav(path),
            _ => throw new NotSupportedException($"unsupported sound format \"{ext}\" (use .mp3 or .wav)"),
        };
    }

    // ---------------------------------------------------------------- mp3

    static SoundClip LoadMp3(string path)
    {
        using var mpeg = new NLayer.MpegFile(path);
        int rate = mpeg.SampleRate, channels = mpeg.Channels;
        if (rate < 8000 || rate > 192000 || channels is < 1 or > 2) throw new InvalidDataException("not a valid mp3 file");

        var limit = rate * channels * MaxSeconds;
        var output = new short[Math.Min(limit, Math.Max(rate * channels, (int)(mpeg.Length / 4 + rate)))];
        var count = 0;
        var buffer = new float[4096 * channels];
        while (count < limit)
        {
            var read = mpeg.ReadSamples(buffer, 0, buffer.Length);
            if (read <= 0) break;
            read = Math.Min(read, limit - count);
            if (count + read > output.Length) Array.Resize(ref output, Math.Min(limit, Math.Max(output.Length * 2, count + read)));
            for (var i = 0; i < read; i++) output[count + i] = ToShort(buffer[i]);
            count += read;
        }
        if (count == 0) throw new InvalidDataException("the mp3 file has no audio");
        Array.Resize(ref output, count);
        return new SoundClip { Pcm = output, SampleRate = rate, Channels = channels };
    }

    static short ToShort(float sample)
    {
        var v = sample * 32767f;
        return (short)(v > 32767f ? 32767 : v < -32768f ? -32768 : v);
    }

    // ---------------------------------------------------------------- wav

    static SoundClip LoadWav(string path)
    {
        var d = File.ReadAllBytes(path);
        if (d.Length < 44 || Ascii(d, 0) != "RIFF" || Ascii(d, 8) != "WAVE") throw new InvalidDataException("not a WAV file");

        int format = 0, channels = 0, rate = 0, bits = 0, dataPos = -1, dataLen = 0;
        var pos = 12;
        while (pos + 8 <= d.Length)
        {
            var id = Ascii(d, pos);
            var len = BitConverter.ToInt32(d, pos + 4);
            pos += 8;
            if (len < 0 || len > d.Length - pos) len = d.Length - pos; // « data » d'un fichier tronqué ou en flux
            if (id == "fmt " && len >= 16)
            {
                format = BitConverter.ToUInt16(d, pos);
                channels = BitConverter.ToUInt16(d, pos + 2);
                rate = BitConverter.ToInt32(d, pos + 4);
                bits = BitConverter.ToUInt16(d, pos + 14);
                if (format == 0xFFFE && len >= 26) format = BitConverter.ToUInt16(d, pos + 24); // WAVE_FORMAT_EXTENSIBLE
            }
            else if (id == "data")
            {
                (dataPos, dataLen) = (pos, len);
                break;
            }
            pos += len + (len & 1);
        }
        if (dataPos < 0 || channels is < 1 or > 2 || rate < 8000 || rate > 192000) throw new InvalidDataException("unreadable WAV header");
        if (!((format == 1 && bits is 8 or 16 or 24 or 32) || (format == 3 && bits == 32)))
            throw new NotSupportedException($"WAV format not supported (format {format}, {bits} bits): use 16-bit PCM");

        var bytesPer = bits / 8;
        var total = Math.Min(dataLen / bytesPer, rate * channels * MaxSeconds);
        total -= total % channels;
        var pcm = new short[total];
        for (var i = 0; i < total; i++)
        {
            var p = dataPos + i * bytesPer;
            pcm[i] = (format, bits) switch
            {
                (1, 8) => (short)((d[p] - 128) << 8),
                (1, 16) => BitConverter.ToInt16(d, p),
                (1, 24) => (short)(((d[p + 2] << 24) | (d[p + 1] << 16) | (d[p] << 8)) >> 16),
                (1, 32) => (short)(BitConverter.ToInt32(d, p) >> 16),
                _ => ToShort(BitConverter.ToSingle(d, p)),
            };
        }
        return new SoundClip { Pcm = pcm, SampleRate = rate, Channels = channels };
    }

    static string Ascii(byte[] d, int pos) => Encoding.ASCII.GetString(d, pos, 4);

    // ---------------------------------------------------------------- sortie

    /// <summary>Un fichier WAV (16 bits) en mémoire, volume appliqué (0 à 1).</summary>
    public byte[] BuildWav(float gain)
    {
        gain = Math.Clamp(gain, 0f, 1f);
        var dataBytes = Pcm.Length * 2;
        var wav = new byte[44 + dataBytes];
        using (var w = new BinaryWriter(new MemoryStream(wav)))
        {
            w.Write(Encoding.ASCII.GetBytes("RIFF"));
            w.Write(36 + dataBytes);
            w.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
            w.Write(16);
            w.Write((short)1);                       // PCM
            w.Write((short)Channels);
            w.Write(SampleRate);
            w.Write(SampleRate * Channels * 2);      // octets par seconde
            w.Write((short)(Channels * 2));          // alignement
            w.Write((short)16);
            w.Write(Encoding.ASCII.GetBytes("data"));
            w.Write(dataBytes);
        }
        var scaled = new short[Pcm.Length];
        for (var i = 0; i < Pcm.Length; i++) scaled[i] = (short)Math.Clamp((int)MathF.Round(Pcm[i] * gain), short.MinValue, short.MaxValue);
        Buffer.BlockCopy(scaled, 0, wav, 44, dataBytes);
        return wav;
    }
}
