namespace JibbersAsyncContent.Preparation;

/// <summary>Visits every pixel once, in full-width row batches, including prepared mip levels.</summary>
internal sealed class TextureUploadPlan
{
    public readonly record struct Batch(int Level, int Width, int Row, int Rows, int ByteOffset)
    {
        public int Pixels => checked(Width * Rows);
    }
    readonly bool mipChain;
    readonly int batchBytes;
    int width, height, level, row, offset;
    bool complete;

    public TextureUploadPlan(TexturePixels pixels, int batchBytes = 1024 * 1024)
    {
        if (pixels.Width <= 0 || pixels.Height <= 0 || batchBytes < 4) throw new ArgumentOutOfRangeException(nameof(batchBytes));
        var required = pixels.HasMipChain ? MipmapGenerator.ByteCount(pixels.Width, pixels.Height) : pixels.BaseBytes;
        if (pixels.Rgba.Length < required) throw new ArgumentException("Incomplete texture pixels.", nameof(pixels));
        width = pixels.Width;
        height = pixels.Height;
        mipChain = pixels.HasMipChain;
        this.batchBytes = batchBytes;
    }

    public bool TryNext(out Batch batch)
    {
        if (complete) { batch = default; return false; }
        var rows = Math.Min(height - row, Math.Max(1, batchBytes / checked(width * 4)));
        batch = new(level, width, row, rows, offset);
        offset = checked(offset + width * rows * 4);
        row += rows;
        if (row == height)
        {
            if (!mipChain || (width == 1 && height == 1)) complete = true;
            else { width = Math.Max(1, width / 2); height = Math.Max(1, height / 2); level++; row = 0; }
        }
        return true;
    }
}
