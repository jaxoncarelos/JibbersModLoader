using System;
namespace HankModPack;

/// <summary>The gamepad buttons the quick menu can show or use.</summary>
public enum PadBtn { A, B, X, Y, LB, RB, LT, RT, L3, R3, Back, Start, DpadDown, RightStick }

public enum PadStyle { Xbox, PlayStation }

/// <summary>
/// Button pictures, drawn in code (no image files): Xbox buttons are coloured discs with a letter, PlayStation buttons are dark discs with the
/// cross / circle / square / triangle symbol in its usual colour. The pictures are 2:1 (width = 2 x height) so the same row layout fits
/// round buttons, shoulder pills and wide buttons. The text on top (letters, "LB", "R3"...) is drawn by the game's font, see <see cref="Label"/>.
/// Pure C#: no Unity needed, so it can be tested on its own.
/// </summary>
public static class ButtonArt
{
    public readonly record struct Rgba(float R, float G, float B, float A = 1f)
    {
        public static Rgba Hex(int rgb, float a = 1f) => new(((rgb >> 16) & 255) / 255f, ((rgb >> 8) & 255) / 255f, (rgb & 255) / 255f, a);
    }

    /// <summary>Picture height in pixels (the width is twice that). Shown at about 40 canvas pixels high.</summary>
    public const int Height = 80;

    // ---------------------------------------------------------------- Names and labels

    /// <summary>What a player calls this button on this pad (used in texts and the log).</summary>
    public static string Name(PadBtn b, PadStyle s) => s == PadStyle.Xbox
        ? b switch
        {
            PadBtn.A => "A", PadBtn.B => "B", PadBtn.X => "X", PadBtn.Y => "Y", PadBtn.LB => "LB", PadBtn.RB => "RB", PadBtn.LT => "LT", PadBtn.RT => "RT",
            PadBtn.L3 => "LSB", PadBtn.R3 => "RSB", PadBtn.Back => "View", PadBtn.Start => "Menu", PadBtn.DpadDown => "D-pad down", _ => "Right stick",
        }
        : b switch
        {
            PadBtn.A => "Cross", PadBtn.B => "Circle", PadBtn.X => "Square", PadBtn.Y => "Triangle", PadBtn.LB => "L1", PadBtn.RB => "R1", PadBtn.LT => "L2", PadBtn.RT => "R2",
            PadBtn.L3 => "L3", PadBtn.R3 => "R3", PadBtn.Back => "Share", PadBtn.Start => "Options", PadBtn.DpadDown => "D-pad down", _ => "Right stick",
        };

    /// <summary>The text to draw on top of the picture ("" when the picture says it all), and its colour.</summary>
    public static (string Text, Rgba Color) Label(PadBtn b, PadStyle s)
    {
        var white = Rgba.Hex(0xFFFFFF);
        var dark = Rgba.Hex(0x14161C);
        if (s == PadStyle.Xbox)
            return b switch
            {
                PadBtn.A => ("A", white), PadBtn.B => ("B", white), PadBtn.X => ("X", white), PadBtn.Y => ("Y", dark),
                PadBtn.LB => ("LB", white), PadBtn.RB => ("RB", white), PadBtn.LT => ("LT", white), PadBtn.RT => ("RT", white),
                PadBtn.L3 => ("LSB", white), PadBtn.R3 => ("RSB", white), PadBtn.Back => ("VIEW", white), PadBtn.Start => ("MENU", white),
                _ => ("", white),
            };
        return b switch
        {
            PadBtn.LB => ("L1", white), PadBtn.RB => ("R1", white), PadBtn.LT => ("L2", white), PadBtn.RT => ("R2", white),
            PadBtn.L3 => ("L3", white), PadBtn.R3 => ("R3", white), PadBtn.Back => ("SHARE", white), PadBtn.Start => ("OPTIONS", white),
            _ => ("", white),
        };
    }

    static readonly Rgba XboxA = Rgba.Hex(0x4CAF3D), XboxB = Rgba.Hex(0xE0443A), XboxX = Rgba.Hex(0x2D74D6), XboxY = Rgba.Hex(0xF5B700);
    static readonly Rgba PsCross = Rgba.Hex(0x74ADEB), PsCircle = Rgba.Hex(0xF0657A), PsSquare = Rgba.Hex(0xE8A0D8), PsTriangle = Rgba.Hex(0x52D6AB);
    static readonly Rgba PillFill = Rgba.Hex(0x2B303C), PillEdge = Rgba.Hex(0x6B7388), PsDisc = Rgba.Hex(0x1B1F2B), PsEdge = Rgba.Hex(0x4A5169);
    static readonly Rgba Light = Rgba.Hex(0xE9ECF4), Dim = Rgba.Hex(0x4A5169);

