namespace Spotify;

/// <summary>
/// A small anti-aliased drawing surface (premultiplied RGBA floats, top-left origin, y down) for the
/// record's parts: rounded boxes, circles, rings, triangles. <see cref="ToRgba"/> gives straight
/// RGBA bytes with Unity's bottom-up rows. No game calls.
/// </summary>
internal sealed class Pixels
{
    public readonly int W, H;
    readonly float[] px;

    public Pixels(int w, int h)
    {
        W = Math.Max(1, w);
        H = Math.Max(1, h);
        px = new float[W * H * 4];
    }

    public readonly record struct Rgba(float R, float G, float B, float A)
    {
        public static Rgba Of(int r, int g, int b, int a = 255) => new(r / 255f, g / 255f, b / 255f, a / 255f);
    }

    void Blend(int x, int y, Rgba c, float coverage)
    {
        var a = c.A * coverage;
        if (a <= 0 || x < 0 || y < 0 || x >= W || y >= H) return;
        var i = (y * W + x) * 4;
        var keep = 1 - a;
        px[i] = c.R * a + px[i] * keep;
        px[i + 1] = c.G * a + px[i + 1] * keep;
        px[i + 2] = c.B * a + px[i + 2] * keep;
        px[i + 3] = a + px[i + 3] * keep;
    }

    static float Cover(float d) => Math.Clamp(0.5f - d, 0, 1);

    /// <summary>Fills every pixel by the signed distance <paramref name="d"/> gives it (negative inside), within a box.</summary>
    void Shape(float x0, float y0, float x1, float y1, Rgba c, Func<float, float, float> d)
    {
        for (var y = Math.Max(0, (int)MathF.Floor(y0 - 1)); y < Math.Min(H, (int)MathF.Ceiling(y1 + 1)); y++)
            for (var x = Math.Max(0, (int)MathF.Floor(x0 - 1)); x < Math.Min(W, (int)MathF.Ceiling(x1 + 1)); x++)
                Blend(x, y, c, Cover(d(x + 0.5f, y + 0.5f)));
    }

    static float Box(float x, float y, float bx, float by, float bw, float bh, float r)
    {
        var qx = MathF.Abs(x - (bx + bw / 2)) - (bw / 2 - r);
        var qy = MathF.Abs(y - (by + bh / 2)) - (bh / 2 - r);
        return MathF.Sqrt(MathF.Max(qx, 0) * MathF.Max(qx, 0) + MathF.Max(qy, 0) * MathF.Max(qy, 0)) + MathF.Min(MathF.Max(qx, qy), 0) - r;
    }

    public void RoundRect(float x, float y, float w, float h, float r, Rgba c) =>
        Shape(x, y, x + w, y + h, c, (px, py) => Box(px, py, x, y, w, h, Math.Min(r, Math.Min(w, h) / 2)));

    public void Circle(float cx, float cy, float r, Rgba c) =>
        Shape(cx - r, cy - r, cx + r, cy + r, c, (x, y) => MathF.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy)) - r);

    public void Ring(float cx, float cy, float r, float width, Rgba c) =>
        Shape(cx - r - width, cy - r - width, cx + r + width, cy + r + width, c, (x, y) => MathF.Abs(MathF.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy)) - r) - width / 2);

    /// <summary>A filled triangle (any winding).</summary>
    public void Triangle(float ax, float ay, float bx, float by, float cx, float cy, Rgba c)
    {
        static float Edge(float x, float y, float x0, float y0, float x1, float y1)
        {
            // Distance to the edge's line, positive on the right of a→b.
            var (dx, dy) = (x1 - x0, y1 - y0);
            return ((x - x0) * dy - (y - y0) * dx) / MathF.Max(MathF.Sqrt(dx * dx + dy * dy), 1e-6f);
        }
        var sign = Edge(cx, cy, ax, ay, bx, by) > 0 ? -1 : 1;
        Shape(MathF.Min(ax, MathF.Min(bx, cx)), MathF.Min(ay, MathF.Min(by, cy)), MathF.Max(ax, MathF.Max(bx, cx)), MathF.Max(ay, MathF.Max(by, cy)), c,
            (x, y) => sign * MathF.Max(Edge(x, y, ax, ay, bx, by), MathF.Max(Edge(x, y, bx, by, cx, cy), Edge(x, y, cx, cy, ax, ay))));
    }

    /// <summary>A wedge from the centre between two angles (radians), out to <paramref name="r"/>.</summary>
    public void Wedge(float cx, float cy, float r, float a0, float a1, Rgba c) =>
        Triangle(cx, cy, cx + r * MathF.Cos(a0), cy + r * MathF.Sin(a0), cx + r * MathF.Cos(a1), cy + r * MathF.Sin(a1), c);

    /// <summary>Straight RGBA bytes, rows bottom-up (as Unity's textures).</summary>
    public byte[] ToRgba()
    {
        var bytes = new byte[px.Length];
        for (var y = 0; y < H; y++)
        {
            var to = (H - 1 - y) * W * 4;
            for (var i = y * W * 4; i < (y + 1) * W * 4; i += 4, to += 4)
            {
                var a = px[i + 3];
                if (a <= 0) continue;
                for (var k = 0; k < 3; k++)
                    bytes[to + k] = (byte)Math.Clamp(MathF.Round(px[i + k] / a * 255), 0, 255);
                bytes[to + 3] = (byte)Math.Clamp(MathF.Round(a * 255), 0, 255);
            }
        }
        return bytes;
    }

    /// <summary>The alpha at a pixel (0..1), for tests.</summary>
    public float AlphaAt(int x, int y) => px[(y * W + x) * 4 + 3];
}
