using Jibbers.Sdk;
using Jibbers.Sdk.Interop;

namespace HankModPack;

/// <summary>Un thème du HUD : la forme (rayon, coins coupés, bordure, ombre), les couleurs, le style du texte et les couleurs des flammes.</summary>
sealed record Look(
    string Name,
    Color Fill, Color FillBottom, Color Border, float BorderWidth,
    Color Shadow, float ShadowX, float ShadowY, float ShadowBlur,
    float Radius, float Chamfer, float Tilt, bool Bold,
    string Label, string Value, string Accent,
    string Flame1, string Flame2, bool NoBg = false, string? Tex = null);

static class HudThemes
{
    public static Color Hex(string h, float a = 1)
    {
        var n = Convert.ToInt32(h.TrimStart('#'), 16);
        return new Color(((n >> 16) & 255) / 255f, ((n >> 8) & 255) / 255f, (n & 255) / 255f, a);
    }

    static readonly Color None = new(0, 0, 0, 0);

    static readonly Look[] Base =
    {
        new("White", Hex("#FFFFFF", .95f), Hex("#FFFFFF", .95f), None, 0, Hex("#000000", .28f), 0, -3, 6,
            14, 0, 0, false, "#777777", "#111111", "#111111", "#FFFFFF", "#9FB4C8"),
        new("Black", Hex("#111111", .95f), Hex("#111111", .95f), None, 0, Hex("#000000", .35f), 0, -3, 6,
            14, 0, 0, false, "#9A9A9A", "#FFFFFF", "#FFFFFF", "#E8E8E8", "#3A3A3A"),
        new("Vaporwave", Hex("#1A0B3A", .94f), Hex("#1A0B3A", .94f), Hex("#01CDFE"), 4, Hex("#FF71CE"), 7, -7, 0,
            0, 0, 0, false, "#B967FF", "#01CDFE", "#05FFA1", "#FF71CE", "#01CDFE"),
        new("Cyberpunk", Hex("#0D0A1A", .95f), Hex("#0D0A1A", .95f), Hex("#FCEE0A"), 3, None, 0, 0, 0,
            0, 16, 0, true, "#00F0FF", "#FCEE0A", "#FF003C", "#FCEE0A", "#FF003C"),
        new("Terminal", Hex("#000000", .95f), Hex("#000000", .95f), Hex("#33FF66"), 2, None, 0, 0, 0,
            0, 0, 0, false, "#33FF66", "#33FF66", "#33FF66", "#B6FFC8", "#33FF66"),
        new("Ice", Hex("#FFFFFF", .72f), Hex("#EAF4FF", .72f), None, 0, Hex("#2A5C85", .22f), 0, -3, 8,
            18, 0, 0, false, "#4A7A9B", "#12324A", "#12324A", "#FFFFFF", "#7FC4FF"),
        new("Arcade", Hex("#000000", .96f), Hex("#000000", .96f), Hex("#FFFFFF"), 5, Hex("#FF004D"), 6, -6, 0,
            0, 0, 0, true, "#FFEC27", "#FFFFFF", "#00E436", "#FFEC27", "#FF004D"),
        new("Rounded", Hex("#FFFFFF", .97f), Hex("#E9EDFF", .97f), None, 0, Hex("#1B1F3B", .25f), 0, -5, 12,
            38, 0, 0, true, "#8A8FA8", "#1B1F3B", "#6C63FF", "#8F88FF", "#D9D6FF"),
        new("Hip Hop", Hex("#FFD400"), Hex("#FFD400"), Hex("#000000"), 6, Hex("#000000"), 9, -9, 0,
            6, 0, 2.5f, true, "#5A4A00", "#000000", "#D6003A", "#FFD400", "#D6003A"),
        new("Freestyle", Hex("#FF2E93"), Hex("#FF9F1C"), Hex("#FFFFFF"), 4, Hex("#2B1055"), 7, -7, 0,
            30, 0, 3f, true, "#FFF3D6", "#FFFFFF", "#FFFB96", "#FFFB96", "#FF2E93"),
        new("Graffiti", Hex("#1B1B1B", .97f), Hex("#1B1B1B", .97f), Hex("#B6FF00"), 4, Hex("#FF2D95"), 7, -7, 0,
            24, 0, 2f, true, "#B6FF00", "#FFFFFF", "#FF2D95", "#B6FF00", "#FF2D95"),
        new("Sunset", Hex("#FF5E62", .97f), Hex("#FF9966", .97f), Hex("#FFF1D0"), 3, Hex("#5B2A86", .45f), 0, -5, 10,
            26, 0, 0, true, "#FFF1D0", "#FFFFFF", "#FFE08A", "#FFF1B0", "#FF5E62"),
        new("Ocean", Hex("#1D8FA8", .96f), Hex("#0B3C5D", .96f), Hex("#7FEFFF"), 3, Hex("#021B2B", .5f), 0, -5, 10,
            22, 0, 0, false, "#A8F0FF", "#FFFFFF", "#FFE066", "#E0FFFF", "#00B4D8"),
        new("Lava", Hex("#4A0E00", .97f), Hex("#1A0500", .97f), Hex("#FF6A00"), 3, Hex("#FF2200"), 5, -5, 0,
            10, 0, 0, true, "#FFB36B", "#FFFFFF", "#FFD60A", "#FFE45E", "#FF2200"),
        new("Forest", Hex("#1B4D32", .96f), Hex("#0F2A1D", .96f), Hex("#8FD694"), 2, Hex("#02110A", .5f), 0, -4, 8,
            18, 0, 0, false, "#A5D6A7", "#FFFFFF", "#D4E157", "#D4E157", "#2E9E5B"),
        new("Candy", Hex("#FFE3F1", .97f), Hex("#D8F3FF", .97f), Hex("#FF6FB5"), 4, Hex("#FF6FB5", .45f), 0, -5, 8,
            36, 0, 0, true, "#E0569A", "#5A2A82", "#00A8E8", "#FF9ACB", "#7FD6FF"),
        new("Gold", Hex("#2B2108", .97f), Hex("#1A1405", .97f), Hex("#D4AF37"), 3, Hex("#000000", .45f), 0, -4, 8,
            8, 0, 0, true, "#BFA14A", "#F5E6A8", "#FFD700", "#FFF2B3", "#D4AF37"),
        new("Midnight", Hex("#1A2350", .96f), Hex("#0A0F24", .96f), Hex("#4C6EF5"), 2, Hex("#000000", .5f), 0, -4, 10,
            20, 0, 0, false, "#8DA2FF", "#FFFFFF", "#74C0FC", "#A5D8FF", "#4C6EF5"),
        new("Sakura", Hex("#FFF0F5", .97f), Hex("#FFD6E4", .97f), Hex("#FFFFFF"), 3, Hex("#E91E63", .28f), 0, -4, 10,
            30, 0, 0, false, "#B5547A", "#5C2040", "#E91E63", "#FFFFFF", "#FF8FB8"),
        new("Steel plate", Hex("#9AA3AB"), Hex("#5E666E"), Hex("#2B3036"), 4, Hex("#000000", .5f), 0, -5, 6,
            4, 10, 0, true, "#E6EAEE", "#FFFFFF", "#FF5A4D", "#FFD27A", "#7F8A94", Tex: "steel"),
        new("Wood", Hex("#A9743F"), Hex("#6B4423"), Hex("#3B2412"), 5, Hex("#000000", .45f), 0, -5, 6,
            10, 0, 0, true, "#F3DFC1", "#FFF6E6", "#FFC857", "#FFC857", "#C0763C", Tex: "wood"),
        new("Wool", Hex("#E9D8B8"), Hex("#CDB68E"), Hex("#B23A48"), 6, Hex("#5A3A22", .35f), 0, -4, 8,
            28, 0, 0, true, "#FFE9D6", "#FFFFFF", "#FFD27A", "#F2B8A0", "#B23A48", Tex: "wool"),
        new("Winter", Hex("#F4FAFF", .96f), Hex("#BFDDF5", .96f), Hex("#FFFFFF"), 4, Hex("#1F4E79", .3f), 0, -4, 10,
            22, 0, 0, true, "#E8F3FF", "#FFFFFF", "#7CC4FF", "#FFFFFF", "#9ED0FF", Tex: "winter"),
        new("Summer", Hex("#FFD93D", .97f), Hex("#FF8C42", .97f), Hex("#00B4D8"), 4, Hex("#0077B6", .4f), 0, -5, 8,
            30, 0, 0, true, "#E6F7FF", "#FFFFFF", "#FFF3B0", "#FFF3B0", "#FF6B35", Tex: "summer"),
        new("X Games", Hex("#0A0A0A", .97f), Hex("#0A0A0A", .97f), Hex("#FF6A13"), 5, Hex("#FF6A13"), 6, -6, 0,
            0, 14, 0, true, "#FF6A13", "#FFFFFF", "#FF6A13", "#FF6A13", "#FFFFFF"),
        new("SLVSH", Hex("#101010", .96f), Hex("#1E1E1E", .96f), Hex("#E8E8E8"), 3, Hex("#000000", .5f), 0, -4, 6,
            2, 0, 0, true, "#9A9A9A", "#FFFFFF", "#FF3B30", "#E8E8E8", "#FF3B30"),
        new("Skis", Hex("#F2F4F7"), Hex("#C9D3DE"), Hex("#E4002B"), 6, Hex("#14213D", .4f), 0, -5, 6,
            40, 0, -2f, true, "#14213D", "#0B132B", "#E4002B", "#FFFFFF", "#E4002B"),
        new("Toxic", Hex("#0E0E0E", .97f), Hex("#0E0E0E", .97f), Hex("#39FF14"), 3, Hex("#FFEE00"), 5, -5, 0,
            0, 12, 0, true, "#39FF14", "#FFFFFF", "#FFEE00", "#CFFF00", "#39FF14"),
    };

