using System.Buffers.Binary;
using System.IO.Compression;

namespace JibbersAsyncContent.Preparation;

/// <summary>Bounded PNG-to-RGBA8 preparation without Unity or OS image objects.</summary>
internal static class PngDecoder
{
    static readonly uint[] crcTable = BuildCrcTable();
    static readonly (int X, int Y, int Dx, int Dy)[] adam7 = [(0, 0, 8, 8), (4, 0, 8, 8), (0, 4, 4, 8), (2, 0, 4, 4), (0, 2, 2, 4), (1, 0, 2, 2), (0, 1, 1, 2)];

    public static TexturePixels Decode(byte[] encoded, CancellationToken cancellation)
    {
        var (width, height) = Dimensions(encoded);
        var depth = encoded[24];
        var color = encoded[25];
        var channels = color switch { 0 => 1, 2 => 3, 3 => 1, 4 => 2, 6 => 4, _ => throw new InvalidDataException("Invalid PNG color type.") };
        byte[]? palette = null, transparency = null;
        var ended = false;
        using var compressed = new MemoryStream();
        for (var offset = 8; offset < encoded.Length;)
        {
            cancellation.ThrowIfCancellationRequested();
            if (encoded.Length - offset < 12) throw new InvalidDataException("Truncated PNG chunk.");
            var length = BinaryPrimitives.ReadInt32BigEndian(encoded.AsSpan(offset, 4));
            if (length < 0 || length > encoded.Length - offset - 12) throw new InvalidDataException("PNG chunk length.");
            var type = encoded.AsSpan(offset + 4, 4);
            var data = encoded.AsSpan(offset + 8, length);
            uint crc = uint.MaxValue;
            foreach (var value in encoded.AsSpan(offset + 4, length + 4)) crc = (crc >> 8) ^ crcTable[(crc ^ value) & 255];
            if (~crc != BinaryPrimitives.ReadUInt32BigEndian(encoded.AsSpan(offset + 8 + length, 4))) throw new InvalidDataException("PNG checksum mismatch.");
            if (type.SequenceEqual("IHDR"u8))
            {
                if (offset != 8 || length != 13) throw new InvalidDataException("Repeated PNG header.");
            }
            else if (type.SequenceEqual("PLTE"u8))
            {
                if (length is 0 or > 768 || length % 3 != 0) throw new InvalidDataException("PNG palette length.");
                palette = data.ToArray();
            }
            else if (type.SequenceEqual("tRNS"u8)) transparency = data.ToArray();
            else if (type.SequenceEqual("IDAT"u8)) compressed.Write(data);
            else if (type.SequenceEqual("IEND"u8)) { ended = length == 0; break; }
            else if ((type[0] & 32) == 0) throw new NotSupportedException("Unknown critical PNG chunk.");
            offset += length + 12;
        }
        if (!ended || compressed.Length == 0 || (color == 3 && palette is null)) throw new InvalidDataException("Incomplete PNG.");
        if (transparency is not null && ((color == 0 && transparency.Length != 2) || (color == 2 && transparency.Length != 6) ||
            (color == 3 && transparency.Length > palette!.Length / 3) || color is 4 or 6)) throw new InvalidDataException("Invalid PNG transparency.");
        var filterMode = MipmapGenerator.Selected;
        var rgba = GC.AllocateUninitializedArray<byte>(MipmapGenerator.Eligible(width, height, filterMode) ? MipmapGenerator.ByteCount(width, height) : checked(width * height * 4));
        compressed.Position = 0;
        using var inflater = new ZLibStream(compressed, CompressionMode.Decompress);
        var passes = encoded[28] == 0 ? new[] { (X: 0, Y: 0, Dx: 1, Dy: 1) } : adam7;
        foreach (var pass in passes)
        {
            var passWidth = width > pass.X ? (width - pass.X + pass.Dx - 1) / pass.Dx : 0;
            var passHeight = height > pass.Y ? (height - pass.Y + pass.Dy - 1) / pass.Dy : 0;
            if (passWidth == 0 || passHeight == 0) continue;
            var rowBytes = checked((passWidth * channels * depth + 7) / 8);
            var previous = new byte[rowBytes];
            var row = new byte[rowBytes];
            var bytesPerPixel = Math.Max(1, (channels * depth + 7) / 8);
            for (var y = 0; y < passHeight; y++)
            {
                cancellation.ThrowIfCancellationRequested();
                var filter = inflater.ReadByte();
                if (filter < 0) throw new InvalidDataException("Truncated PNG rows.");
                inflater.ReadExactly(row);
                Unfilter(row, previous, bytesPerPixel, filter);
                var sample = 0;
                for (var x = 0; x < passWidth; x++)
                {
                    var destination = ((height - 1 - (pass.Y + y * pass.Dy)) * width + pass.X + x * pass.Dx) * 4;
                    int red, green, blue, alpha = (1 << depth) - 1;
                    if (color == 3)
                    {
                        var index = Sample(row, ref sample, depth);
                        if (index >= palette!.Length / 3) throw new InvalidDataException("PNG palette index.");
                        rgba[destination] = palette[index * 3]; rgba[destination + 1] = palette[index * 3 + 1]; rgba[destination + 2] = palette[index * 3 + 2];
                        rgba[destination + 3] = transparency is not null && index < transparency.Length ? transparency[index] : (byte)255;
                        continue;
                    }
                    red = Sample(row, ref sample, depth);
                    if (color is 0 or 4) green = blue = red;
                    else { green = Sample(row, ref sample, depth); blue = Sample(row, ref sample, depth); }
                    if (color is 4 or 6) alpha = Sample(row, ref sample, depth);
                    if (transparency is not null && red == BinaryPrimitives.ReadUInt16BigEndian(transparency) &&
                        (color == 0 || (green == BinaryPrimitives.ReadUInt16BigEndian(transparency.AsSpan(2)) && blue == BinaryPrimitives.ReadUInt16BigEndian(transparency.AsSpan(4))))) alpha = 0;
                    rgba[destination] = ToByte(red, depth); rgba[destination + 1] = ToByte(green, depth); rgba[destination + 2] = ToByte(blue, depth); rgba[destination + 3] = ToByte(alpha, depth);
                }
                (row, previous) = (previous, row);
            }
        }
        if (inflater.ReadByte() != -1) throw new InvalidDataException("PNG exceeds its declared dimensions.");
        return MipmapGenerator.Complete(new(width, height, rgba), filterMode, cancellation);
    }

