using Jibbers.Sdk;
using Jibbers.Sdk.Interop;

namespace PhoneCamera.Integration;

/// <summary>What the phone on screen shows.</summary>
internal sealed record PhoneView(bool Selfie, bool Centered, bool Portrait, string Who, float Zoom, int? RecSeconds, bool Saved, string? Hint, bool Blink);

/// <summary>
/// The phone on the game screen (a canvas of our own, pixels): the camera's live picture, the
/// phone's body over it (<see cref="PhoneArt"/>), the status bar's text, and the controls hint at the
/// bottom of the screen. In the bottom right corner, or bigger in the middle. Main thread only.
/// </summary>
internal sealed unsafe class PhoneScreen : IDisposable
{
    OwnedGameObject? canvas;
    GameRef? root, feed, body, left, middle, right, hint, bodyTexture;
    (PhoneLayout Layout, bool Recording)? drawn;
    nint font;

    /// <summary>The record button on the screen while the phone shows: centre (pixels from the bottom left) and radius.</summary>
    public (float X, float Y, float R)? RecordButton { get; private set; }

    static int ScreenSize(string getter) => Il2Cpp.Unbox<int>(Unity.CallStatic(Unity.Class("UnityEngine.Screen"), getter));

    nint Font()
    {
        if (font != 0) return font;
        foreach (var t in Unity.FindObjects(Unity.Class("TMPro.TMP_Text")))
        {
            if (Unity.IsNull(t)) continue;
            var f = Unity.Call(t, "get_font");
            if (f != 0 && !Unity.IsNull(f)) return font = f;
        }
        return 0;
    }

    nint Child(string name, string component)
    {
        using var go = OwnedGameObject.Create(name);
        var c = go.AddComponent(component);
        go.AttachTo(root!.Target);
        return c;
    }

    GameRef Text(string name, float size)
    {
        var t = Child(name, "TMPro.TextMeshProUGUI");
        Unity.Call(t, "set_font", Font());
        UnityProperties.Set(t, "set_fontSize", size);
        UnityProperties.Set(t, "set_raycastTarget", false);
        UnityProperties.Set(t, "set_richText", false);
        if (!Il2Cpp.ClassOf(t).Method("set_textWrappingMode", 1).IsNull)
            UnityProperties.Set(t, "set_textWrappingMode", 0);
        else
            UnityProperties.Set(t, "set_enableWordWrapping", false);
        UnityProperties.Set(t, "set_overflowMode", 1); // Ellipsis
        return new GameRef(t);
    }

    bool Build()
    {
        if (canvas is not null && !Unity.IsNull(canvas.Target)) return true;
        Dispose();
        if (Font() == 0) return false; // the game hasn't loaded a font yet
        canvas = OwnedGameObject.Create("Mod_PhoneCamera_Screen");
        Unity.CallStatic(Unity.Class("UnityEngine.Object"), "DontDestroyOnLoad", canvas.Target);
        var c = canvas.AddComponent("UnityEngine.Canvas");
        UnityProperties.Set(c, "set_renderMode", 0); // ScreenSpaceOverlay
        UnityProperties.Set(c, "set_sortingOrder", 509);
        using (var r = OwnedGameObject.Create("Phone"))
        {
            var rt = r.AddComponent("UnityEngine.RectTransform");
            r.AttachTo(canvas.Transform);
            root = new GameRef(rt);
        }
        feed = new GameRef(Child("Screen", "UnityEngine.UI.RawImage"));
        body = new GameRef(Child("Body", "UnityEngine.UI.RawImage"));
        foreach (var raw in new[] { feed, body })
            UnityProperties.Set(raw.Target, "set_raycastTarget", false);
        left = Text("Rec", 15);
        middle = Text("Who", 15);
        right = Text("Zoom", 15);
        // The hint sits under the phone's canvas root but is placed against the screen.
        hint = Text("Hint", 15);
        UnityProperties.Set(left.Target, "set_color", new Color(1, 60 / 255f, 60 / 255f, 1));
        UnityProperties.Set(right.Target, "set_color", new Color(190 / 255f, 190 / 255f, 200 / 255f, 1));
        UnityProperties.Set(hint.Target, "set_color", new Color(190 / 255f, 190 / 255f, 200 / 255f, 1));
        // Hidden until Show has drawn it all (a half-built phone shows as blank white squares).
        UnityProperties.Set(Unity.Call(root.Target, "get_gameObject"), "SetActive", false);
        return true;
    }