    /// <summary>Every theme followed by its twin without background, then the text-only theme: L1 / R1 alternate normal / transparent.</summary>
    public static readonly Look[] All = Base.SelectMany(l => new[] { l, Bare(l, l.Name + " (no background)") })
        .Append(new Look("Text only", None, None, None, 0, None, 0, 0, 0, 0, 0, 0, true, "#E6E6E6", "#FFFFFF", "#FFFFFF", "#FFFFFF", "#9FB4C8", NoBg: true)).ToArray();

    /// <summary>Dark outline on a TextMeshPro text so it stays readable without a window behind it. Never throws.</summary>
    public static void Outline(nint tmp)
    {
        try
        {
            UnityProperties.Set(tmp, "set_outlineWidth", 0.22f);
            UnityProperties.Set(tmp, "set_outlineColor", new global::RickBackflip.Color32(0, 0, 0, 255));
        }
        catch { }
    }

    public static string[] Names => All.Select(l => l.Name).ToArray();

    /// <summary>Same theme without its window: no fill, border or shadow, only the text (and the flames) stay.</summary>
    static Look Bare(Look l, string name) => l with
    {
        Name = name, Fill = None, FillBottom = None, Border = None, BorderWidth = 0, Shadow = None, ShadowX = 0, ShadowY = 0, ShadowBlur = 0,
        Radius = 0, Chamfer = 0, NoBg = true, Tex = null,
    };

