using System.Diagnostics;
using Jibbers.Sdk;
using Jibbers.Sdk.Interop;
using RickBackflip;

namespace HankModPack;

/// <summary>
/// The quick menu panel, on the left of the screen: a list of button pictures with what each one does. Not clickable (no raycast target).
/// Drawn with the game's own text font on a Unity Canvas above the game, like the HUD. Rebuilt only when what it shows changes.
/// Main thread only.
/// </summary>
internal sealed unsafe class QuickMenuOverlay : IDisposable
{
    /// <summary>One line of the panel. <paramref name="Badge"/> is a small pill on the right ("ON", "OFF", "ACTIVE"), empty for none.</summary>
    public readonly record struct Row(PadBtn Icon, string Label, string Badge, bool BadgeOn, bool Highlight,
        bool HasIcon = true, string Value = "", float Bar = -1f, bool Small = false, IReadOnlyList<(PadBtn Icon, string Text)>? Legend = null);

    // Layout, in canvas pixels at 100 %.
    public const float PanelWidth = 560, Margin = 28, Pad = 20, HeaderHeight = 66, RowHeight = 62, IconHeight = 46, FootPad = 14;

    OwnedGameObject? root;
    string signature = "";
    long lastTry;
    readonly List<GameRef> textures = new();
    readonly Dictionary<(PadBtn, PadStyle), GameRef> icons = new();

    public Action<string>? Logger { get; set; }

    /// <summary>The panel sits at the right of the screen instead of the left.</summary>
    public bool RightSide { get; set; }

    /// <summary>Small hint panel: in the bottom corner (right or left side), without the D-pad picture and the title.</summary>
    public bool Compact { get; set; }

    // Geometry of the last panel built (canvas units), so the mouse can be matched with a row.
    float lastK = 1, lastW, lastH, lastLabelX; bool lastRight;
    readonly List<(float Y0, float Y1)> rowGeo = new();

    /// <summary>The row under the mouse: its index, the position along the row (0..1) and along its bar (0..1, clamped).</summary>
    public (int Row, float Fx, float Bar)? HitTest(float screenX, float screenY)
    {
        if (root is null || rowGeo.Count == 0 || !Pointer.Screen(out var sw, out var sh)) return null;
        var s = Pointer.CanvasScale();
        var cw = sw / s; var ch = sh / s;
        var left = lastRight ? cw - Margin * lastK - lastW : Margin * lastK;
        var top = ch / 2 - lastH / 2;
        var lx = screenX / s - left; var ly = (sh - screenY) / s - top;
        if (lx < 0 || lx > lastW || ly < 0 || ly > lastH) return null;
        var bx = lastLabelX; var bw = lastW - lastLabelX - Pad * lastK - 12 * lastK;
        for (var i = 0; i < rowGeo.Count; i++)
            if (ly >= rowGeo[i].Y0 && ly < rowGeo[i].Y1)
                return (i, lx / lastW, bw > 1 ? Math.Clamp((lx - bx) / bw, 0, 1) : 0);
        return null;
    }
    public bool Visible => root is not null;

    static readonly ButtonArt.Rgba PanelFill = new(0.055f, 0.063f, 0.094f);
    static readonly ButtonArt.Rgba PanelEdge = new(1, 1, 1, 0.12f);
    static readonly ButtonArt.Rgba Accent = new(0.32f, 0.55f, 0.95f);

    /// <param name="look">The HUD theme to dress the panel in (null = the default dark panel).</param>
    public void Show(string title, IReadOnlyList<Row> rows, PadStyle style, float scalePercent, float opacityPercent, Look? look = null, float width = PanelWidth)
    {
        var sig = $"{RightSide}|{Compact}|{title}|{style}|{scalePercent}|{opacityPercent}|{look?.Name}|{width}|" + string.Join(";", rows.Select(r => $"{r.Icon},{r.Label},{r.Badge},{r.BadgeOn},{r.Highlight},{r.HasIcon},{r.Value},{r.Bar:0.###},{r.Small},{(r.Legend is null ? "" : string.Join("/", r.Legend.Select(l => l.Icon + l.Text)))}"));
        if (root is not null && sig == signature && !Unity.IsNull(root.Target)) return;

        // Not more than one try per half second (the game's font is not always ready).
        var now = Stopwatch.GetTimestamp();
        if (root is null && lastTry != 0 && Stopwatch.GetElapsedTime(lastTry, now).TotalSeconds < 0.5) return;
        lastTry = now;

        var font = TextBanner.FindFont();
        if (font == 0) return;
        Teardown();
        OwnedGameObject? canvas = null;
        try
        {
            canvas = OverlayCanvas.BuildRoot("HankModPack.QuickMenu");
            Build(canvas, title, rows, style, scalePercent / 100f, Math.Clamp(opacityPercent / 100f, 0.2f, 1f), font, look, width);
            root = canvas;
            signature = sig;
        }
        catch (Exception e)
        {
            Logger?.Invoke("quick menu: build failed: " + e.Message);
            canvas?.Dispose();
            Teardown();
        }
    }