    static void Place(nint o, Vector2 anchor, Vector2 pivot, Vector2 at, Vector2 size)
    {
        var tr = Unity.Call(o, "get_transform");
        UnityProperties.Set(tr, "set_anchorMin", anchor);
        UnityProperties.Set(tr, "set_anchorMax", anchor);
        UnityProperties.Set(tr, "set_pivot", pivot);
        UnityProperties.Set(tr, "set_anchoredPosition", at);
        UnityProperties.Set(tr, "set_sizeDelta", size);
    }

    static void SetText(GameRef? t, string text, int alignment)
    {
        if (t is null) return;
        Unity.Call(t.Target, "set_text", Il2Cpp.String(text));
        UnityProperties.Set(t.Target, "set_alignment", alignment);
    }

    static int Align(string name) => Unity.Class("TMPro.TextAlignmentOptions").Field(name).Get<int>(0);

    void UploadBody(PhoneLayout l, bool recording)
    {
        if (drawn == (l, recording) && bodyTexture is not null && !Unity.IsNull(bodyTexture.Target)) return;
        var rgba = PhoneArt.Draw(l, recording);
        // Unity's rows run bottom-up.
        var flipped = new byte[rgba.Length];
        var row = l.W * 4;
        for (var y = 0; y < l.H; y++)
            Buffer.BlockCopy(rgba, y * row, flipped, (l.H - 1 - y) * row, row);
        var tk = Unity.Class("UnityEngine.Texture2D");
        if (bodyTexture is null || Unity.IsNull(bodyTexture.Target) || Unity.Call<int>(bodyTexture.Target, "get_width") != l.W || Unity.Call<int>(bodyTexture.Target, "get_height") != l.H)
        {
            bodyTexture?.Dispose();
            var tex = tk.New();
            int w = l.W, h = l.H, format = 4; // RGBA32
            var mips = (byte)0;
            tk.Method(".ctor", "System.Int32", "System.Int32", "UnityEngine.TextureFormat", "System.Boolean").Invoke(tex, (nint)(&w), (nint)(&h), (nint)(&format), (nint)(&mips));
            bodyTexture = new GameRef(tex);
            Unity.Call(body!.Target, "set_texture", tex);
        }
        Fill(tk, bodyTexture.Target, flipped);
        Unity.Call(bodyTexture.Target, "Apply");
        drawn = (l, recording);
    }

    /// <summary>Puts RGBA bytes into a Texture2D with whichever method this game build kept.</summary>
    static void Fill(Il2CppClass tk, nint tex, byte[] rgba)
    {
        var raw = tk.Method("LoadRawTextureData", "System.IntPtr", "System.Int32");
        if (!raw.IsNull)
        {
            fixed (byte* p = rgba)
            {
                nint ptr = (nint)p;
                var len = rgba.Length;
                raw.Invoke(tex, (nint)(&ptr), (nint)(&len));
            }
            return;
        }
        var bytes = tk.Method("LoadRawTextureData", "System.Byte[]");
        var colors = tk.Method("SetPixels32", "UnityEngine.Color32[]");
        var (method, element) = !bytes.IsNull ? (bytes, Il2Cpp.RequireClass("mscorlib.dll", "System.Byte")) : !colors.IsNull ? (colors, Unity.Class("UnityEngine.Color32")) : default;
        if (method.IsNull) throw new InvalidOperationException("no way to fill a texture in this game build");
        var array = Il2CppApi.array_new(element.Ptr, (nuint)(method == bytes ? rgba.Length : rgba.Length / 4));
        fixed (byte* p = rgba)
            Buffer.MemoryCopy(p, (void*)Il2Cpp.ArrayData(array), rgba.Length, rgba.Length);
        method.Invoke(tex, array);
    }