    /// <summary>Text on a textured window gets the same dark outline as the no-background themes, so it reads on any part of the material.</summary>
    public static bool NeedsOutline(Look? l) => l != null && (l.NoBg || l.Tex != null);

    /// <summary>The theme with its window: a "(no background)" theme gives its normal twin, "Text only" gives the default dark panel (null). For windows that must stay readable over the game's own texts.</summary>
    public static Look? WithWindow(Look? l)
    {
        if (l is null || !l.NoBg) return l;
        const string suffix = " (no background)";
        return l.Name.EndsWith(suffix) ? Base.FirstOrDefault(b => b.Name == l.Name[..^suffix.Length]) : null;
    }

    public static Look Get(string name) => All.FirstOrDefault(l => l.Name == name) ?? All[0];
}

/// <summary>Dessine une fenêtre (ombre, bordure, fond, dégradé) dans une texture RGBA, avec des coins arrondis ou coupés lisses.</summary>
static unsafe class HudArt
{
    const int Scale = 2; // la texture a deux fois la résolution du Canvas : bords lisses

    /// <summary>Marge autour de la fenêtre pour l'ombre (en pixels du Canvas).</summary>
    public static int Padding(Look l) => (int)Math.Ceiling(Math.Max(Math.Abs(l.ShadowX), Math.Abs(l.ShadowY)) + l.ShadowBlur * 2) + 3;