    public void Hide() => Teardown();

    void Teardown()
    {
        var r = root;
        root = null;
        signature = "";
        r?.Dispose();
        foreach (var t in textures)
        {
            try { if (!Unity.IsNull(t.Target)) Unity.Destroy(t.Target); } catch { }
            t.Dispose();
        }
        textures.Clear();
    }

    public void Dispose()
    {
        Teardown();
        foreach (var t in icons.Values)
        {
            try { if (!Unity.IsNull(t.Target)) Unity.Destroy(t.Target); } catch { }
            t.Dispose();
        }
        icons.Clear();
    }

    // ---------------------------------------------------------------- Building

    static ButtonArt.Rgba Rgba(Color c, float a = -1) => new(c.R, c.G, c.B, a < 0 ? c.A : a);
    static Color Tint(string hex) => HudThemes.Hex(hex);

    void Build(OwnedGameObject canvas, string title, IReadOnlyList<Row> rows, PadStyle style, float k, float opacity, nint font, Look? look, float baseWidth)
    {
        const float SmallHeight = 40;
        var w = baseWidth * k;
        var hdr = Compact ? 6f : HeaderHeight;
        var total = hdr + FootPad;
        foreach (var r in rows) total += r.Small ? SmallHeight : RowHeight;
        var h = total * k;

        // Colours: the HUD theme when there is one, else the default dark panel.
        var fill = look is null ? PanelFill with { A = 0.88f * opacity } : Rgba(look.Fill, look.NoBg ? 0f : Math.Max(look.Fill.A, 0.9f) * opacity);
        var edge = look is null ? PanelEdge : look.NoBg ? Rgba(look.Border, 0f) : look.BorderWidth > 0 ? Rgba(look.Border, Math.Max(look.Border.A, 0.9f) * opacity) : PanelEdge;
        var edgeW = look is null ? 2f : Math.Max(2f, look.BorderWidth);
        var radius = look is null ? 22f : Math.Clamp(look.Radius, 6f, 34f);
        var accent = look is null ? Accent : Rgba(Tint(look.Accent), 1f);
        var titleColor = look is null ? new Color(0.80f, 0.84f, 0.93f, 1) : Tint(look.Label);
        var textColor = look is null ? new Color(0.96f, 0.97f, 1, 1) : Tint(look.Value);
        var dimColor = look is null ? new Color(0.62f, 0.67f, 0.78f, 1) : Tint(look.Label);
        var valueColor = look is null ? new Color(0.62f, 0.85f, 1f, 1) : Tint(look.Accent);

        outlineText = HudThemes.NeedsOutline(look);
        var panelPixels = ButtonArt.RenderRounded((int)w, (int)h, radius * k, fill, edge, edgeW);
        if (look != null) HudTextures.PaintRounded(panelPixels, (int)w, (int)h, k, fill, look);
        var panelTex = Own(HudArt.MakeTexture(panelPixels, (int)w, (int)h));
        using var panel = OwnedGameObject.Create("Panel");
        var raw = panel.AddComponent("UnityEngine.UI.RawImage");
        UnityProperties.Set(raw, "set_raycastTarget", false);
        Unity.Call(raw, "set_texture", panelTex);
        var rect = Unity.Call(raw, "get_rectTransform");
        var anchor = new Vector2(RightSide ? 1 : 0, Compact ? 0f : 0.5f);
        UnityProperties.Set(rect, "set_anchorMin", anchor);
        UnityProperties.Set(rect, "set_anchorMax", anchor);
        UnityProperties.Set(rect, "set_pivot", anchor);
        UnityProperties.Set(rect, "set_anchoredPosition", new Vector2(Compact ? (RightSide ? -6f : 6f) : RightSide ? -Margin * k : Margin * k, Compact ? 6f : 0));
        lastK = k; lastW = w; lastH = h; lastRight = RightSide; lastLabelX = Pad * k + 4 * k; rowGeo.Clear();
        UnityProperties.Set(rect, "set_sizeDelta", new Vector2(w, h));
        panel.AttachTo(canvas.Transform);

        // Header: the D-pad (down lit) and the title.
        var iconH = IconHeight * k;
        if (!Compact)
        {
            Picture(panel, Icon(PadBtn.DpadDown, style), Pad * k - 6 * k, 10 * k, iconH * 2 * 0.7f, iconH * 0.7f);
            Text(panel, title, font, 27 * k, titleColor, "MidlineLeft", Pad * k + 62 * k, 8 * k, w - 2 * Pad * k - 62 * k, 44 * k, bold: true);
        }

        var yy = hdr * k;
        foreach (var row in rows)
        {
            var rh = (row.Small ? SmallHeight : RowHeight) * k;
            var y = yy;
            yy += rh;
            rowGeo.Add((y, y + rh));
            if (row.Highlight)
            {
                var hl = Own(HudArt.MakeTexture(ButtonArt.RenderRounded((int)(w - 20 * k), (int)(rh - 6 * k), 14 * k, accent with { A = 0.55f * opacity }, new ButtonArt.Rgba(1, 1, 1, 0.5f), 2f),
                    (int)(w - 20 * k), (int)(rh - 6 * k)));
                Picture(panel, hl, 10 * k, y + 3 * k, w - 20 * k, rh - 6 * k);
            }

            var iconW = iconH * 2;
            var labelX = Pad * k + 4 * k;
            if (row.HasIcon)
            {
                var iconX = Pad * k - 6 * k;
                var iconY = y + (rh - iconH) / 2;
                Picture(panel, Icon(row.Icon, style), iconX, iconY, iconW, iconH);
                var (text, col) = ButtonArt.Label(row.Icon, style);
                if (text.Length > 0)
                {
                    var size = text.Length switch { 1 => 30f, 2 => 25f, 3 => 19f, 4 => 15f, _ => 12.5f } * k;
                    Text(panel, text, font, size, new Color(col.R, col.G, col.B, 1), "Midline", iconX, iconY, iconW, iconH, bold: true);
                }
                labelX = Pad * k + iconW - 6 * k + 16 * k;
            }

            if (row.Legend is { Count: > 0 } legend)
            {
                // A line of key hints: the button picture, then what it does.
                var ih = 30 * k; var iw = ih * 2; var lx = Pad * k;
                foreach (var (icon, text) in legend)
                {
                    var iy = y + (rh - ih) / 2;
                    Picture(panel, Icon(icon, style), lx - 4 * k, iy, iw, ih);
                    var (lt, lc) = ButtonArt.Label(icon, style);
                    if (lt.Length > 0)
                    {
                        var sz = lt.Length switch { 1 => 20f, 2 => 16f, 3 => 12.5f, 4 => 10f, _ => 8.5f } * k;
                        Text(panel, lt, font, sz, new Color(lc.R, lc.G, lc.B, 1), "Midline", lx - 4 * k, iy, iw, ih, bold: true);
                    }
                    lx += iw - 4 * k;
                    if (text.Length > 0)
                    {
                        var tw = (text.Length * 11 + 10) * k;
                        Text(panel, text, font, 20 * k, dimColor, "MidlineLeft", lx, y, tw, rh, bold: false, fit: true);
                        lx += tw + 6 * k;
                    }
                }
                continue;
            }

            var badgeW = row.Badge.Length > 0 ? 92 * k : 0;
            var valueW = row.Value.Length > 0 ? Math.Clamp((row.Value.Length * 17 + 24) * k, 90 * k, w * 0.5f) : 0;
            if (row.Small)
                Text(panel, row.Label, font, 21 * k, dimColor, "MidlineLeft", labelX, y, w - labelX - Pad * k, rh, bold: false, fit: true);
            else
                Text(panel, row.Label, font, 33 * k, textColor, "MidlineLeft", labelX, y, w - labelX - Pad * k - badgeW - valueW - 10 * k, rh, bold: false, fit: true);

            if (row.Value.Length > 0)
                Text(panel, row.Value, font, 29 * k, row.Highlight ? new Color(1, 1, 1, 1) : valueColor, "MidlineRight", w - Pad * k - valueW, y, valueW, rh, bold: true, fit: true);

            if (row.Bar >= 0)
            {
                var bx = labelX; var bw = w - labelX - Pad * k - 12 * k; var bh = 5 * k; var by = y + rh - 11 * k;
                var track = Own(HudArt.MakeTexture(ButtonArt.RenderRounded((int)bw, (int)bh, bh / 2, new ButtonArt.Rgba(1, 1, 1, 0.22f), new ButtonArt.Rgba(1, 1, 1, 0f), 0f), (int)bw, (int)bh));
                Picture(panel, track, bx, by, bw, bh);
                var fw = Math.Max(bh, bw * Math.Clamp(row.Bar, 0, 1));
                var bar = Own(HudArt.MakeTexture(ButtonArt.RenderRounded((int)fw, (int)bh, bh / 2, accent, new ButtonArt.Rgba(1, 1, 1, 0f), 0f), (int)fw, (int)bh));
                Picture(panel, bar, bx, by, fw, bh);
            }

            if (row.Badge.Length > 0)
            {
                var bw = 80 * k; var bh = 36 * k;
                var bx = w - Pad * k - bw;
                var by = y + (rh - bh) / 2;
                var back = row.BadgeOn ? new ButtonArt.Rgba(0.20f, 0.68f, 0.35f, 0.95f) : new ButtonArt.Rgba(0.78f, 0.25f, 0.25f, 0.95f);
                var pill = Own(HudArt.MakeTexture(ButtonArt.RenderRounded((int)bw, (int)bh, bh / 2, back, new ButtonArt.Rgba(1, 1, 1, 0.35f), 2f), (int)bw, (int)bh));
                Picture(panel, pill, bx, by, bw, bh);
                Text(panel, row.Badge, font, 22 * k, new Color(1, 1, 1, 1), "Midline", bx, by, bw, bh, bold: true);
            }
        }
    }

