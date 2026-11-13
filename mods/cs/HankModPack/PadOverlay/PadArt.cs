using Jibbers.Sdk;
using Rgba = HankModPack.ButtonArt.Rgba;

namespace HankModPack;

/// <summary>
/// The pictures of the live input overlay (controller, keyboard, mouse), drawn in code. Pure C# (no Unity), 2 pixels per logical unit.
/// A <see cref="Part"/> is one piece that moves or lights up while playing: its picture is white with the shape's coverage as alpha, so the overlay
/// tints it (lit colour or invisible) without making new pictures. The base picture holds everything that never changes.
/// </summary>
internal static class PadArt
{
    public const int S = 2;                       // pixels per logical unit
    public const float Margin = 10;               // free space round the drawing (logical units)

    public enum Kind { Ps4, Ps5, X360, XOne, Keyboard }

    /// <summary>The colours of one look: a plain white / black pad, or built from a HUD theme.</summary>
    public sealed record Pal(Rgba Body, Rgba Body2, Rgba Dark, Rgba Edge, Rgba Key, Rgba Well, Rgba Stick, Rgba Lit, Rgba Text, Rgba LitText,
        Rgba? Bg, Rgba BgEdge, float BgBorder, float BgRadius, Rgba Bump, Rgba Trig, Rgba Tp, Rgba Fb, Rgba StickHi);

    public enum PartKind { Lit, Cap, Fill, Dot }

    /// <summary>One piece: where it is (logical units, centre), how big, its white picture, and what it does.</summary>
    public sealed record Part(string Name, PartKind Kind, float Cx, float Cy, float W, float H, byte[] Rgba, int PxW, int PxH);

    public sealed record Label(string Text, float Cx, float Cy, float Size, string Part);

    public sealed class Art
    {
        public int PxW, PxH;
        public float W, H;                        // logical size, margin included
        public byte[] Base = Array.Empty<byte>();
        public readonly List<Part> Parts = new();
        public readonly List<Label> Labels = new();
        public float OffsetX, OffsetY;            // where the drawing's (0,0) is inside the base picture
    }

    // ---------------------------------------------------------------- Palettes

    static Rgba H(int rgb, float a = 1f) => Rgba.Hex(rgb, a);

    public static Pal White() => new(H(0xF7F8FA), H(0xD6D9E0), H(0x15171B), H(0x6A7080), H(0xF1F3F7), H(0xC5C9D2), H(0x2B2E35), H(0x1F7BFF), H(0x14161C), H(0xFFFFFF), null, H(0), 0, 0,
        H(0xE3E6EC), H(0xE9ECF1), H(0xECEFF4), H(0xF2F4F8), H(0x686D79));
    public static Pal Black() => new(H(0x2B2E35), H(0x15171B), H(0x0B0C0F), H(0x050608, .9f), H(0x1C1F25), H(0x0A0B0E), H(0x23262C), H(0x3FB0FF), H(0xE6E9F0), H(0x001018), null, H(0), 0, 0,
        H(0x23262D), H(0x1D2026), H(0x22252C), H(0x1B1E24), H(0x4D525E));

    /// <summary>The colours of a HUD theme, for a pad like the white and black ones (no background): the theme's window colours for the body, its border for the outline, its accent for what is pressed.</summary>
    public static Pal FromLook(Look l)
    {
        Rgba C(Color c, float a = 1f) => new(c.R, c.G, c.B, a);
        var fill = C(l.Fill); var fill2 = C(l.FillBottom);
        var text = C(HudThemes.Hex(l.Value));
        var acc = C(HudThemes.Hex(l.Accent));
        if (l.NoBg) { fill = H(0x2B2E35); fill2 = H(0x15171B); }
        var lum = 0.3f * fill.R + 0.59f * fill.G + 0.11f * fill.B;
        var dark = lum < 0.5f;
        var white = new Rgba(1, 1, 1); var black = new Rgba(0, 0, 0);
        var key = dark ? Mix(fill, white, .14f) : Mix(fill, black, .08f);
        var well = Mix(fill2, black, dark ? .55f : .35f);
        var stick = dark ? Mix(fill, white, .22f) : Mix(fill, black, .55f);
        var stickHi = Mix(stick, white, .35f);
        var edge = l.BorderWidth > 0 && l.Border.A > 0 ? C(l.Border) : Mix(fill2, black, .55f);
        var accLum = 0.3f * acc.R + 0.59f * acc.G + 0.11f * acc.B;
        return new Pal(fill, fill2, Mix(fill2, black, .5f), edge, key, well, stick, acc, text, accLum > 0.55f ? H(0x101010) : H(0xFFFFFF),
            null, edge, 0, 0, Mix(fill2, black, dark ? .15f : .06f), Mix(fill2, black, dark ? .1f : .04f), key, key, stickHi);
    }

