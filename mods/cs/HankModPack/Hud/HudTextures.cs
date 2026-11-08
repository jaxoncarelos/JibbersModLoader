using System.IO.Compression;
using System.Reflection;

namespace HankModPack;

/// <summary>Real material textures (steel, wood, wool, snow, beach) for the themes that have one. Tiles are stored as raw RGB, deflated, in TexData.cs.</summary>
static class HudTextures
{
    const int TileW = 560, TileH = 300;
    static readonly Dictionary<string, byte[]?> cache = new();

    static byte[]? Load(string name)
    {
        lock (cache)
        {
            if (cache.TryGetValue(name, out var c)) return c;
            byte[]? rgb = null;
            try
            {
                var b64 = TexData.Get(name);
                if (b64 != null)
                {
                    using var s = new MemoryStream(Convert.FromBase64String(b64));
                    using var d = new DeflateStream(s, CompressionMode.Decompress);
                    using var ms = new MemoryStream();
                    d.CopyTo(ms);
                    rgb = ms.ToArray();
                    if (rgb.Length != TileW * TileH * 3) rgb = null;
                }
            }
            catch { rgb = null; }
            cache[name] = rgb;
            return rgb;
        }
    }

    /// <summary>Colour of the texture at (x, y) in canvas units, mirrored so the tile never shows a seam.</summary>
    public static bool Sample(byte[] rgb, float x, float y, out float r, out float g, out float b)
    {
        static int Fold(int v, int n) { v %= 2 * n; if (v < 0) v += 2 * n; return v >= n ? 2 * n - 1 - v : v; }
        var i = (Fold((int)MathF.Floor(y), TileH) * TileW + Fold((int)MathF.Floor(x), TileW)) * 3;
        r = rgb[i] / 255f; g = rgb[i + 1] / 255f; b = rgb[i + 2] / 255f;
        return true;
    }

    /// <summary>Size of one texel in canvas units: 1 for windows as big as the tile or bigger, smaller for small windows so they still show the whole tile.</summary>
    public static float ScaleFor(float w, float h) => Math.Clamp(Math.Max(w / TileW, h / TileH), 0.12f, 1f);

    public static byte[]? Get(Look? l) => l?.Tex is { } t && !l.NoBg ? Load(t) : null;

    /// <summary>Four bolts in the corners of the steel plate: 0 outside, up to 1 in the bolt (and a darker ring). Canvas units, origin top-left.</summary>
    public static float Bolt(float x, float y, float w, float h, out float shade)
    {
        shade = 0;
        if (w < 90 || h < 40) return 0;
        const float inset = 12, rad = 4.6f;
        float cx = x < w / 2 ? inset : w - inset, cy = y < h / 2 ? inset : h - inset;
        var d = MathF.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy));
        if (d > rad + 1.2f) return 0;
        // ring, then a lit head with a slot
        shade = d > rad - 1f ? 0.25f : 0.62f + 0.3f * (1 - (x - cx + rad) / (2 * rad)) * (1 - (y - cy + rad) / (2 * rad));
        if (d < rad - 1f && MathF.Abs((y - cy) - 0.45f * (x - cx)) < 0.55f && MathF.Abs(x - cx) < 3f) shade = 0.18f;
        return Math.Clamp(rad + 1.2f - d, 0, 1);
    }

    /// <summary>Quick-menu panel: puts the texture where the picture still holds the plain fill colour (the border and the soft edge stay).</summary>
    public static void PaintRounded(byte[] rgba, int w, int h, float k, ButtonArt.Rgba fill, Look look)
    {
        var tex = Get(look);
        if (tex == null) return;
        byte fr = (byte)(fill.R * 255 + 0.5f), fg = (byte)(fill.G * 255 + 0.5f), fb = (byte)(fill.B * 255 + 0.5f);
        var steel = look.Tex == "steel";
        var ts = ScaleFor(w / k, h / k);
        for (var py = 0; py < h; py++)
        for (var px = 0; px < w; px++)
        {
            var i = (py * w + px) * 4;
            if (rgba[i + 3] == 0 || Math.Abs(rgba[i] - fr) > 3 || Math.Abs(rgba[i + 1] - fg) > 3 || Math.Abs(rgba[i + 2] - fb) > 3) continue;
            var u = px / k; var v = (h - 1 - py) / k;   // v from the top of the panel, in canvas units
            Sample(tex, u / ts, v / ts, out var r, out var g, out var b);
            if (steel && Bolt(u, v, w / k, h / k, out var sh) is var a && a > 0) { r += (sh - r) * a; g += (sh - g) * a; b += (sh - b) * a; }
            rgba[i] = (byte)(r * 255 + 0.5f); rgba[i + 1] = (byte)(g * 255 + 0.5f); rgba[i + 2] = (byte)(b * 255 + 0.5f);
        }
    }
}
