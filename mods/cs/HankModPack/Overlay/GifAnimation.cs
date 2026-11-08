namespace RickBackflip;

/// <summary>
/// Un GIF décodé en images complètes (RGBA, lignes de bas en haut comme les textures Unity).
/// Décodeur minimal sans dépendance : palettes globales/locales, transparence, entrelacement,
/// méthodes de disposal 0 à 3.
/// </summary>
internal sealed class GifAnimation
{
    public int Width { get; }
    public int Height { get; }
    public IReadOnlyList<byte[]> Frames { get; }
    /// <summary>Durée de chaque image en millisecondes.</summary>
    public IReadOnlyList<int> DelaysMs { get; }
    public int TotalMs { get; }

    GifAnimation(int width, int height, List<byte[]> frames, List<int> delays)
    {
        Width = width;
        Height = height;
        Frames = frames;
        DelaysMs = delays;
        TotalMs = Math.Max(1, delays.Sum());
    }

    /// <summary>Animation construite en code (bannières) : images RGBA bas-en-haut de même taille.</summary>
    public static GifAnimation FromFrames(int width, int height, List<byte[]> frames, List<int> delays) =>
        new(width, height, frames, delays);

    /// <summary>Index de l'image à afficher après <paramref name="elapsedMs"/> (le GIF boucle).</summary>
    public int FrameAt(long elapsedMs)
    {
        var t = (int)(elapsedMs % TotalMs);
        for (var i = 0; i < DelaysMs.Count; i++)
        {
            if (t < DelaysMs[i]) return i;
            t -= DelaysMs[i];
        }
        return DelaysMs.Count - 1;
    }

    const long MemoryBudget = 300L * 1024 * 1024;

    public static GifAnimation Load(string path, Action<string>? log = null)
    {
        if (new FileInfo(path).Length > 32L * 1024 * 1024) throw new InvalidDataException("GIF files must be at most 32 MiB.");
        var data = File.ReadAllBytes(path);
        return Decode(data, log);
    }

    static GifAnimation Decode(byte[] d, Action<string>? log)
    {
        if (d.Length < 13 || d[0] != 'G' || d[1] != 'I' || d[2] != 'F')
            throw new InvalidDataException("not a GIF file");

        var pos = 6;
        int width = U16(d, pos), height = U16(d, pos + 2);
        var packed = d[pos + 4];
        pos += 7; // largeur, hauteur, flags, fond, ratio
        if (width <= 0 || height <= 0 || width > 2048 || height > 2048)
            throw new InvalidDataException($"unsupported GIF size {width}x{height}");

        byte[]? globalPalette = null;
        if ((packed & 0x80) != 0)
        {
            var n = 3 * (1 << ((packed & 7) + 1));
            globalPalette = Slice(d, pos, n);
            pos += n;
        }

        var canvas = new byte[width * height * 4];
        byte[]? restore = null;
        var frames = new List<byte[]>();
        var delays = new List<int>();

        // Graphic Control Extension en attente pour l'image suivante.
        int disposal = 0, delayMs = 100, transparent = -1;
        // Disposal de l'image précédente, à appliquer avant de dessiner la suivante.
        int prevDisposal = 0;
        (int X, int Y, int W, int H) prevRect = (0, 0, 0, 0);

        while (pos < d.Length)
        {
            var block = d[pos++];
            if (block == 0x3B) break; // trailer

            if (block == 0x21) // extension
            {
                var label = d[pos++];
                if (label == 0xF9 && pos + 5 < d.Length)
                {
                    var size = d[pos]; // 4
                    var flags = d[pos + 1];
                    var cs = U16(d, pos + 2);
                    disposal = (flags >> 2) & 7;
                    delayMs = cs <= 1 ? 100 : cs * 10; // comme les navigateurs : 0/1 cs => 100 ms
                    transparent = (flags & 1) != 0 ? d[pos + 4] : -1;
                    pos += 1 + size;
                    pos = SkipSubBlocks(d, pos);
                }
                else
                {
                    pos = SkipSubBlocks(d, pos);
                }
                continue;
            }

            if (block != 0x2C) continue; // octet inattendu : on ignore

            // Image descriptor
            int ix = U16(d, pos), iy = U16(d, pos + 2), iw = U16(d, pos + 4), ih = U16(d, pos + 6);
            var iflags = d[pos + 8];
            pos += 9;

            var palette = globalPalette;
            if ((iflags & 0x80) != 0)
            {
                var n = 3 * (1 << ((iflags & 7) + 1));
                palette = Slice(d, pos, n);
                pos += n;
            }
            var interlaced = (iflags & 0x40) != 0;

            var minCode = d[pos++];
            // Concatène les sous-blocs de données LZW.
            using var ms = new MemoryStream();
            while (pos < d.Length)
            {
                int len = d[pos++];
                if (len == 0) break;
                ms.Write(d, pos, Math.Min(len, d.Length - pos));
                pos += len;
            }
            var indices = LzwDecode(ms.ToArray(), minCode, iw * ih);

            // Applique le disposal de l'image précédente.
            if (prevDisposal == 2) Clear(canvas, width, height, prevRect);
            else if (prevDisposal == 3 && restore is not null) Buffer.BlockCopy(restore, 0, canvas, 0, canvas.Length);

            if (disposal == 3) restore = (byte[])canvas.Clone();

            // Dessine l'image sur le canevas.
            if (palette is not null)
            {
                for (var row = 0; row < ih; row++)
                {
                    var srcRow = interlaced ? InterlacedRow(row, ih) : row;
                    var y = iy + row;
                    if (y < 0 || y >= height) continue;
                    for (var col = 0; col < iw; col++)
                    {
                        var x = ix + col;
                        if (x < 0 || x >= width) continue;
                        int index = indices[srcRow * iw + col];
                        if (index == transparent) continue;
                        var p = index * 3;
                        if (p + 2 >= palette.Length) continue;
                        var o = (y * width + x) * 4;
                        canvas[o] = palette[p];
                        canvas[o + 1] = palette[p + 1];
                        canvas[o + 2] = palette[p + 2];
                        canvas[o + 3] = 255;
                    }
                }
            }

            frames.Add(FlipVertically(canvas, width, height));
            delays.Add(delayMs);

            prevDisposal = disposal;
            prevRect = (ix, iy, iw, ih);
            // Réinitialise l'extension : elle ne vaut que pour l'image qui la suit.
            disposal = 0;
            delayMs = 100;
            transparent = -1;

            if ((long)frames.Count * width * height * 4 > MemoryBudget)
            {
                log?.Invoke($"GIF too large: keeping the first {frames.Count} frames.");
                break;
            }
        }

        if (frames.Count == 0) throw new InvalidDataException("GIF has no frames");
        return new GifAnimation(width, height, frames, delays);
    }