    // ---------------------------------------------------------------- Canvas

    sealed class Px
    {
        public readonly int W, H; public readonly float[] P; readonly float ox, oy;
        public Px(int w, int h, float ox = 0, float oy = 0) { W = w; H = h; P = new float[w * h * 4]; this.ox = ox; this.oy = oy; }

        /// <summary>Paints where the signed distance (logical units, negative inside) says; (x, y) are logical units, origin = the drawing's origin.</summary>
        public void Paint(Func<float, float, float> sd, Rgba c, float x0 = float.NaN, float y0 = float.NaN, float x1 = float.NaN, float y1 = float.NaN)
        {
            int ix0 = 0, iy0 = 0, ix1 = W, iy1 = H;
            if (!float.IsNaN(x0)) { ix0 = Math.Max(0, (int)((x0 + ox) * S) - 2); iy0 = Math.Max(0, (int)((y0 + oy) * S) - 2); ix1 = Math.Min(W, (int)((x1 + ox) * S) + 3); iy1 = Math.Min(H, (int)((y1 + oy) * S) + 3); }
            for (var y = iy0; y < iy1; y++)
                for (var x = ix0; x < ix1; x++)
                {
                    var d = sd((x + 0.5f) / S - ox, (y + 0.5f) / S - oy) * S;
                    var cov = Math.Clamp(0.5f - d, 0f, 1f) * c.A;
                    if (cov <= 0) continue;
                    var i = (y * W + x) * 4;
                    var a0 = P[i + 3];
                    var a = cov + a0 * (1 - cov);
                    P[i] = (c.R * cov + P[i] * a0 * (1 - cov)) / a;
                    P[i + 1] = (c.G * cov + P[i + 1] * a0 * (1 - cov)) / a;
                    P[i + 2] = (c.B * cov + P[i + 2] * a0 * (1 - cov)) / a;
                    P[i + 3] = a;
                }
        }

        /// <summary>Like Paint, with a colour that depends on the height (vertical gradient) and an optional soft edge (shadow).</summary>
        public void PaintFn(Func<float, float, float> sd, Func<float, Rgba> colour, float soft = 0)
        {
            for (var y = 0; y < H; y++)
                for (var x = 0; x < W; x++)
                {
                    var ly = (y + 0.5f) / S - oy;
                    var d = sd((x + 0.5f) / S - ox, ly) * S;
                    var cov = soft > 0 ? Math.Clamp(0.5f - d / (soft * S * 2), 0f, 1f) : Math.Clamp(0.5f - d, 0f, 1f);
                    var c = colour(ly);
                    cov *= c.A;
                    if (cov <= 0) continue;
                    var i = (y * W + x) * 4;
                    var a0 = P[i + 3];
                    var a = cov + a0 * (1 - cov);
                    P[i] = (c.R * cov + P[i] * a0 * (1 - cov)) / a;
                    P[i + 1] = (c.G * cov + P[i + 1] * a0 * (1 - cov)) / a;
                    P[i + 2] = (c.B * cov + P[i + 2] * a0 * (1 - cov)) / a;
                    P[i + 3] = a;
                }
        }

        public byte[] Bytes()
        {
            var o = new byte[W * H * 4];
            for (var y = 0; y < H; y++)
                for (var x = 0; x < W; x++)
                {
                    var s = (y * W + x) * 4; var d = ((H - 1 - y) * W + x) * 4;   // Unity textures start at the bottom row
                    for (var k = 0; k < 4; k++) o[d + k] = (byte)Math.Round(Math.Clamp(P[s + k], 0, 1) * 255);
                }
            return o;
        }
    }

    // ---------------------------------------------------------------- Shapes (distance functions, logical units)