    /// <summary>Distance signée au rectangle arrondi (ou à coins coupés) : négative dedans, positive dehors.</summary>
    static float Sdf(float u, float v, float w, float h, float r, float chamfer)
    {
        float ax = MathF.Abs(u - w / 2), ay = MathF.Abs(v - h / 2);
        float qx = ax - (w / 2 - r), qy = ay - (h / 2 - r);
        var d = MathF.Sqrt(MathF.Max(qx, 0) * MathF.Max(qx, 0) + MathF.Max(qy, 0) * MathF.Max(qy, 0)) + MathF.Min(MathF.Max(qx, qy), 0) - r;
        if (chamfer > 0) d = MathF.Max(d, (ax + ay - (w / 2 + h / 2 - chamfer)) * 0.70710678f);
        return d;
    }

    static float Clamp01(float x) => x < 0 ? 0 : x > 1 ? 1 : x;

    /// <param name="mirror">Fenêtre de droite : l'ombre est inversée en x.</param>
    public static (byte[] Rgba, int Width, int Height) Render(Look l, float w, float h, bool mirror)
    {
        var pad = Padding(l);
        var texW = (int)Math.Ceiling(w + 2 * pad) * Scale;
        var texH = (int)Math.Ceiling(h + 2 * pad) * Scale;
        var data = new byte[texW * texH * 4];
        var shadowX = mirror ? -l.ShadowX : l.ShadowX;
        var radius = MathF.Min(l.Radius, MathF.Min(w, h) / 2);
        var tex = HudTextures.Get(l);
        var texScale = HudTextures.ScaleFor(w, h);

        for (var py = 0; py < texH; py++)
        for (var px = 0; px < texW; px++)
        {
            // u, v : position dans la fenêtre, en unités du Canvas (origine en bas à gauche de la fenêtre, py = 0 en bas).
            var u = (px + 0.5f) / Scale - pad;
            var v = (py + 0.5f) / Scale - pad;

            var d = Sdf(u, v, w, h, radius, l.Chamfer);
            var cover = Clamp01(0.5f - d * Scale);

            // Ombre : la même forme décalée, nette ou floue.
            var sd = Sdf(u - shadowX, v - l.ShadowY, w, h, radius, l.Chamfer);
            var shadowCover = l.Shadow.A <= 0 ? 0 : l.ShadowBlur > 0 ? Clamp01(0.5f - sd / (2 * l.ShadowBlur)) : Clamp01(0.5f - sd * Scale);
            var alphaS = shadowCover * l.Shadow.A;

            // Fond (dégradé vertical) et bordure intérieure.
            var t = Clamp01(v / h);
            Color fill = new(l.FillBottom.R + (l.Fill.R - l.FillBottom.R) * t, l.FillBottom.G + (l.Fill.G - l.FillBottom.G) * t,
                             l.FillBottom.B + (l.Fill.B - l.FillBottom.B) * t, l.FillBottom.A + (l.Fill.A - l.FillBottom.A) * t);
            if (tex != null)
            {
                // The real material under the window (canvas units from the top-left, 1 texel per unit), with the bolts of the steel plate.
                var tu = u; var tv = h - v;
                // A small window shows the whole tile shrunk to its size (so the sand of the beach is there too), a big one shows it at full size.
                HudTextures.Sample(tex, tu / texScale, tv / texScale, out var tr, out var tg, out var tb);
                if (l.Tex == "steel" && HudTextures.Bolt(tu, tv, w, h, out var sh) is var ba0 && ba0 > 0) { tr += (sh - tr) * ba0; tg += (sh - tg) * ba0; tb += (sh - tb) * ba0; }
                fill = new Color(tr, tg, tb, fill.A);
            }
            var borderMix = l.BorderWidth > 0 ? Clamp01(0.5f + (d + l.BorderWidth) * Scale) : 0;
            var br = fill.R + (l.Border.R - fill.R) * borderMix;
            var bg = fill.G + (l.Border.G - fill.G) * borderMix;
            var bb = fill.B + (l.Border.B - fill.B) * borderMix;
            var ba = fill.A + (l.Border.A - fill.A) * borderMix;
            var alphaB = cover * ba;

            var outA = alphaB + alphaS * (1 - alphaB);
            float r = 0, g = 0, b = 0;
            if (outA > 0.0001f)
            {
                r = (br * alphaB + l.Shadow.R * alphaS * (1 - alphaB)) / outA;
                g = (bg * alphaB + l.Shadow.G * alphaS * (1 - alphaB)) / outA;
                b = (bb * alphaB + l.Shadow.B * alphaS * (1 - alphaB)) / outA;
            }
            var i = (py * texW + px) * 4;
            data[i] = (byte)(Clamp01(r) * 255 + 0.5f);
            data[i + 1] = (byte)(Clamp01(g) * 255 + 0.5f);
            data[i + 2] = (byte)(Clamp01(b) * 255 + 0.5f);
            data[i + 3] = (byte)(Clamp01(outA) * 255 + 0.5f);
        }
        return (data, texW, texH);
    }