    static int Sample(byte[] row, ref int index, int depth)
    {
        var bit = index++ * depth;
        if (depth == 16) return (row[bit / 8] << 8) | row[bit / 8 + 1];
        if (depth == 8) return row[bit / 8];
        return (row[bit / 8] >> (8 - depth - bit % 8)) & ((1 << depth) - 1);
    }
    static byte ToByte(int value, int depth) => depth == 16 ? (byte)(value >> 8) : (byte)(value * 255 / ((1 << depth) - 1));
    internal static void Unfilter(Span<byte> row, ReadOnlySpan<byte> previous, int stride, int filter)
    {
        if (filter is < 0 or > 4) throw new InvalidDataException("PNG filter.");
        for (var index = 0; index < row.Length; index++)
        {
            var left = index >= stride ? row[index - stride] : 0;
            var above = previous[index];
            var upperLeft = index >= stride ? previous[index - stride] : 0;
            var prediction = filter switch { 0 => 0, 1 => left, 2 => above, 3 => (left + above) / 2, _ => Paeth(left, above, upperLeft) };
            row[index] = unchecked((byte)(row[index] + prediction));
        }
    }
    static int Paeth(int left, int above, int corner)
    {
        var prediction = left + above - corner;
        var a = Math.Abs(prediction - left); var b = Math.Abs(prediction - above); var c = Math.Abs(prediction - corner);
        return a <= b && a <= c ? left : b <= c ? above : corner;
    }
    public static (int Width, int Height) Dimensions(ReadOnlySpan<byte> png)
    {
        ReadOnlySpan<byte> signature = [137, 80, 78, 71, 13, 10, 26, 10];
        if (png.Length < 33 || !png[..8].SequenceEqual(signature) || !png.Slice(12, 4).SequenceEqual("IHDR"u8) || BinaryPrimitives.ReadUInt32BigEndian(png.Slice(8, 4)) != 13) throw new NotSupportedException("Not a supported PNG header.");
        var width = BinaryPrimitives.ReadInt32BigEndian(png.Slice(16, 4));
        var height = BinaryPrimitives.ReadInt32BigEndian(png.Slice(20, 4));
        if (width <= 0 || height <= 0 || width > 8192 || height > 8192 || (long)width * height > ContentLimits.ImagePixels) throw new NotSupportedException("PNG exceeds the preparation budget.");
        var depth = png[24];
        var valid = png[25] switch { 0 => depth is 1 or 2 or 4 or 8 or 16, 2 or 4 or 6 => depth is 8 or 16, 3 => depth is 1 or 2 or 4 or 8, _ => false };
        if (!valid || png[26] != 0 || png[27] != 0 || png[28] > 1) throw new NotSupportedException("Invalid PNG encoding.");
        return (width, height);
    }
    static uint[] BuildCrcTable()
    {
        var result = new uint[256];
        for (uint index = 0; index < result.Length; index++)
        {
            var value = index;
            for (var bit = 0; bit < 8; bit++) value = (value >> 1) ^ (0xedb88320u & (uint)-(int)(value & 1));
            result[index] = value;
        }
        return result;
    }
}