    static float Circle(float x, float y, float cx, float cy, float r) => MathF.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy)) - r;
    static float Box(float x, float y, float cx, float cy, float hw, float hh, float r)
    {
        var qx = MathF.Abs(x - cx) - (hw - r); var qy = MathF.Abs(y - cy) - (hh - r);
        return MathF.Sqrt(MathF.Max(qx, 0) * MathF.Max(qx, 0) + MathF.Max(qy, 0) * MathF.Max(qy, 0)) + MathF.Min(MathF.Max(qx, qy), 0) - r;
    }
    static float Seg(float x, float y, float ax, float ay, float bx, float by, float w)
    {
        float dx = bx - ax, dy = by - ay;
        var t = Math.Clamp(((x - ax) * dx + (y - ay) * dy) / (dx * dx + dy * dy), 0f, 1f);
        var px = ax + t * dx; var py = ay + t * dy;
        return MathF.Sqrt((x - px) * (x - px) + (y - py) * (y - py)) - w / 2;
    }
    static float Ring(float sd, float w) => MathF.Abs(sd + w / 2) - w / 2;     // outline of any shape, drawn inside
    static float SMin(float a, float b, float k)
    {
        var h = Math.Clamp(0.5f + 0.5f * (b - a) / k, 0f, 1f);
        return b + (a - b) * h - k * h * (1 - h);
    }

    // ---------------------------------------------------------------- A controller

    // ---------------------------------------------------------------- Paths (the same drawing as the mock-up, flattened to polygons)

    static List<(float X, float Y)> Flatten(string d)
    {
        var t = d.Replace(",", " ").Replace("M", " M ").Replace("Q", " Q ").Replace("C", " C ").Replace("L", " L ").Replace("V", " V ").Replace("H", " H ").Replace("Z", " Z ")
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var pts = new List<(float X, float Y)>();
        float cx = 0, cy = 0; var i = 0;
        float N() => float.Parse(t[i++], System.Globalization.CultureInfo.InvariantCulture);
        while (i < t.Length)
        {
            var cmd = t[i++];
            switch (cmd)
            {
                case "M": cx = N(); cy = N(); pts.Add((cx, cy)); break;
                case "L": cx = N(); cy = N(); pts.Add((cx, cy)); break;
                case "V": cy = N(); pts.Add((cx, cy)); break;
                case "H": cx = N(); pts.Add((cx, cy)); break;
                case "Q":
                    {
                        float x1 = N(), y1 = N(), x2 = N(), y2 = N();
                        for (var k = 1; k <= 24; k++) { var u = k / 24f; var v = 1 - u; pts.Add((v * v * cx + 2 * v * u * x1 + u * u * x2, v * v * cy + 2 * v * u * y1 + u * u * y2)); }
                        cx = x2; cy = y2; break;
                    }
                case "C":
                    {
                        float x1 = N(), y1 = N(), x2 = N(), y2 = N(), x3 = N(), y3 = N();
                        for (var k = 1; k <= 24; k++)
                        {
                            var u = k / 24f; var v = 1 - u;
                            pts.Add((v * v * v * cx + 3 * v * v * u * x1 + 3 * v * u * u * x2 + u * u * u * x3, v * v * v * cy + 3 * v * v * u * y1 + 3 * v * u * u * y2 + u * u * u * y3));
                        }
                        cx = x3; cy = y3; break;
                    }
                default: break;   // Z
            }
        }
        return pts;
    }

    /// <summary>Signed distance to a closed polygon (negative inside).</summary>
    static Func<float, float, float> Poly(List<(float X, float Y)> p)
    {
        var n = p.Count;
        return (x, y) =>
        {
            var best = float.MaxValue; var inside = false;
            for (int i = 0, j = n - 1; i < n; j = i++)
            {
                float ax = p[j].X, ay = p[j].Y, bx = p[i].X, by = p[i].Y;
                float dx = bx - ax, dy = by - ay;
                var len2 = dx * dx + dy * dy;
                var t = len2 < 1e-9f ? 0 : Math.Clamp(((x - ax) * dx + (y - ay) * dy) / len2, 0f, 1f);
                float qx = ax + t * dx - x, qy = ay + t * dy - y;
                var d2 = qx * qx + qy * qy;
                if (d2 < best) best = d2;
                if ((ay > y) != (by > y) && x < (bx - ax) * (y - ay) / (by - ay) + ax) inside = !inside;
            }
            var d = MathF.Sqrt(best);
            return inside ? -d : d;
        };
    }

    /// <summary>A polygon's distance read from a grid computed once (the body is looked at many times).</summary>
    static Func<float, float, float> Cached(Func<float, float, float> f, float x0, float y0, float x1, float y1)
    {
        var w = (int)((x1 - x0) * 4) + 1; var h = (int)((y1 - y0) * 4) + 1;
        var g = new float[w * h];
        for (var j = 0; j < h; j++) for (var i = 0; i < w; i++) g[j * w + i] = f(x0 + i / 4f, y0 + j / 4f);
        return (x, y) =>
        {
            var fx = (x - x0) * 4; var fy = (y - y0) * 4;
            if (fx < 0 || fy < 0 || fx >= w - 1 || fy >= h - 1) return f(x, y);
            int ix = (int)fx, iy = (int)fy; float tx = fx - ix, ty = fy - iy;
            var a = g[iy * w + ix] * (1 - tx) + g[iy * w + ix + 1] * tx;
            var b2 = g[(iy + 1) * w + ix] * (1 - tx) + g[(iy + 1) * w + ix + 1] * tx;
            return a * (1 - ty) + b2 * ty;
        };
    }

    // DualShock 4: flat top, long handles spreading outwards, a deep notch in between. Xbox One: round and chunky, short handles, a curved underside.
    const string Ps4Body = "M104,66 Q200,54 296,66 C330,70 352,96 366,150 C380,200 392,248 380,270 C368,288 340,284 326,262 C314,244 306,226 288,218 L112,218 C94,226 86,244 74,262 C60,284 32,288 20,270 C8,248 20,200 34,150 C48,96 70,70 104,66 Z";
    const string XoneBody = "M88,66 Q200,52 312,66 C352,72 384,118 390,176 C396,236 380,276 346,276 C318,276 304,246 286,232 Q200,246 114,232 C96,246 82,276 54,276 C20,276 4,236 10,176 C16,118 48,72 88,66 Z";

    static Rgba Mix(Rgba a, Rgba b, float t) => new(a.R + (b.R - a.R) * t, a.G + (b.G - a.G) * t, a.B + (b.B - a.B) * t, a.A + (b.A - a.A) * t);

    /// <summary>The PlayStation 4 (DualShock 4) or Xbox One controller, drawn like the mock-up.</summary>
    public static Art Controller(Kind kind, Pal p, bool bg)
    {
        var a = new Art();
        var ps = kind is Kind.Ps4 or Kind.Ps5;
        var m = bg && p.Bg is not null ? 22f : Margin;
        float w = 400 + 2 * m, h = 290 + 2 * m;
        a.W = w; a.H = h; a.OffsetX = m; a.OffsetY = m;
        a.PxW = (int)(w * S); a.PxH = (int)(h * S);
        var c = new Px(a.PxW, a.PxH, m, m);

        if (bg && p.Bg is { } back)
        {
            float R(float x, float y) => Box(x, y, 200, 145, 200 + m, 145 + m, p.BgRadius);
            c.Paint(R, back);
            if (p.BgBorder > 0) c.Paint((x, y) => Ring(R(x, y), p.BgBorder), p.BgEdge);
        }

        // Triggers (behind everything) and bumpers.
        foreach (var x0 in new[] { 60f, 278f })
        {
            var tr = Poly(Flatten($"M{x0},30 Q{x0 + 31},14 {x0 + 62},30 L{x0 + 58},50 L{x0 + 4},50 Z"));
            c.Paint(tr, p.Trig); c.Paint((x, y) => MathF.Abs(tr(x, y)) - 0.75f, p.Edge);
        }
        float bw = ps ? 76 : 84;
        foreach (var bx in new[] { ps ? 58f : 50f, 266f })
        {
            float Bump(float x, float y) => Box(x, y, bx + bw / 2, 56.5f, bw / 2, 8.5f, 8);
            c.Paint(Bump, p.Bump); c.Paint((x, y) => MathF.Abs(Bump(x, y)) - 0.75f, p.Edge);
        }

        // The body: shadow, vertical gradient, outline, highlight along the top.
        var body = Cached(Poly(Flatten(ps ? Ps4Body : XoneBody)), -10, 40, 410, 290);
        c.PaintFn((x, y) => body(x, y - 4), _ => new Rgba(0, 0, 0, .42f), 5);
        c.PaintFn(body, y => Mix(p.Body, p.Body2, Math.Clamp((y - 56) / 222f, 0, 1)));
        c.Paint((x, y) => MathF.Abs(body(x, y)) - 1.25f, p.Edge);
        var hl = Poly(Flatten("M70,90 Q200,66 330,90"));
        c.Paint((x, y) => MathF.Max(hl(x, y) - 1.5f, body(x, y) + 2f), new Rgba(1, 1, 1, .18f), 60, 60, 340, 100);

        void Well(float cx, float cy, float r) { c.Paint((x, y) => Circle(x, y, cx, cy, r), p.Well); c.Paint((x, y) => MathF.Abs(Circle(x, y, cx, cy, r)) - 1f, p.Edge); }
        void Stick(string n, float cx, float cy) { Well(cx, cy, 27); a.Parts.Add(MakeCap(n, cx, cy, p)); }
        void Small(string n, float x0, float y0, float sw, float sh, float r)
        {
            float Sh(float x, float y) => Box(x, y, x0 + sw / 2, y0 + sh / 2, sw / 2, sh / 2, r);
            c.Paint(Sh, p.Fb); c.Paint((x, y) => MathF.Abs(Sh(x, y)) - 0.65f, p.Edge);
            a.Parts.Add(Make(n, PartKind.Lit, x0 + sw / 2, y0 + sh / 2, sw / 2, sh / 2, Sh));
        }
        void Face(float cx, float cy)
        {
            foreach (var (n, px, py) in new[] { ("Y", cx, cy - 26), ("B", cx + 26, cy), ("A", cx, cy + 26), ("X", cx - 26, cy) })
            {
                c.Paint((x, y) => Circle(x, y, px, py, 14), p.Fb);
                c.Paint((x, y) => MathF.Abs(Circle(x, y, px, py, 14)) - 0.9f, p.Edge);
                var gs = GlyphSd(n, ps, px, py);
                c.Paint(gs, GlyphColour(n, ps));
                a.Parts.Add(Make(n, PartKind.Lit, px, py, 14, 14, (x, y) => Circle(x, y, px, py, 14)));
                a.Parts.Add(Make(n + "g", PartKind.Lit, px, py, 9, 9, gs));    // the symbol again, drawn over the lit button in a readable colour
            }
        }
        void ArmPaths(float x, float y)
        {
            // PlayStation: four separate arms with a rounded end. Xbox: a disc with a plus.
            if (ps)
            {
                var arms = new[]
                {
                    ("Up", $"M{x - 10},{y - 8} V{y - 30} Q{x},{y - 36} {x + 10},{y - 30} V{y - 8} Z"),
                    ("Down", $"M{x - 10},{y + 8} V{y + 30} Q{x},{y + 36} {x + 10},{y + 30} V{y + 8} Z"),
                    ("Left", $"M{x - 8},{y - 10} H{x - 30} Q{x - 36},{y} {x - 30},{y + 10} H{x - 8} Z"),
                    ("Right", $"M{x + 8},{y - 10} H{x + 30} Q{x + 36},{y} {x + 30},{y + 10} H{x + 8} Z"),
                };
                foreach (var (n, d) in arms)
                {
                    var sd = Poly(Flatten(d));
                    c.Paint(sd, p.Fb); c.Paint((xx, yy) => MathF.Abs(sd(xx, yy)) - 0.75f, p.Edge);
                    a.Parts.Add(MakeBox(n, PartKind.Lit, sd, x - 40, y - 40, x + 40, y + 40));
                }
            }
            else
            {
                Well(x, y, 28);
                var plus = Poly(Flatten($"M{x - 9},{y - 9} V{y - 26} H{x + 9} V{y - 9} H{x + 26} V{y + 9} H{x + 9} V{y + 26} H{x - 9} V{y + 9} H{x - 26} V{y - 9} Z"));
                c.Paint(plus, p.Fb); c.Paint((xx, yy) => MathF.Abs(plus(xx, yy)) - 0.75f, p.Edge);
                var quarters = new[]
                {
                    ("Up", $"M{x - 9},{y - 9} V{y - 26} H{x + 9} V{y - 9} Z"), ("Right", $"M{x + 9},{y - 9} H{x + 26} V{y + 9} H{x + 9} Z"),
                    ("Down", $"M{x - 9},{y + 9} V{y + 26} H{x + 9} V{y + 9} Z"), ("Left", $"M{x - 9},{y - 9} H{x - 26} V{y + 9} H{x - 9} Z"),
                };
                foreach (var (n, d) in quarters) a.Parts.Add(MakeBox(n, PartKind.Lit, Poly(Flatten(d)), x - 30, y - 30, x + 30, y + 30));
            }
        }

        if (ps)
        {
            ArmPaths(112, 106); Face(288, 106);
            float Tp(float x, float y) => Box(x, y, 200, 107, 48, 25, 10);
            c.Paint(Tp, p.Tp); c.Paint((x, y) => MathF.Abs(Tp(x, y)) - 0.75f, p.Edge);
            Small("Back", 130, 74, 10, 18, 4); Small("Start", 260, 74, 10, 18, 4);
            c.Paint((x, y) => Circle(x, y, 200, 164, 9), p.Fb); c.Paint((x, y) => MathF.Abs(Circle(x, y, 200, 164, 9)) - 0.75f, p.Edge);
            a.Parts.Add(Make("Home", PartKind.Lit, 200, 164, 9, 9, (x, y) => Circle(x, y, 200, 164, 9)));
            c.Paint((x, y) => Box(x, y, 200, 72.5f, 60, 2.5f, 2.5f), new Rgba(p.Lit.R, p.Lit.G, p.Lit.B, .85f));   // the light bar
            Stick("L", 150, 172); Stick("R", 250, 172);
        }
        else
        {
            Stick("L", 110, 104); Face(290, 104); ArmPaths(156, 172); Stick("R", 244, 172);
            c.Paint((x, y) => Circle(x, y, 200, 100, 11), p.Fb); c.Paint((x, y) => MathF.Abs(Circle(x, y, 200, 100, 11)) - 1f, p.Edge);
            c.Paint((x, y) => Ring(Circle(x, y, 200, 100, 6), 2f), new Rgba(p.LitText.R, p.LitText.G, p.LitText.B, .5f));
            a.Parts.Add(Make("Home", PartKind.Lit, 200, 100, 11, 11, (x, y) => Circle(x, y, 200, 100, 11)));
            Small("Back", 166, 110, 18, 11, 5.5f); Small("Start", 216, 110, 18, 11, 5.5f);
        }

        // Pieces that light up or move over everything: bumpers and the trigger fill.
        foreach (var (n, bx) in new[] { ("LB", ps ? 58f : 50f), ("RB", 266f) })
            a.Parts.Add(Make(n, PartKind.Lit, bx + bw / 2, 56.5f, bw / 2, 8.5f, (x, y) => Box(x, y, bx + bw / 2, 56.5f, bw / 2, 8.5f, 8)));
        foreach (var (n, x0) in new[] { ("LT", 60f), ("RT", 278f) })
        {
            var tr = Poly(Flatten($"M{x0},30 Q{x0 + 31},14 {x0 + 62},30 L{x0 + 58},50 L{x0 + 4},50 Z"));
            a.Parts.Add(MakeBox(n, PartKind.Fill, tr, x0, 14, x0 + 62, 50));
        }
        a.Base = c.Bytes();
        return a;
    }

    /// <summary>A white picture of a polygon-like shape inside a box (logical units).</summary>
    static Part MakeBox(string name, PartKind kind, Func<float, float, float> sd, float x0, float y0, float x1, float y1)
    {
        var cx = (x0 + x1) / 2; var cy = (y0 + y1) / 2;
        return Make(name, kind, cx, cy, (x1 - x0) / 2, (y1 - y0) / 2, sd);
    }

    /// <summary>The letter or symbol on a face button, as a stroke (distance function): Xbox Y B A X, PlayStation triangle circle cross square.</summary>
    static Func<float, float, float> GlyphSd(string n, bool ps, float cx, float cy)
    {
        float lw = 2.8f;
        float L(float x, float y, float ax, float ay, float bx, float by) => Seg(x, y, cx + ax, cy + ay, cx + bx, cy + by, lw);
        if (ps)
        {
            const float u = 6f;
            return n switch
            {
                "A" => (x, y) => MathF.Min(L(x, y, -u, -u, u, u), L(x, y, -u, u, u, -u)),
                "B" => (x, y) => MathF.Abs(Circle(x, y, cx, cy, u * 1.1f)) - lw / 2,
                "X" => (x, y) => MathF.Abs(Box(x, y, cx, cy, u, u, 1)) - lw / 2,
                _ => (x, y) => MathF.Min(L(x, y, 0, -u * 1.15f, -u * 1.1f, u * 0.8f), MathF.Min(L(x, y, -u * 1.1f, u * 0.8f, u * 1.1f, u * 0.8f), L(x, y, u * 1.1f, u * 0.8f, 0, -u * 1.15f))),
            };
        }
        return n switch
        {
            "X" => (x, y) => MathF.Min(L(x, y, -5.5f, -6.5f, 5.5f, 6.5f), L(x, y, 5.5f, -6.5f, -5.5f, 6.5f)),
            "Y" => (x, y) => MathF.Min(L(x, y, -5.5f, -6.5f, 0, 0.5f), MathF.Min(L(x, y, 5.5f, -6.5f, 0, 0.5f), L(x, y, 0, 0.5f, 0, 6.5f))),
            "A" => (x, y) => MathF.Min(L(x, y, -6, 6.5f, 0, -6.5f), MathF.Min(L(x, y, 6, 6.5f, 0, -6.5f), L(x, y, -3.4f, 1.5f, 3.4f, 1.5f))),
            _ => (x, y) => MathF.Min(L(x, y, -4, -6.5f, -4, 6.5f), MathF.Min(MathF.Min(L(x, y, -4, -6.5f, 2, -6.5f), L(x, y, 2, -6.5f, 5, -4.2f)), MathF.Min(MathF.Min(L(x, y, 5, -4.2f, 2, -0.5f), L(x, y, 2, -0.5f, -4, -0.5f)),
                MathF.Min(MathF.Min(L(x, y, 2, -0.5f, 6, 2.5f), L(x, y, 6, 2.5f, 3, 6.5f)), L(x, y, 3, 6.5f, -4, 6.5f))))),
        };
    }

    static Rgba GlyphColour(string n, bool ps) => ps
        ? n switch { "A" => H(0x6AA8FF), "B" => H(0xFF5A6E), "X" => H(0xFF8FD0), _ => H(0x38D6B0) }
        : n switch { "A" => H(0x3DBB4A), "B" => H(0xE5333F), "X" => H(0x2F7FE0), _ => H(0xF4C20D) };

    /// <summary>A white picture of a shape (coverage as alpha) with a little room round it.</summary>
    static Part Make(string name, PartKind kind, float cx, float cy, float hw, float hh, Func<float, float, float> sd)
    {
        int w = (int)Math.Ceiling((hw * 2 + 4) * S), h = (int)Math.Ceiling((hh * 2 + 4) * S);
        var c = new Px(w, h, -(cx - hw - 2), -(cy - hh - 2));
        c.Paint(sd, new Rgba(1, 1, 1, 1));
        return new Part(name, kind, cx, cy, w / (float)S, h / (float)S, c.Bytes(), w, h);
    }

    /// <summary>A stick cap, drawn in the palette's colours: a lighter spot top left, a ring in the middle. The overlay tints it when the stick is clicked.</summary>
    static Part MakeCap(string stick, float cx, float cy, Pal p)
    {
        const float r = 20;
        int n = (int)Math.Ceiling((r * 2 + 4) * S);
        var c = new Px(n, n, -(cx - r - 2), -(cy - r - 2));
        Rgba Over(Rgba t) => new(t.R * t.A + p.Body.R * (1 - t.A), t.G * t.A + p.Body.G * (1 - t.A), t.B * t.A + p.Body.B * (1 - t.A), 1f);
        var hi = Over(p.StickHi);
        var lo = Over(p.Stick);
        c.PaintFn((x, y) => Circle(x, y, cx, cy, r), _ => lo);
        for (var i = 1; i <= 8; i++)   // a radial glow: smaller and smaller discs towards the light spot
        {
            var t = i / 8f; var rr = r * (1 - t * 0.85f);
            c.Paint((x, y) => Circle(x, y, cx - 6 * t, cy - 6 * t, rr), Mix(lo, hi, t) with { A = 0.35f });
        }
        c.Paint((x, y) => MathF.Abs(Circle(x, y, cx, cy, r)) - 1f, new Rgba(p.Edge.R, p.Edge.G, p.Edge.B, p.Edge.A));
        c.Paint((x, y) => MathF.Abs(Circle(x, y, cx, cy, 11)) - 0.75f, new Rgba(p.Edge.R, p.Edge.G, p.Edge.B, .6f));
        return new Part(stick + "Cap", PartKind.Cap, cx, cy, n / (float)S, n / (float)S, c.Bytes(), n, n);
    }

    // ---------------------------------------------------------------- Keyboard and mouse

    public sealed record KeyDef(string Label, int Vk, float X, float Y, float W, float Size = 16);

    public static readonly KeyDef[] Keys =
    {
        new("Q", 0x51, 40, 10, 48), new("W", 0x57, 94, 10, 48), new("E", 0x45, 148, 10, 48), new("R", 0x52, 202, 10, 48),
        new("A", 0x41, 30, 56, 48), new("S", 0x53, 84, 56, 48), new("D", 0x44, 138, 56, 48), new("F", 0x46, 192, 56, 48),
        new("Shift", 0x10, 10, 102, 78, 12), new("Z", 0x5A, 94, 102, 48), new("X", 0x58, 148, 102, 48),
        new("Ctrl", 0x11, 10, 148, 64, 12), new("Space", 0x20, 80, 148, 190),
        new("^", 0x26, 320, 102, 40), new("<", 0x25, 278, 148, 40), new("v", 0x28, 324, 148, 40), new(">", 0x27, 370, 148, 40),
    };

    public static Art KeyboardMouse(Pal p, bool bg)
    {
        var a = new Art();
        var m = bg && p.Bg is not null ? 22f : Margin;
        float w = 520 + 2 * m, h = 200 + 2 * m;
        a.W = w; a.H = h; a.OffsetX = m; a.OffsetY = m;
        a.PxW = (int)(w * S); a.PxH = (int)(h * S);
        var c = new Px(a.PxW, a.PxH, m, m);
        if (bg && p.Bg is { } back)
        {
            float R(float x, float y) => Box(x, y, 260, 100, 260 + m, 100 + m, p.BgRadius);
            c.Paint(R, back);
            if (p.BgBorder > 0) c.Paint((x, y) => Ring(R(x, y), p.BgBorder), p.BgEdge);
        }
        var keyFill = bg && p.Bg is not null ? p.Key : p.Key;
        foreach (var k in Keys)
        {
            var cx = k.X + k.W / 2; var cy = k.Y + 20;
            float Sh(float x, float y) => Box(x, y, cx, cy, k.W / 2, 20, 7);
            c.Paint((x, y) => Sh(x, y + 2), new Rgba(0, 0, 0, .25f));
            c.Paint(Sh, p.Body);
            c.Paint(Sh, keyFill);
            c.Paint((x, y) => Ring(Sh(x, y), 1.6f), p.Edge);
            a.Parts.Add(Make("K" + k.Vk, PartKind.Lit, cx, cy, k.W / 2, 20, Sh));
            a.Labels.Add(new Label(k.Label, cx, cy, k.Size, "K" + k.Vk));
        }
        // The mouse.
        const float mx = 460, my = 90;
        float Mouse(float x, float y) => Box(x, y, mx, my, 30, 70, 28);
        c.Paint((x, y) => Mouse(x, y + 2), new Rgba(0, 0, 0, .25f));
        c.Paint(Mouse, p.Body); c.Paint(Mouse, p.Key);
        c.Paint((x, y) => Ring(Mouse(x, y), 1.8f), p.Edge);
        c.Paint((x, y) => Seg(x, y, mx - 30, my - 40, mx + 30, my - 40, 1.4f), p.Edge);
        c.Paint((x, y) => Seg(x, y, mx, my - 70, mx, my - 40, 1.4f), p.Edge);
        a.Parts.Add(Make("ML", PartKind.Lit, mx - 15, my - 55, 15, 15, (x, y) => MathF.Max(Mouse(x, y), MathF.Max(x - mx + 1, y - (my - 40)))));
        a.Parts.Add(Make("MR", PartKind.Lit, mx + 15, my - 55, 15, 15, (x, y) => MathF.Max(Mouse(x, y), MathF.Max(mx + 1 - x, y - (my - 40)))));
        a.Parts.Add(Make("MM", PartKind.Lit, mx, my - 54, 6, 13, (x, y) => Box(x, y, mx, my - 54, 5, 12, 5)));
        a.Parts.Add(Make("MDot", PartKind.Dot, mx, my + 20, 6, 6, (x, y) => Circle(x, y, mx, my + 20, 5)));
        a.Base = c.Bytes();
        return a;
    }
}
