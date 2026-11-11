using System.Runtime.InteropServices;
using Jibbers.Sdk;
using Jibbers.Sdk.Interop;

namespace RickBackflip;

[StructLayout(LayoutKind.Sequential)]
internal record struct Color32(byte R, byte G, byte B, byte A);

/// <summary>TMPro.VertexGradient : quatre couleurs (haut gauche, haut droit, bas gauche, bas droit).</summary>
[StructLayout(LayoutKind.Sequential)]
internal record struct VertexGradient(Color TopLeft, Color TopRight, Color BottomLeft, Color BottomRight);

/// <summary>
/// Le nom du trick écrit avec la police du jeu (TextMeshPro), en dégradé : plus le trick est impressionnant,
/// plus les couleurs sont chaudes et vives, plus la lueur est forte (et elle pulse). Animation « splash » de 1,5 s.
/// À n'appeler que depuis le thread principal du jeu.
/// </summary>
internal sealed unsafe class TextBanner : IDisposable
{
    public const int DurationMs = 1500;

    OwnedGameObject? root;
    GameRef? text;
    GameRef? rect;
    GameRef? material;
    double intensity;
    float fit = 1, angle, cx, cy;
    bool glow;
    Color top, bottom;

    /// <summary>La police du jeu : celle de sa liste de tricks si elle existe, sinon celle de n'importe quel texte TextMeshPro.</summary>
    public static nint FindFont()
    {
        var tmp = Unity.Class("TMPro.TMP_Text");
        try
        {
            foreach (var z in Jibbers.Interop.Game.bij.Live())
            {
                var container = z.zyt.Ptr;
                if (container == 0 || Unity.IsNull(container)) continue;
                foreach (var t in Unity.GetComponentsInChildren(container, tmp, true))
                {
                    var f = Unity.Call(t, "get_font");
                    if (f != 0 && !Unity.IsNull(f)) return f;
                }
            }
        }
        catch { /* on prend une autre police du jeu */ }
        foreach (var t in Unity.FindObjects(tmp))
        {
            if (Unity.IsNull(t)) continue;
            var f = Unity.Call(t, "get_font");
            if (f != 0 && !Unity.IsNull(f)) return f;
        }
        return 0;
    }

    static Color Mix(Color a, Color b, double t) =>
        new((float)(a.R + (b.R - a.R) * t), (float)(a.G + (b.G - a.G) * t), (float)(a.B + (b.B - a.B) * t), 1);

    /// <summary>Palette en trois étapes : calme (bleu glace) -> chaud (jaune orangé) -> brûlant (blanc chaud et rouge magenta).</summary>
    static (Color Top, Color Bottom) Palette(double i)
    {
        (Color, Color) calm = (new(0.85f, 0.97f, 1f, 1), new(0.30f, 0.65f, 1f, 1));
        (Color, Color) warm = (new(1f, 0.96f, 0.35f, 1), new(1f, 0.50f, 0.05f, 1));
        (Color, Color) hot = (new(1f, 1f, 0.85f, 1), new(1f, 0.08f, 0.30f, 1));
        return i < 0.5
            ? (Mix(calm.Item1, warm.Item1, i * 2), Mix(calm.Item2, warm.Item2, i * 2))
            : (Mix(warm.Item1, hot.Item1, (i - 0.5) * 2), Mix(warm.Item2, hot.Item2, (i - 0.5) * 2));
    }

