using Jibbers.Sdk.Interop;

namespace Jibbers.Sdk.UI;

/// <summary>
/// Unity side of an overlay: its own screen-space Canvas (kept across maps), a RawImage showing the
/// rasterized shapes, and pooled TextMeshPro texts on top. Called only on the main thread.
/// </summary>
internal sealed unsafe class UnityOverlaySurface : IOverlaySurface
{
    OwnedGameObject? root;
    GameRef? image, texture;
    nint font;
    (float W, float H) size;
    byte[]? pixels;
    int pixelWidth, pixelHeight;
    readonly List<(GameRef Text, OverlayText? Applied)> texts = new();

    public void Render(OverlaySnapshot s)
    {
        try
        {
            if (root is null || Unity.IsNull(root.Target) || image is null || Unity.IsNull(image.Target) || texts.Any(t => Unity.IsNull(t.Text.Target)))
                Build(s);
            if (s.Pixels is not null) (pixels, pixelWidth, pixelHeight) = (s.Pixels, s.PixelWidth, s.PixelHeight);
            if (s.Pixels is not null || texture is null || Unity.IsNull(texture.Target)) Upload();
            if (size != (s.Width, s.Height))
            {
                size = (s.Width, s.Height);
                UnityProperties.Set(Unity.Call(image!.Target, "get_rectTransform"), "set_sizeDelta", new Vector2(s.Width, s.Height));
            }
            for (var i = 0; i < Math.Max(s.Texts.Length, texts.Count); i++)
            {
                if (i == texts.Count) texts.Add((CreateText(), null));
                var want = i < s.Texts.Length ? s.Texts[i] : null;
                var (text, applied) = texts[i];
                if (want == applied) continue;
                if (want is null) Unity.Call(text.Target, "set_text", Il2Cpp.String(""));
                else Apply(text.Target, want, applied);
                texts[i] = (text, want);
            }
            UnityProperties.Set(root!.Target, "SetActive", true);
        }
        catch
        {
            // No half-built overlay survives a failure; the next frame rebuilds it.
            Dispose();
            throw;
        }
    }

    void Build(OverlaySnapshot s)
    {
        Dispose();
        font = UiFont.Get();
        if (font == 0) throw new InvalidOperationException("waiting for the game to load a font");
        root = OwnedGameObject.Create("Jibbers.Overlay." + s.Name);
        var canvas = root.AddComponent("UnityEngine.Canvas");
        UnityProperties.Set(canvas, "set_renderMode", 0); // ScreenSpaceOverlay
        UnityProperties.Set(canvas, "set_sortingOrder", s.Options.SortingOrder);
        var scaler = root.AddComponent("UnityEngine.UI.CanvasScaler");
        UnityProperties.Set(scaler, "set_uiScaleMode", 1); // ScaleWithScreenSize
        UnityProperties.Set(scaler, "set_referenceResolution", s.Options.ReferenceResolution);
        UnityProperties.Set(scaler, "set_matchWidthOrHeight", 0.5f);
        try { Unity.Class("UnityEngine.Object").Method("DontDestroyOnLoad", 1).Invoke(0, root.Target); }
        catch (Exception e) { Log.Info("overlay", $"{s.Name}: stays with this map only ({e.Message})"); }

        using var child = OwnedGameObject.Create("Background");
        var raw = child.AddComponent("UnityEngine.UI.RawImage");
        UnityProperties.Set(raw, "set_raycastTarget", false);
        var rect = Unity.Call(raw, "get_rectTransform");
        var anchor = HudValidation.Anchor(s.Options.Anchor);
        UnityProperties.Set(rect, "set_anchorMin", anchor);
        UnityProperties.Set(rect, "set_anchorMax", anchor);
        UnityProperties.Set(rect, "set_pivot", anchor);
        UnityProperties.Set(rect, "set_anchoredPosition", s.Options.Offset);
        UnityProperties.Set(rect, "set_sizeDelta", new Vector2(s.Width, s.Height));
        size = (s.Width, s.Height);
        image = new GameRef(raw);
        child.AttachTo(root.Transform);
    }

    void Upload()
    {
        if (pixels is null) return;
        var k = Unity.Class("UnityEngine.Texture2D");
        if (texture is null || Unity.IsNull(texture.Target) || Unity.Call<int>(texture.Target, "get_width") != pixelWidth || Unity.Call<int>(texture.Target, "get_height") != pixelHeight)
        {
            var old = texture;
            var tex = k.New();
            int w = pixelWidth, h = pixelHeight, format = 4; // RGBA32
            byte mips = 0;
            k.Method(".ctor", "System.Int32", "System.Int32", "UnityEngine.TextureFormat", "System.Boolean").Invoke(tex, (nint)(&w), (nint)(&h), (nint)(&format), (nint)(&mips));
            UnityProperties.Set(tex, "set_wrapMode", 1);   // Clamp: no bleeding at the edges
            UnityProperties.Set(tex, "set_filterMode", 1); // Bilinear
            texture = new GameRef(tex);
            Unity.Call(image!.Target, "set_texture", tex);
            if (old is not null)
            {
                if (!Unity.IsNull(old.Target)) Unity.Destroy(old.Target);
                old.Dispose();
            }
        }
        Fill(k, texture.Target, pixels);
        var apply = k.Method("Apply", 0);
        if (!apply.IsNull) apply.Invoke(texture.Target);
        else
        {
            byte mips = 0, unreadable = 0;
            k.Method("Apply", 2).Invoke(texture.Target, (nint)(&mips), (nint)(&unreadable));
        }
    }

