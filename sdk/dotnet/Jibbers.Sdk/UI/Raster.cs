namespace Jibbers.Sdk.UI;

/// <summary>
/// A small software rasterizer for overlay backgrounds: rounded rectangles, rings, arcs and soft
/// shadows, drawn from signed distances so every edge is anti-aliased. Coordinates are frame units
/// (top-left origin, y down); <c>scale</c> pixels per unit. Pixels are premultiplied RGBA floats.
/// </summary>
internal sealed class Raster
{
    public readonly int Width, Height;
    readonly float scale;
    readonly float[] px;

    public Raster(float width, float height, float scale)
    {
        this.scale = scale;
        Width = Math.Max(1, (int)MathF.Ceiling(width * scale));
        Height = Math.Max(1, (int)MathF.Ceiling(height * scale));
        px = new float[Width * Height * 4];
    }

    void Blend(int x, int y, Color c, float coverage)
    {
        var a = c.A * coverage;
        if (a <= 0) return;
        var i = (y * Width + x) * 4;
        var keep = 1 - a;
        px[i] = c.R * a + px[i] * keep;
        px[i + 1] = c.G * a + px[i + 1] * keep;
        px[i + 2] = c.B * a + px[i + 2] * keep;
        px[i + 3] = a + px[i + 3] * keep;
    }

    /// <summary>Visits every pixel centre inside a box (frame units, grown by <paramref name="pad"/>), in pixel space.</summary>
    void Each(float x, float y, float w, float h, float pad, Action<int, int, float, float> body)
    {
        var x0 = Math.Max(0, (int)MathF.Floor((x - pad) * scale));
        var y0 = Math.Max(0, (int)MathF.Floor((y - pad) * scale));
        var x1 = Math.Min(Width, (int)MathF.Ceiling((x + w + pad) * scale));
        var y1 = Math.Min(Height, (int)MathF.Ceiling((y + h + pad) * scale));
        for (var py = y0; py < y1; py++)
            for (var pxl = x0; pxl < x1; pxl++)
                body(pxl, py, pxl + 0.5f, py + 0.5f);
    }

    /// <summary>Signed distance (pixels) from a point to a rounded box given in pixels.</summary>
    static float BoxDistance(float px, float py, float cx, float cy, float hw, float hh, float r)
    {
        var qx = MathF.Abs(px - cx) - (hw - r);
        var qy = MathF.Abs(py - cy) - (hh - r);
        var outside = MathF.Sqrt(MathF.Max(qx, 0) * MathF.Max(qx, 0) + MathF.Max(qy, 0) * MathF.Max(qy, 0));
        return outside + MathF.Min(MathF.Max(qx, qy), 0) - r;
    }

    static float Coverage(float d) => Math.Clamp(0.5f - d, 0, 1);

    public void RoundRect(float x, float y, float w, float h, float radius, Color top, Color bottom, bool horizontal = false)
    {
        var (cx, cy, hw, hh, r) = ((x + w / 2) * scale, (y + h / 2) * scale, w / 2 * scale, h / 2 * scale, Math.Clamp(radius, 0, Math.Min(w, h) / 2) * scale);
        Each(x, y, w, h, 1, (ix, iy, fx, fy) =>
        {
            var cov = Coverage(BoxDistance(fx, fy, cx, cy, hw, hh, r));
            if (cov <= 0) return;
            var t = horizontal ? Math.Clamp((fx / scale - x) / w, 0, 1) : Math.Clamp((fy / scale - y) / h, 0, 1);
            Blend(ix, iy, Lerp(top, bottom, t), cov);
        });
    }

    public void Stroke(float x, float y, float w, float h, float radius, float width, Color color)
    {
        var (cx, cy, hw, hh, r, sw) = ((x + w / 2) * scale, (y + h / 2) * scale, w / 2 * scale, h / 2 * scale, Math.Clamp(radius, 0, Math.Min(w, h) / 2) * scale, width * scale);
        Each(x, y, w, h, 1, (ix, iy, fx, fy) =>
        {
            var d = BoxDistance(fx, fy, cx, cy, hw, hh, r);
            Blend(ix, iy, color, Coverage(d) * Math.Clamp(0.5f + d + sw, 0, 1));
        });
    }