    /// <summary>Crée le texte (invisible tant qu'on ne l'a pas placé) et renvoie sa taille en pixels du Canvas.</summary>
    public (float Width, float Height) Prepare(string trickName, double intensity, float sizeScale, nint font)
    {
        Dispose();
        this.intensity = intensity;
        (top, bottom) = Palette(intensity);
        try
        {
            root = OverlayCanvas.BuildRoot("RickBackflip.Splash");
            using var child = OwnedGameObject.Create("Text");
            var tmp = child.AddComponent("TMPro.TextMeshProUGUI");
            text = new GameRef(tmp);
            Unity.Call(tmp, "set_font", font);
            UnityProperties.Set(tmp, "set_raycastTarget", false);
            UnityProperties.Set(tmp, "set_richText", true);
            UnityProperties.Set(tmp, "set_alignment", Unity.Class("TMPro.TextAlignmentOptions").Field("Center").Get<int>(0));
            UnityProperties.Set(tmp, "set_fontSize", (float)((70 + 38 * intensity) * sizeScale)); // les gros tricks sont écrits plus grand
            Try(() => UnityProperties.Set(tmp, "set_enableVertexGradient", (byte)1));
            Try(() => UnityProperties.Set(tmp, "set_color", new Color(1, 1, 1, 1)));
            ApplyGradient(1.0);

            // Le nom exact du jeu, tel quel (« < » neutralisé pour que TextMeshPro ne le lise pas comme une balise).
            var label = new string(trickName.Where(c => !char.IsControl(c)).ToArray()).Replace("<", "\u2039");
            Unity.Call(tmp, "set_text", Il2Cpp.String($"{label}\n<size=55%>{TrickBanner.Subtitle(trickName)}</size>"));

            rect = new GameRef(Unity.Call(tmp, "get_rectTransform"));
            var zero = new Vector2(0, 0);
            UnityProperties.Set(rect.Target, "set_anchorMin", zero);
            UnityProperties.Set(rect.Target, "set_anchorMax", zero);
            UnityProperties.Set(rect.Target, "set_pivot", new Vector2(0.5f, 0.5f));
            UnityProperties.Set(rect.Target, "set_localScale", new Vector3(0, 0, 0));

            // Mesure : d'abord sur une ligne, puis avec retour à la ligne si c'est trop large.
            var maxW = 1000f * sizeScale;
            SetWrap(false);
            UnityProperties.Set(rect.Target, "set_sizeDelta", new Vector2(4000, 600));
            var pw = Unity.Call<float>(tmp, "get_preferredWidth");
            var w = Math.Min(pw + 40, maxW);
            SetWrap(pw + 40 > maxW);
            UnityProperties.Set(rect.Target, "set_sizeDelta", new Vector2(w, 600));
            var h = Unity.Call<float>(tmp, "get_preferredHeight") + 24;
            UnityProperties.Set(rect.Target, "set_sizeDelta", new Vector2(w, h));

            child.AttachTo(root.Transform);
            SetupMaterial(tmp);
            return (w * 1.15f, h * 1.15f);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    void SetWrap(bool wrap)
    {
        var tmp = text!.Target;
        if (!Il2Cpp.ClassOf(tmp).Method("set_textWrappingMode", 1).IsNull) UnityProperties.Set(tmp, "set_textWrappingMode", wrap ? 1 : 0);
        else Try(() => UnityProperties.Set(tmp, "set_enableWordWrapping", (byte)(wrap ? 1 : 0)));
    }

    static void Try(Action a) { try { a(); } catch (Exception) { /* réglage facultatif : le texte reste lisible sans lui */ } }

    void SetupMaterial(nint tmp)
    {
        // Contour sombre épais + lueur de la couleur du bas. La lueur dépend du shader de la police : sans elle, il reste le contour.
        Try(() => UnityProperties.Set(tmp, "set_outlineWidth", (float)(0.22 + 0.10 * intensity)));
        Try(() => UnityProperties.Set(tmp, "set_outlineColor", new Color32(20, 4, 36, 255)));
        Try(() =>
        {
            material = new GameRef(Unity.Call(tmp, "get_fontMaterial"));
            var m = material.Target;
            SetMat(m, "_GlowColor", new Color(bottom.R, bottom.G, bottom.B, 1));
            SetMat(m, "_GlowOffset", 0f);
            SetMat(m, "_GlowInner", 0.1f);
            SetMat(m, "_GlowOuter", (float)(0.35 + 0.65 * intensity));
            SetMat(m, "_GlowPower", (float)(0.3 + 0.6 * intensity));
            Il2Cpp.ClassOf(m).Method("EnableKeyword", "System.String").Invoke(m, Il2Cpp.String("GLOW_ON"));
            glow = true;
        });
    }

    static void SetMat(nint mat, string name, float v) =>
        Il2Cpp.ClassOf(mat).Method("SetFloat", "System.String", "System.Single").Invoke(mat, Il2Cpp.String(name), (nint)(&v));

    static void SetMat(nint mat, string name, Color c) =>
        Il2Cpp.ClassOf(mat).Method("SetColor", "System.String", "UnityEngine.Color").Invoke(mat, Il2Cpp.String(name), (nint)(&c));

    void ApplyGradient(double brightness)
    {
        Color Br(Color c) => new((float)Math.Min(1, c.R * brightness), (float)Math.Min(1, c.G * brightness), (float)Math.Min(1, c.B * brightness), 1);
        var (t, b) = (Br(top), Br(bottom));
        Try(() => UnityProperties.Set(text!.Target, "set_colorGradient", new VertexGradient(t, t, b, b)));
    }

    /// <summary>Pose le centre du texte (pixels du Canvas), l'inclinaison (degrés) et le facteur de réduction si la place manquait.</summary>
    public void Place(float centerX, float centerY, float degrees, float fit)
        => (cx, cy, angle, this.fit) = (centerX, centerY, degrees, fit);

    /// <summary>Anime le texte ; renvoie faux quand les 1,5 s sont écoulées.</summary>
    public bool Update(long ms)
    {
        if (text is null || rect is null || Unity.IsNull(text.Target) || Unity.IsNull(rect.Target)) return false;
        if (ms >= DurationMs) return false;

        // Rebond d'entrée (0,3 -> 1,2 -> 1), maintien, léger fondu final.
        double pop = ms < 120 ? 0.3 + 0.9 * (ms / 120.0)
            : ms < 260 ? 1.2 - 0.2 * ((ms - 120) / 140.0)
            : 1.0 + 0.02 * Math.Sin(ms / 70.0 * (1 + intensity));
        var fade = ms > DurationMs - 250 ? (DurationMs - ms) / 250.0 : 1.0;
        var s = (float)(pop * fit * (1 + 0.1 * intensity));

        // Tremblement au début pour les gros tricks.
        double shake = ms < 500 ? intensity * intensity * 7 * (1 - ms / 500.0) : 0;
        var dx = (float)(Math.Sin(ms * 0.9) * shake);
        var dy = (float)(Math.Cos(ms * 1.3) * shake);

        UnityProperties.Set(rect.Target, "set_localScale", new Vector3(s, s, 1));
        UnityProperties.Set(rect.Target, "set_anchoredPosition", new Vector2(cx + dx, cy + dy));
        UnityProperties.Set(rect.Target, "set_localEulerAngles", new Vector3(0, 0, angle * (float)Math.Min(1, ms / 150.0)));
        UnityProperties.Set(text.Target, "set_alpha", (float)fade);

        // Surbrillance : les gros tricks pulsent (plus vite et plus fort).
        if (intensity > 0.3)
        {
            var pulse = 1 + 0.18 * intensity * Math.Sin(ms / 1000.0 * (6 + 10 * intensity));
            ApplyGradient(pulse);
            if (glow && material is not null && !Unity.IsNull(material.Target))
                Try(() => SetMat(material.Target, "_GlowPower", (float)((0.3 + 0.6 * intensity) * (0.85 + 0.3 * Math.Sin(ms / 1000.0 * (5 + 12 * intensity))))));
        }
        return true;
    }

    public void Dispose()
    {
        var (r, tx, rc, mat) = (root, text, rect, material);
        (root, text, rect, material) = (null, null, null, null);
        glow = false;
        Cleanup.Run(new Action[]
        {
            () => r?.Dispose(),
            () => tx?.Dispose(),
            () => rc?.Dispose(),
            () => mat?.Dispose(),
        });
    }
}