    nint Own(nint texture)
    {
        textures.Add(new GameRef(texture));
        return texture;
    }

    nint Icon(PadBtn b, PadStyle s)
    {
        if (icons.TryGetValue((b, s), out var hit) && !Unity.IsNull(hit.Target)) return hit.Target;
        var rgba = ButtonArt.Render(b, s, out var w, out var h);
        var tex = HudArt.MakeTexture(rgba, w, h);
        icons[(b, s)] = new GameRef(tex);
        return tex;
    }

    /// <summary>A picture, placed from the top-left corner of the panel.</summary>
    static void Picture(OwnedGameObject parent, nint texture, float x, float y, float w, float h)
    {
        using var go = OwnedGameObject.Create("Image");
        var raw = go.AddComponent("UnityEngine.UI.RawImage");
        UnityProperties.Set(raw, "set_raycastTarget", false);
        Unity.Call(raw, "set_texture", texture);
        PlaceTopLeft(Unity.Call(raw, "get_rectTransform"), x, y, w, h);
        go.AttachTo(parent.Transform);
    }

    static bool outlineText;

    static void Text(OwnedGameObject parent, string text, nint font, float size, Color color, string alignment, float x, float y, float w, float h, bool bold, bool fit = false)
    {
        using var go = OwnedGameObject.Create("Text");
        var tmp = go.AddComponent("TMPro.TextMeshProUGUI");
        Unity.Call(tmp, "set_font", font);
        UnityProperties.Set(tmp, "set_raycastTarget", false);
        UnityProperties.Set(tmp, "set_richText", false);
        var options = Unity.Class("TMPro.TextAlignmentOptions");
        var field = options.Field(alignment);
        UnityProperties.Set(tmp, "set_alignment", field.IsNull ? options.Field("Left").Get<int>(0) : field.Get<int>(0));
        UnityProperties.Set(tmp, "set_fontSize", size);
        if (fit)
        {
            // Long texts shrink to fit their box instead of running into the next column.
            UnityProperties.Set(tmp, "set_fontSizeMin", size * 0.45f);
            UnityProperties.Set(tmp, "set_fontSizeMax", size);
            UnityProperties.Set(tmp, "set_enableAutoSizing", (byte)1);
        }
        if (!Il2Cpp.ClassOf(tmp).Method("set_textWrappingMode", 1).IsNull) UnityProperties.Set(tmp, "set_textWrappingMode", 0);
        else UnityProperties.Set(tmp, "set_enableWordWrapping", (byte)0);
        UnityProperties.Set(tmp, "set_color", color);
        if (bold) UnityProperties.Set(tmp, "set_fontStyle", 1);
        if (outlineText) HudThemes.Outline(tmp);
        PlaceTopLeft(Unity.Call(tmp, "get_rectTransform"), x, y, w, h);
        go.AttachTo(parent.Transform);
        Unity.Call(tmp, "set_text", Il2Cpp.String(text));
    }

    static void PlaceTopLeft(nint rect, float x, float y, float w, float h)
    {
        var anchor = new Vector2(0, 1);
        UnityProperties.Set(rect, "set_anchorMin", anchor);
        UnityProperties.Set(rect, "set_anchorMax", anchor);
        UnityProperties.Set(rect, "set_pivot", anchor);
        UnityProperties.Set(rect, "set_anchoredPosition", new Vector2(x, -y));
        UnityProperties.Set(rect, "set_sizeDelta", new Vector2(w, h));
    }
}