    /// <summary>Copies RGBA bytes into the texture with whichever method this game build kept.</summary>
    static void Fill(Il2CppClass k, nint tex, byte[] rgba)
    {
        var raw = k.Method("LoadRawTextureData", "System.IntPtr", "System.Int32");
        if (!raw.IsNull)
        {
            fixed (byte* p = rgba)
            {
                var (ptr, len) = ((nint)p, rgba.Length);
                raw.Invoke(tex, (nint)(&ptr), (nint)(&len));
            }
            return;
        }
        var bytes = k.Method("LoadRawTextureData", "System.Byte[]");
        if (bytes.IsNull) throw new InvalidOperationException("this game build cannot fill textures");
        var arr = Il2Cpp.NewArray(Il2Cpp.RequireClass("mscorlib.dll", "System.Byte"), rgba.Length);
        rgba.CopyTo(new Span<byte>((void*)Il2Cpp.ArrayData(arr), rgba.Length));
        bytes.Invoke(tex, arr);
    }

    GameRef CreateText()
    {
        using var child = OwnedGameObject.Create("Text");
        var text = child.AddComponent("TMPro.TextMeshProUGUI");
        Unity.Call(text, "set_font", font);
        UnityProperties.Set(text, "set_raycastTarget", false);
        UnityProperties.Set(text, "set_richText", false); // player names are shown as written
        var rect = Unity.Call(text, "get_rectTransform");
        var topLeft = new Vector2(0, 1);
        UnityProperties.Set(rect, "set_anchorMin", topLeft);
        UnityProperties.Set(rect, "set_anchorMax", topLeft);
        UnityProperties.Set(rect, "set_pivot", topLeft);
        UnityProperties.Set(rect, "set_localScale", new Vector3(1, 1, 1));
        TrySet(text, "set_overflowMode", 1); // Ellipsis
        var reference = new GameRef(text);
        try { child.AttachTo(Unity.Transform(image!.Target)); return reference; }
        catch { reference.Dispose(); throw; }
    }

    static void Apply(nint text, OverlayText t, OverlayText? old)
    {
        if (old?.Text != t.Text) Unity.Call(text, "set_text", Il2Cpp.String(t.Text));
        if (old?.Size != t.Size) UnityProperties.Set(text, "set_fontSize", t.Size);
        if (old?.Color != t.Color)
        {
            UnityProperties.Set(text, "set_color", t.Color);
            UnityProperties.Set(text, "set_alpha", t.Color.A);
        }
        if (old?.Align != t.Align) UnityProperties.Set(text, "set_alignment", Unity.Class("TMPro.TextAlignmentOptions").Field(t.Align.ToString()).Get<int>(0));
        if (old?.Bold != t.Bold) TrySet(text, "set_fontStyle", t.Bold ? 1 : 0);
        if (old?.Spacing != t.Spacing) TrySet(text, "set_characterSpacing", t.Spacing);
        if (old?.Wrap != t.Wrap)
        {
            if (!Il2Cpp.ClassOf(text).Method("set_textWrappingMode", 1).IsNull) UnityProperties.Set(text, "set_textWrappingMode", t.Wrap ? 1 : 0);
            else TrySet(text, "set_enableWordWrapping", t.Wrap);
        }
        if (old is null || (old.X, old.Y, old.W, old.H, old.Size, old.Align) != (t.X, t.Y, t.W, t.H, t.Size, t.Align))
        {
            var (y, h) = LineBox(t);
            var rect = Unity.Call(text, "get_rectTransform");
            UnityProperties.Set(rect, "set_anchoredPosition", new Vector2(t.X, -y));
            UnityProperties.Set(rect, "set_sizeDelta", new Vector2(t.W, h));
        }
    }

    /// <summary>A line's height relative to its size, with room for Segoe UI's (about 1.33).</summary>
    internal const float MinLineHeight = 1.4f;

    /// <summary>
    /// The box a text is laid out in: at least one line tall. Text is clipped with an ellipsis, and a
    /// box shorter than one line of the font shows nothing at all, so a short box grows around the
    /// place the text sits in it (its vertical alignment): (top, height).
    /// </summary>
    internal static (float Y, float H) LineBox(OverlayText t)
    {
        var min = t.Size * MinLineHeight;
        if (t.H >= min) return (t.Y, t.H);
        var extra = min - t.H;
        var y = t.Align switch
        {
            HudTextAlignment.TopLeft or HudTextAlignment.Top or HudTextAlignment.TopRight => t.Y,
            HudTextAlignment.BottomLeft or HudTextAlignment.Bottom or HudTextAlignment.BottomRight => t.Y - extra,
            _ => t.Y - extra / 2,
        };
        return (y, min);
    }

    /// <summary>Optional look: a build that stripped the setter shows plain text instead.</summary>
    static void TrySet<T>(nint target, string setter, T value) where T : unmanaged
    {
        try { UnityProperties.Set(target, setter, value); }
        catch (MissingMethodException) { }
    }

    public void Hide()
    {
        if (root is not null && !Unity.IsNull(root.Target)) UnityProperties.Set(root.Target, "SetActive", false);
    }

    public void Dispose()
    {
        var (ownedRoot, ownedImage, ownedTexture) = (root, image, texture);
        var ownedTexts = texts.Select(t => t.Text).ToArray();
        (root, image, texture, font, size) = (null, null, null, 0, default);
        texts.Clear();
        Cleanup.Run(new Action[]
        {
            () => ownedRoot?.Dispose(),
            () => { if (ownedTexture is not null && !Unity.IsNull(ownedTexture.Target)) Unity.Destroy(ownedTexture.Target); ownedTexture?.Dispose(); },
            () => ownedImage?.Dispose(),
        }.Concat(ownedTexts.Select(t => (Action)t.Dispose)).ToArray());
    }
}