    public void Show(PhoneView v, nint picture)
    {
        if (!Build()) return;
        var l = PhoneLayout.For(v.Portrait);
        UploadBody(l, v.RecSeconds is not null);
        Unity.Call(feed!.Target, "set_texture", picture);

        // Sized for the screen's height (drawn for 1080p): bottom right above the hint, or bigger in the middle.
        float sw = ScreenSize("get_width"), sh = ScreenSize("get_height");
        var s = Math.Clamp(sh / 1080f, 0.5f, 2.5f) * (v.Centered ? 1.3f : 1f);
        float w = l.W * s, h = l.H * s;
        float leftX, bottom;
        if (v.Centered)
        {
            Place(root!.Target, new(0.5f, 0.5f), new(0.5f, 0.5f), new(0, 0), new(w, h));
            (leftX, bottom) = ((sw - w) / 2, (sh - h) / 2);
        }
        else
        {
            float margin = 32 * s, lift = 80 * s;
            Place(root!.Target, new(1, 0), new(1, 0), new(-margin, lift), new(w, h));
            (leftX, bottom) = (sw - margin - w, lift);
        }
        // Children in the phone's pixels (y down in the art, up in Unity).
        Vector2 At(float x, float y, float height) => new(x * s, (l.H - y - height) * s);
        Place(feed.Target, new(0, 0), new(0, 0), At(l.ScreenX, l.ScreenY, l.ScreenH), new(l.ScreenW * s, l.ScreenH * s));
        Place(body!.Target, new(0, 0), new(0, 0), new(0, 0), new(w, h));
        var barY = l.ScreenY + 8;
        var recW = v.RecSeconds is null ? 0 : 110f;
        Place(left!.Target, new(0, 0), new(0, 0), At(l.ScreenX + 22, barY, 30), new(recW * s, 30 * s));
        Place(middle!.Target, new(0, 0), new(0, 0), At(l.ScreenX + 22 + recW, barY, 30), new((l.ScreenW - 44 - recW - 60) * s, 30 * s));
        Place(right!.Target, new(0, 0), new(0, 0), At(l.ScreenX + l.ScreenW - 80, barY, 30), new(60 * s, 30 * s));
        foreach (var t in new[] { left, middle, right })
            UnityProperties.Set(t.Target, "set_fontSize", 15 * s);
        var rec = v.RecSeconds is { } secs ? $"{(v.Blink ? "● " : "  ")}REC {secs / 60:00}:{secs % 60:00}" : "";
        SetText(left, rec, Align("Left"));
        SetText(middle, v.Saved ? "Saved to Videos › Jibbers Clips" : v.Who, Align("Left"));
        SetText(right, $"{v.Zoom:0.0}x", Align("Right"));

        // The hint, along the bottom of the screen (parented to the phone, so placed back against the screen).
        if (v.Hint is { } text)
        {
            Place(hint!.Target, new(0, 0), new(0.5f, 0), new(sw / 2 - leftX, 24 * s - bottom), new(sw * 0.95f, 30 * s));
            UnityProperties.Set(hint.Target, "set_fontSize", 15 * s);
            SetText(hint, text, Align("Center"));
            UnityProperties.Set(hint.Target, "set_enabled", true);
        }
        else
            UnityProperties.Set(hint!.Target, "set_enabled", false);

        UnityProperties.Set(Unity.Call(root.Target, "get_gameObject"), "SetActive", true);
        RecordButton = (leftX + l.ButtonX * s, bottom + (l.H - l.ButtonY) * s, l.ButtonR * s);
    }

    public void Hide()
    {
        RecordButton = null;
        if (root is not null && !Unity.IsNull(root.Target))
            UnityProperties.Set(Unity.Call(root.Target, "get_gameObject"), "SetActive", false);
    }

    public void Dispose()
    {
        RecordButton = null;
        drawn = null;
        Cleanup.Run(() => canvas?.Dispose(), () => bodyTexture?.Dispose(), () => root?.Dispose(), () => feed?.Dispose(), () => body?.Dispose(),
            () => left?.Dispose(), () => middle?.Dispose(), () => right?.Dispose(), () => hint?.Dispose());
        canvas = null;
        root = feed = body = left = middle = right = hint = bodyTexture = null;
        font = 0;
    }
}
