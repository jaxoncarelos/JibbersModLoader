namespace JibbersAsyncContent.Preparation;

internal enum MipmapFilter { None, Floor, Rounded, Even, PairFloorRows, PairRoundRows, PairFloorColumns, PairRoundColumns, Srgb, Gamma22 }

/// <summary>Used only for opaque power-of-two images after exact comparison with this player's native mip generator.</summary>
internal static class MipmapGenerator
{
    static int selected;
    static readonly double[] srgb = Enumerable.Range(0, 256).Select(value =>
    {
        var color = value / 255d;
        return color <= .04045 ? color / 12.92 : Math.Pow((color + .055) / 1.055, 2.4);
    }).ToArray();
    static readonly double[] gamma22 = Enumerable.Range(0, 256).Select(value => Math.Pow(value / 255d, 2.2)).ToArray();
    public static MipmapFilter Selected { get => (MipmapFilter)Volatile.Read(ref selected); set => Volatile.Write(ref selected, (int)value); }
    public static bool Eligible(int width, int height, MipmapFilter filter) => filter != MipmapFilter.None &&
        width > 0 && height > 0 && (width & (width - 1)) == 0 && (height & (height - 1)) == 0 && (width > 1 || height > 1);
    public static int ByteCount(int width, int height)
    {
        var total = 0;
        while (true)
        {
            total = checked(total + width * height * 4);
            if (width == 1 && height == 1) return total;
            width = Math.Max(1, width / 2); height = Math.Max(1, height / 2);
        }
    }
    public static TexturePixels Complete(TexturePixels pixels, MipmapFilter filter, CancellationToken cancellation)
    {
        if (!Eligible(pixels.Width, pixels.Height, filter)) return pixels;
        var baseBytes = pixels.BaseBytes;
        for (var offset = 3; offset < baseBytes; offset += 4)
            if (pixels.Rgba[offset] != 255) return pixels; // Transparent images retain native filtering.
        var data = pixels.Rgba;
        var length = ByteCount(pixels.Width, pixels.Height);
        if (data.Length != length)
        {
            data = GC.AllocateUninitializedArray<byte>(length);
            pixels.Rgba.AsSpan(0, baseBytes).CopyTo(data);
        }
        var width = pixels.Width; var height = pixels.Height; var source = 0;
        while (width > 1 || height > 1)
        {
            var nextWidth = Math.Max(1, width / 2); var nextHeight = Math.Max(1, height / 2);
            var destination = source + width * height * 4;
            for (var y = 0; y < nextHeight; y++)
            {
                cancellation.ThrowIfCancellationRequested();
                for (var x = 0; x < nextWidth; x++)
                {
                    var left = x * 2; var right = Math.Min(left + 1, width - 1);
                    var top = y * 2; var bottom = Math.Min(top + 1, height - 1);
                    for (var channel = 0; channel < 4; channel++)
                    {
                        var a = data[source + (top * width + left) * 4 + channel];
                        var b = data[source + (top * width + right) * 4 + channel];
                        var c = data[source + (bottom * width + left) * 4 + channel];
                        var d = data[source + (bottom * width + right) * 4 + channel];
                        data[destination + (y * nextWidth + x) * 4 + channel] = channel == 3 ? (byte)255 : Average(a, b, c, d, filter);
                    }
                }
            }
            source = destination; width = nextWidth; height = nextHeight;
        }
        return new(pixels.Width, pixels.Height, data, true);
    }

    static byte Average(byte a, byte b, byte c, byte d, MipmapFilter filter)
    {
        var sum = a + b + c + d;
        return filter switch
        {
            MipmapFilter.Floor => (byte)(sum / 4),
            MipmapFilter.Rounded => (byte)((sum + 2) / 4),
            MipmapFilter.Even => (byte)Math.Round(sum / 4d, MidpointRounding.ToEven),
            MipmapFilter.PairFloorRows => (byte)(((a + b) / 2 + (c + d) / 2) / 2),
            MipmapFilter.PairRoundRows => (byte)(((a + b + 1) / 2 + (c + d + 1) / 2 + 1) / 2),
            MipmapFilter.PairFloorColumns => (byte)(((a + c) / 2 + (b + d) / 2) / 2),
            MipmapFilter.PairRoundColumns => (byte)(((a + c + 1) / 2 + (b + d + 1) / 2 + 1) / 2),
            MipmapFilter.Srgb => SrgbByte((srgb[a] + srgb[b] + srgb[c] + srgb[d]) * .25),
            MipmapFilter.Gamma22 => (byte)Math.Clamp((int)Math.Round(Math.Pow((gamma22[a] + gamma22[b] + gamma22[c] + gamma22[d]) * .25, 1 / 2.2) * 255), 0, 255),
            _ => throw new InvalidOperationException("No validated mip filter.")
        };
    }
    static byte SrgbByte(double linear)
    {
        var encoded = linear <= .0031308 ? linear * 12.92 : 1.055 * Math.Pow(linear, 1 / 2.4) - .055;
        return (byte)Math.Clamp((int)Math.Round(encoded * 255), 0, 255);
    }
}