    // ---------------------------------------------------------------- Drawing

    sealed class Canvas
    {
        public readonly int W, H;
        public readonly float[] Px;
        public Canvas(int w, int h) { W = w; H = h; Px = new float[w * h * 4]; }

        /// <summary>Paints a colour where the signed distance (in pixels, negative inside) says the shape is.</summary>
        public void Paint(Func<float, float, float> sd, Rgba c)
        {
            for (var y = 0; y < H; y++)
                for (var x = 0; x < W; x++)
                {
                    var cov = Math.Clamp(0.5f - sd(x + 0.5f, y + 0.5f), 0f, 1f) * c.A;
                    if (cov <= 0) continue;
                    var i = (y * W + x) * 4;
                    var a0 = Px[i + 3];
                    var a = cov + a0 * (1 - cov);
                    for (var k = 0; k < 3; k++)
                    {
                        var src = k == 0 ? c.R : k == 1 ? c.G : c.B;
                        Px[i + k] = a <= 0 ? 0 : (src * cov + Px[i + k] * a0 * (1 - cov)) / a;
                    }
                    Px[i + 3] = a;
                }
        }

        public byte[] ToRgba32()
        {
            var o = new byte[W * H * 4];
            // Unity textures start at the bottom row, so the picture is written upside down.
            for (var y = 0; y < H; y++)
                for (var x = 0; x < W; x++)
                {
                    var s = (y * W + x) * 4;
                    var d = ((H - 1 - y) * W + x) * 4;
                    var a = Px[s + 3];
                    o[d] = (byte)Math.Round(Math.Clamp(Px[s], 0, 1) * 255);
                    o[d + 1] = (byte)Math.Round(Math.Clamp(Px[s + 1], 0, 1) * 255);
                    o[d + 2] = (byte)Math.Round(Math.Clamp(Px[s + 2], 0, 1) * 255);
                    o[d + 3] = (byte)Math.Round(Math.Clamp(a, 0, 1) * 255);
                }
            return o;
        }
    }