    /// <summary>Crée une Texture2D RGBA32 avec ces octets (même technique que l'overlay des GIF).</summary>
    public static nint MakeTexture(byte[] rgba, int width, int height)
    {
        var k = Unity.Class("UnityEngine.Texture2D");
        var tex = k.New();
        int w = width, h = height, format = 4; // RGBA32
        byte mips = 0;
        k.Method(".ctor", "System.Int32", "System.Int32", "UnityEngine.TextureFormat", "System.Boolean")
            .Invoke(tex, (nint)(&w), (nint)(&h), (nint)(&format), (nint)(&mips));
        UnityProperties.Set(tex, "set_wrapMode", 1);   // Clamp
        UnityProperties.Set(tex, "set_filterMode", 1); // Bilinear

        var raw = k.Method("LoadRawTextureData", "System.IntPtr", "System.Int32");
        if (!raw.IsNull)
        {
            fixed (byte* p = rgba)
            {
                var (ptr, len) = ((nint)p, rgba.Length);
                raw.Invoke(tex, (nint)(&ptr), (nint)(&len));
            }
        }
        else
        {
            var bytes = k.Method("LoadRawTextureData", "System.Byte[]");
            if (bytes.IsNull) throw new InvalidOperationException("this game build cannot fill textures");
            var arr = Il2Cpp.NewArray(Il2Cpp.RequireClass("mscorlib.dll", "System.Byte"), rgba.Length);
            rgba.CopyTo(new Span<byte>((void*)Il2Cpp.ArrayData(arr), rgba.Length));
            bytes.Invoke(tex, arr);
        }

        var apply = k.Method("Apply", 0);
        if (!apply.IsNull) apply.Invoke(tex);
        else
        {
            byte m = 0, unreadable = 0;
            k.Method("Apply", 2).Invoke(tex, (nint)(&m), (nint)(&unreadable));
        }
        return tex;
    }
}
