using Jibbers.Sdk;
using Jibbers.Sdk.Interop;

namespace Spotify.Integration;

/// <summary>Small helpers for building Unity UI on a canvas of our own (in screen pixels). Main thread only.</summary>
internal static unsafe class Ui
{
    static GameRef? font;
    static readonly Dictionary<nint, string> texts = new();

    public static int Screen(string getter) => Il2Cpp.Unbox<int>(Unity.CallStatic(Unity.Class("UnityEngine.Screen"), getter));

    /// <summary>The mouse in Unity's screen space (pixels from the bottom left).</summary>
    public static Vector3? Mouse()
    {
        var position = Il2Cpp.RequireClass("UnityEngine.InputLegacyModule.dll", "UnityEngine.Input").Method("get_mousePosition", 0);
        return position.IsNull ? null : position.Invoke<Vector3>(0);
    }

    /// <summary>A font the game has loaded, or 0 before it has any.</summary>
    public static nint Font()
    {
        if (font is not null && !Unity.IsNull(font.Target)) return font.Target;
        foreach (var t in Unity.FindObjects(Unity.Class("TMPro.TMP_Text")))
        {
            if (Unity.IsNull(t)) continue;
            var f = Unity.Call(t, "get_font");
            if (f == 0 || Unity.IsNull(f)) continue;
            font?.Dispose();
            font = new GameRef(f);
            return f;
        }
        return 0;
    }

    /// <summary>A screen-space overlay canvas kept across maps, and a root rectangle under it.</summary>
    public static (OwnedGameObject Canvas, GameRef Root) Canvas(string name, int order)
    {
        var canvas = OwnedGameObject.Create(name);
        Unity.CallStatic(Unity.Class("UnityEngine.Object"), "DontDestroyOnLoad", canvas.Target);
        var c = canvas.AddComponent("UnityEngine.Canvas");
        UnityProperties.Set(c, "set_renderMode", 0); // ScreenSpaceOverlay
        UnityProperties.Set(c, "set_sortingOrder", order);
        using var r = OwnedGameObject.Create("Root");
        var rt = r.AddComponent("UnityEngine.RectTransform");
        r.AttachTo(canvas.Transform);
        return (canvas, new GameRef(rt));
    }

    /// <summary>A new child with <paramref name="component"/> under <paramref name="parent"/> (a Transform: anything else crashes Unity).</summary>
    static nint Child(nint parent, string name, string component)
    {
        if (!Unity.Class("UnityEngine.Transform").IsAssignableFrom(Il2Cpp.ClassOf(parent)))
            throw new InvalidOperationException($"{name}'s parent isn't a Transform");
        using var go = OwnedGameObject.Create(name);
        var c = go.AddComponent(component);
        go.AttachTo(parent);
        return c;
    }

    /// <summary>An image (white until given a texture or a colour).</summary>
    public static GameRef Raw(nint parent, string name, Color? colour = null)
    {
        var r = Child(parent, name, "UnityEngine.UI.RawImage");
        UnityProperties.Set(r, "set_raycastTarget", false);
        if (colour is { } c) UnityProperties.Set(r, "set_color", c);
        return new GameRef(r);
    }

    /// <summary>One line of text (cut with "..." when too long).</summary>
    public static GameRef Text(nint parent, string name, Color colour, bool bold = false, string align = "Left")
    {
        var t = Child(parent, name, "TMPro.TextMeshProUGUI");
        Unity.Call(t, "set_font", Font());
        UnityProperties.Set(t, "set_raycastTarget", false);
        UnityProperties.Set(t, "set_richText", false);
        UnityProperties.Set(t, "set_color", colour);
        if (!Il2Cpp.ClassOf(t).Method("set_textWrappingMode", 1).IsNull) UnityProperties.Set(t, "set_textWrappingMode", 0);
        else UnityProperties.Set(t, "set_enableWordWrapping", false);
        UnityProperties.Set(t, "set_overflowMode", 1); // Ellipsis
        if (bold) UnityProperties.Set(t, "set_fontStyle", 1);
        UnityProperties.Set(t, "set_alignment", Unity.Class("TMPro.TextAlignmentOptions").Field(align).Get<int>(0));
        return new GameRef(t);
    }

    public static void Place(nint o, Vector2 anchor, Vector2 pivot, Vector2 at, Vector2 size)
    {
        var tr = Unity.Call(o, "get_transform");
        UnityProperties.Set(tr, "set_anchorMin", anchor);
        UnityProperties.Set(tr, "set_anchorMax", anchor);
        UnityProperties.Set(tr, "set_pivot", pivot);
        UnityProperties.Set(tr, "set_anchoredPosition", at);
        UnityProperties.Set(tr, "set_sizeDelta", size);
    }

    /// <summary>Sets a text's words (only when they change) and size.</summary>
    public static void SetText(GameRef t, string text, float size)
    {
        UnityProperties.Set(t.Target, "set_fontSize", size);
        if (texts.TryGetValue(t.Target, out var old) && old == text) return;
        texts[t.Target] = text;
        Unity.Call(t.Target, "set_text", Il2Cpp.String(text));
    }

    public static void Colour(GameRef o, Color c) => UnityProperties.Set(o.Target, "set_color", c);

    /// <summary>Shows or hides one image or text.</summary>
    public static void Visible(GameRef o, bool on) => UnityProperties.Set(o.Target, "set_enabled", on);

    /// <summary>Shows or hides a rectangle and everything under it.</summary>
    public static void Active(GameRef root, bool on) => UnityProperties.Set(Unity.Call(root.Target, "get_gameObject"), "SetActive", on);

    /// <summary>Forgets cached text (the objects were destroyed).</summary>
    public static void Forget(IEnumerable<GameRef?> gone)
    {
        foreach (var g in gone)
            if (g is not null) texts.Remove(g.Target);
    }
}