    static float Circle(float x, float y, float cx, float cy, float r) => MathF.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy)) - r;
    static float Ring(float x, float y, float cx, float cy, float r, float w) => MathF.Abs(Circle(x, y, cx, cy, r)) - w / 2;

    static float RoundRect(float x, float y, float cx, float cy, float hw, float hh, float r)
    {
        var qx = MathF.Abs(x - cx) - (hw - r);
        var qy = MathF.Abs(y - cy) - (hh - r);
        return MathF.Sqrt(MathF.Max(qx, 0) * MathF.Max(qx, 0) + MathF.Max(qy, 0) * MathF.Max(qy, 0)) + MathF.Min(MathF.Max(qx, qy), 0) - r;
    }

    static float Segment(float x, float y, float ax, float ay, float bx, float by, float w)
    {
        float dx = bx - ax, dy = by - ay;
        var t = Math.Clamp(((x - ax) * dx + (y - ay) * dy) / (dx * dx + dy * dy), 0f, 1f);
        var px = ax + t * dx; var py = ay + t * dy;
        return MathF.Sqrt((x - px) * (x - px) + (y - py) * (y - py)) - w / 2;
    }

    static float Min(params float[] v) { var m = v[0]; for (var i = 1; i < v.Length; i++) m = MathF.Min(m, v[i]); return m; }

    /// <summary>The picture of a button: 2 x <see cref="Height"/> wide, <see cref="Height"/> high, RGBA, bottom row first (Unity order).</summary>
    public static byte[] Render(PadBtn b, PadStyle s, out int width, out int height)
    {
        var h = Height; var w = h * 2;
        width = w; height = h;
        var c = new Canvas(w, h);
        float cx = w / 2f, cy = h / 2f;
        var r = h * 0.44f;

        switch (b)
        {
            case PadBtn.A or PadBtn.B or PadBtn.X or PadBtn.Y when s == PadStyle.Xbox:
                {
                    var col = b switch { PadBtn.A => XboxA, PadBtn.B => XboxB, PadBtn.X => XboxX, _ => XboxY };
                    c.Paint((x, y) => Circle(x, y, cx, cy, r + 1.5f), Rgba.Hex(0x0B0D12, 0.55f));
                    c.Paint((x, y) => Circle(x, y, cx, cy, r), col);
                    c.Paint((x, y) => Ring(x, y, cx, cy, r - 1.5f, 3f), new Rgba(1, 1, 1, 0.22f));
                    break;
                }
            case PadBtn.A or PadBtn.B or PadBtn.X or PadBtn.Y:
                {
                    c.Paint((x, y) => Circle(x, y, cx, cy, r), PsDisc);
                    c.Paint((x, y) => Ring(x, y, cx, cy, r - 1.5f, 3f), PsEdge);
                    var u = r * 0.46f;     // half size of the symbol
                    var lw = r * 0.2f;     // line width
                    switch (b)
                    {
                        case PadBtn.A: // Cross
                            c.Paint((x, y) => Min(Segment(x, y, cx - u, cy - u, cx + u, cy + u, lw), Segment(x, y, cx - u, cy + u, cx + u, cy - u, lw)), PsCross);
                            break;
                        case PadBtn.B: // Circle
                            c.Paint((x, y) => Ring(x, y, cx, cy, u * 1.12f, lw), PsCircle);
                            break;
                        case PadBtn.X: // Square
                            c.Paint((x, y) => MathF.Abs(RoundRect(x, y, cx, cy, u, u, lw * 0.6f) + lw / 2) - lw / 2, PsSquare);
                            break;
                        default: // Triangle
                            {
                                float ax = cx, ay = cy - u * 1.15f, bx = cx - u * 1.1f, by = cy + u * 0.8f, ex = cx + u * 1.1f, ey = cy + u * 0.8f;
                                c.Paint((x, y) => Min(Segment(x, y, ax, ay, bx, by, lw), Segment(x, y, bx, by, ex, ey, lw), Segment(x, y, ex, ey, ax, ay, lw)), PsTriangle);
                                break;
                            }
                    }
                    break;
                }
            case PadBtn.LB or PadBtn.RB:
                {
                    var hw = w * 0.40f; var hh = h * 0.34f;
                    c.Paint((x, y) => RoundRect(x, y, cx, cy, hw + 1.5f, hh + 1.5f, hh * 0.9f), Rgba.Hex(0x0B0D12, 0.55f));
                    c.Paint((x, y) => RoundRect(x, y, cx, cy, hw, hh, hh * 0.85f), PillFill);
                    c.Paint((x, y) => Ring2(RoundRect(x, y, cx, cy, hw - 1.5f, hh - 1.5f, hh * 0.8f), 3f), PillEdge);
                    break;
                }
            case PadBtn.LT or PadBtn.RT:
                {
                    var hw = w * 0.30f; var hh = h * 0.44f;
                    c.Paint((x, y) => RoundRect(x, y, cx, cy, hw + 1.5f, hh + 1.5f, hh * 0.55f), Rgba.Hex(0x0B0D12, 0.55f));
                    c.Paint((x, y) => RoundRect(x, y, cx, cy, hw, hh, hh * 0.5f), PillFill);
                    c.Paint((x, y) => Ring2(RoundRect(x, y, cx, cy, hw - 1.5f, hh - 1.5f, hh * 0.46f), 3f), PillEdge);
                    break;
                }
            case PadBtn.L3 or PadBtn.R3:
                {
                    c.Paint((x, y) => Circle(x, y, cx, cy, r + 1.5f), Rgba.Hex(0x0B0D12, 0.55f));
                    c.Paint((x, y) => Circle(x, y, cx, cy, r), PillFill);
                    c.Paint((x, y) => Ring(x, y, cx, cy, r - 1.5f, 3f), PillEdge);
                    break;
                }
            case PadBtn.Back or PadBtn.Start:
                {
                    var hw = w * 0.40f; var hh = h * 0.30f;
                    c.Paint((x, y) => RoundRect(x, y, cx, cy, hw + 1.5f, hh + 1.5f, hh), Rgba.Hex(0x0B0D12, 0.55f));
                    c.Paint((x, y) => RoundRect(x, y, cx, cy, hw, hh, hh), PillFill);
                    c.Paint((x, y) => Ring2(RoundRect(x, y, cx, cy, hw - 1.5f, hh - 1.5f, hh * 0.9f), 3f), PillEdge);
                    break;
                }
            case PadBtn.DpadDown:
                {
                    // A plus-shaped pad, the down arm lit.
                    var arm = r * 0.36f; var len = r * 0.95f;
                    float Plus(float x, float y) => Min(RoundRect(x, y, cx, cy, len, arm, arm * 0.35f), RoundRect(x, y, cx, cy, arm, len, arm * 0.35f));
                    c.Paint((x, y) => Plus(x, y) + 1.5f, Rgba.Hex(0x0B0D12, 0.55f));
                    c.Paint((x, y) => Plus(x, y), Dim);
                    c.Paint((x, y) => RoundRect(x, y, cx, cy + len * 0.55f, arm, len * 0.45f, arm * 0.35f), Light);
                    c.Paint((x, y) => Ring2(Plus(x, y), 2f), Rgba.Hex(0x0B0D12, 0.35f));
                    break;
                }
            default: // RightStick: a stick cap with arrows round it
                {
                    c.Paint((x, y) => Circle(x, y, cx, cy, r * 0.62f + 1.5f), Rgba.Hex(0x0B0D12, 0.55f));
                    c.Paint((x, y) => Circle(x, y, cx, cy, r * 0.62f), PillFill);
                    c.Paint((x, y) => Ring(x, y, cx, cy, r * 0.62f - 1.5f, 3f), PillEdge);
                    c.Paint((x, y) => Circle(x, y, cx, cy, r * 0.30f), Light);
                    var d = r * 0.86f; var t = r * 0.16f;
                    float Arrow(float x, float y, float dx, float dy)
                    {
                        // a small triangle pointing away from the centre
                        float px = cx + dx * d, py = cy + dy * d;
                        float nx = -dy, ny = dx;
                        float ax = px + dx * t, ay = py + dy * t;
                        float bx = px - dx * t * 0.2f + nx * t * 1.1f, by = py - dy * t * 0.2f + ny * t * 1.1f;
                        float ex = px - dx * t * 0.2f - nx * t * 1.1f, ey = py - dy * t * 0.2f - ny * t * 1.1f;
                        return Min(Segment(x, y, ax, ay, bx, by, t * 0.9f), Segment(x, y, bx, by, ex, ey, t * 0.9f), Segment(x, y, ex, ey, ax, ay, t * 0.9f));
                    }
                    c.Paint((x, y) => Min(Arrow(x, y, 0, -1), Arrow(x, y, 0, 1), Arrow(x, y, -1, 0), Arrow(x, y, 1, 0)), Light);
                    break;
                }
        }
        return c.ToRgba32();
    }

    static float Ring2(float sd, float width) => MathF.Abs(sd + width / 2) - width / 2;

    /// <summary>A rounded rectangle picture (panel, row highlight, ON/OFF pill): fill plus an optional border, RGBA, bottom row first.</summary>
    public static byte[] RenderRounded(int w, int h, float radius, Rgba fill, Rgba border, float borderWidth)
    {
        var c = new Canvas(w, h);
        float cx = w / 2f, cy = h / 2f;
        var r = Math.Min(radius, Math.Min(w, h) / 2f);
        c.Paint((x, y) => RoundRect(x, y, cx, cy, w / 2f, h / 2f, r), fill);
        if (borderWidth > 0 && border.A > 0)
            c.Paint((x, y) => Ring2(RoundRect(x, y, cx, cy, w / 2f, h / 2f, r), borderWidth), border);
        return c.ToRgba32();
    }

    // ---------------------------------------------------------------- Controller kind

    /// <summary>Which pad style the name of a connected controller suggests (DualShock / DualSense / "Wireless Controller" = PlayStation, else Xbox).</summary>
    public static PadStyle StyleFromName(string name)
    {
        var n = (name ?? "").ToLowerInvariant();
        if (n.Contains("dualsense") || n.Contains("dualshock") || n.Contains("playstation") || n.Contains("ps4") || n.Contains("ps5")
            || n.Contains("sony") || n.Contains("wireless controller") || n.Contains("054c"))
            return PadStyle.PlayStation;
        return PadStyle.Xbox;
    }
}