    /// <summary>A soft shadow under a rounded box, offset down by <paramref name="dy"/>.</summary>
    public void Shadow(float x, float y, float w, float h, float radius, float blur, float dy, Color color)
    {
        var (cx, cy, hw, hh, r, b) = ((x + w / 2) * scale, (y + h / 2 + dy) * scale, w / 2 * scale, h / 2 * scale, Math.Clamp(radius, 0, Math.Min(w, h) / 2) * scale, Math.Max(0.5f, blur) * scale);
        Each(x, y + dy, w, h, blur, (ix, iy, fx, fy) =>
        {
            var t = Math.Clamp((BoxDistance(fx, fy, cx, cy, hw, hh, r) + b * 0.35f) / b, 0, 1);
            Blend(ix, iy, color, 1 - t * t * (3 - 2 * t));
        });
    }

    public void Circle(float cx, float cy, float radius, Color color)
    {
        var (pcx, pcy, pr) = (cx * scale, cy * scale, radius * scale);
        Each(cx - radius, cy - radius, radius * 2, radius * 2, 1, (ix, iy, fx, fy) =>
            Blend(ix, iy, color, Coverage(MathF.Sqrt((fx - pcx) * (fx - pcx) + (fy - pcy) * (fy - pcy)) - pr)));
    }

    /// <summary>
    /// A ring segment with round ends: <paramref name="startDegrees"/> from 12 o'clock, clockwise,
    /// <paramref name="sweepDegrees"/> long (360 = full ring).
    /// </summary>
    public void Arc(float cx, float cy, float radius, float thickness, float startDegrees, float sweepDegrees, Color color)
    {
        if (sweepDegrees <= 0) return;
        var (pcx, pcy, pr, half) = (cx * scale, cy * scale, radius * scale, thickness / 2 * scale);
        var full = sweepDegrees >= 360;
        var start = startDegrees * MathF.PI / 180;
        var sweep = Math.Min(sweepDegrees, 360) * MathF.PI / 180;
        (float X, float Y) End(float a) => (pcx + MathF.Sin(a) * pr, pcy - MathF.Cos(a) * pr);
        var (s, e) = (End(start), End(start + sweep));
        var pad = thickness / 2 + 1;
        Each(cx - radius - pad, cy - radius - pad, (radius + pad) * 2, (radius + pad) * 2, 0, (ix, iy, fx, fy) =>
        {
            var dx = fx - pcx;
            var dy = fy - pcy;
            float d;
            var angle = (MathF.Atan2(dx, -dy) - start) % (2 * MathF.PI);
            if (angle < 0) angle += 2 * MathF.PI;
            if (full || angle <= sweep) d = MathF.Abs(MathF.Sqrt(dx * dx + dy * dy) - pr) - half;
            else d = MathF.Min(MathF.Sqrt((fx - s.X) * (fx - s.X) + (fy - s.Y) * (fy - s.Y)), MathF.Sqrt((fx - e.X) * (fx - e.X) + (fy - e.Y) * (fy - e.Y))) - half;
            Blend(ix, iy, color, Coverage(d));
        });
    }

    static Color Lerp(Color a, Color b, float t) => new(a.R + (b.R - a.R) * t, a.G + (b.G - a.G) * t, a.B + (b.B - a.B) * t, a.A + (b.A - a.A) * t);

    /// <summary>Straight-alpha RGBA32, bottom row first (Unity's texture order).</summary>
    public byte[] ToRgba32()
    {
        var bytes = new byte[Width * Height * 4];
        for (var y = 0; y < Height; y++)
        {
            var src = y * Width * 4;
            var dst = (Height - 1 - y) * Width * 4;
            for (var x = 0; x < Width * 4; x += 4)
            {
                var a = px[src + x + 3];
                if (a <= 0) continue;
                for (var k = 0; k < 3; k++) bytes[dst + x + k] = (byte)Math.Clamp(px[src + x + k] / a * 255 + 0.5f, 0, 255);
                bytes[dst + x + 3] = (byte)Math.Clamp(a * 255 + 0.5f, 0, 255);
            }
        }
        return bytes;
    }

    /// <summary>One pixel as straight-alpha (r, g, b, a) bytes, top-left origin (for tests).</summary>
    internal (byte R, byte G, byte B, byte A) Pixel(int x, int y)
    {
        var i = (y * Width + x) * 4;
        var a = px[i + 3];
        byte C(float v) => (byte)Math.Clamp(a <= 0 ? 0 : v / a * 255 + 0.5f, 0, 255);
        return (C(px[i]), C(px[i + 1]), C(px[i + 2]), (byte)Math.Clamp(a * 255 + 0.5f, 0, 255));
    }
}