    static int U16(byte[] d, int p) => d[p] | (d[p + 1] << 8);

    static byte[] Slice(byte[] d, int pos, int n)
    {
        var a = new byte[n];
        Buffer.BlockCopy(d, pos, a, 0, Math.Min(n, Math.Max(0, d.Length - pos)));
        return a;
    }

    static int SkipSubBlocks(byte[] d, int pos)
    {
        while (pos < d.Length)
        {
            int len = d[pos++];
            if (len == 0) break;
            pos += len;
        }
        return pos;
    }

    static void Clear(byte[] canvas, int width, int height, (int X, int Y, int W, int H) r)
    {
        for (var y = Math.Max(0, r.Y); y < Math.Min(height, r.Y + r.H); y++)
            for (var x = Math.Max(0, r.X); x < Math.Min(width, r.X + r.W); x++)
                Array.Clear(canvas, (y * width + x) * 4, 4);
    }

    static byte[] FlipVertically(byte[] rgba, int width, int height)
    {
        var stride = width * 4;
        var flipped = new byte[rgba.Length];
        for (var y = 0; y < height; y++)
            Buffer.BlockCopy(rgba, y * stride, flipped, (height - 1 - y) * stride, stride);
        return flipped;
    }

    /// <summary>Ligne réelle de la <paramref name="row"/>-ième ligne stockée d'une image entrelacée.</summary>
    static int InterlacedRow(int row, int height)
    {
        var count = 0;
        foreach (var (start, step) in new[] { (0, 8), (4, 8), (2, 4), (1, 2) })
        {
            for (var y = start; y < height; y += step)
            {
                if (count++ == row) return y;
            }
        }
        return row;
    }

    /// <summary>Décompression LZW des GIF (codes de 2 à 12 bits). Remplit un tableau d'indices de palette.</summary>
    static byte[] LzwDecode(byte[] data, int minCode, int pixelCount)
    {
        var output = new byte[pixelCount];
        if (minCode < 2 || minCode > 11) return output;

        var clear = 1 << minCode;
        var eoi = clear + 1;
        var prefix = new short[4096];
        var suffix = new byte[4096];
        var stack = new byte[4097];
        for (var i = 0; i < clear; i++) suffix[i] = (byte)i;

        var codeSize = minCode + 1;
        var next = eoi + 1;
        int old = -1, datum = 0, bits = 0, pos = 0, op = 0;
        byte first = 0;

        while (op < pixelCount)
        {
            while (bits < codeSize)
            {
                if (pos >= data.Length) return output;
                datum |= data[pos++] << bits;
                bits += 8;
            }
            var code = datum & ((1 << codeSize) - 1);
            datum >>= codeSize;
            bits -= codeSize;

            if (code == clear)
            {
                codeSize = minCode + 1;
                next = eoi + 1;
                old = -1;
                continue;
            }
            if (code == eoi) break;

            if (old == -1)
            {
                if (code >= clear) return output; // flux invalide
                output[op++] = (byte)code;
                first = (byte)code;
                old = code;
                continue;
            }

            var inCode = code;
            var sp = 0;
            if (code >= next)
            {
                if (code > next) return output; // flux invalide
                stack[sp++] = first;
                code = old;
            }
            while (code >= clear)
            {
                if (code >= 4096 || sp >= stack.Length - 1) return output;
                stack[sp++] = suffix[code];
                code = prefix[code];
            }
            first = suffix[code];
            stack[sp++] = first;

            if (next < 4096)
            {
                prefix[next] = (short)old;
                suffix[next] = first;
                next++;
                if ((next & ((1 << codeSize) - 1)) == 0 && codeSize < 12) codeSize++;
            }
            old = inCode;

            while (sp > 0 && op < pixelCount) output[op++] = stack[--sp];
        }
        return output;
    }
}
