using Jibbers.Sdk;
using Jibbers.Sdk.Interop;

namespace Spotify.Integration;

/// <summary>How the corner record looks (from the settings).</summary>
internal readonly record struct RecordLook(string Corner, float Size, bool RecordOnly, float Opacity, bool ArtColour, float Angle, float Pop);

/// <summary>
/// The spinning record in a corner of the game screen: the vinyl with the album art as its label
/// (turned on the GPU), the title, artist, time and a progress bar in the art's colour, and back /
/// play-pause / skip buttons. Or the record alone. A canvas of our own, in pixels. Main thread only.
/// </summary>
internal sealed unsafe class RecordHud : IDisposable
{
    static readonly Color Green = new(30 / 255f, 215 / 255f, 96 / 255f, 1);
    OwnedGameObject? canvas;
    GameRef? root, panel, vinyl, label, title, artist, time, barBack, barFill, group;
    GameRef? panelTex, vinylTex;
    (bool Playing, float Scale)? panelDrawn;
    float vinylScale;

    /// <summary>The buttons on screen (pixels from the bottom left: centre, half-width, half-height) while the full record shows.</summary>
    public List<(Command Command, float X, float Y, float HalfW, float HalfH)> Buttons { get; } = new();

    bool Build()
    {
        if (canvas is not null && !Unity.IsNull(canvas.Target)) return true;
        Dispose();
        if (Ui.Font() == 0) return false;
        (canvas, root) = Ui.Canvas("Mod_Spotify_Record", 505);
        var cg = Il2Cpp.ClassOf(Unity.Call(root.Target, "get_gameObject")).Method("AddComponent", "System.Type")
            .Invoke(Unity.Call(root.Target, "get_gameObject"), Unity.Class("UnityEngine.CanvasGroup").SystemType);
        if (cg != 0) group = new GameRef(cg);
        panel = Ui.Raw(root.Target, "Panel");
        vinyl = Ui.Raw(root.Target, "Vinyl");
        label = Ui.Raw(Unity.Transform(vinyl.Target), "Label"); // under the vinyl's transform, so it turns with it
        barBack = Ui.Raw(root.Target, "BarBack", new Color(80 / 255f, 80 / 255f, 84 / 255f, 1));
        barFill = Ui.Raw(root.Target, "BarFill", Green);
        title = Ui.Text(root.Target, "Title", new Color(1, 1, 1, 1), bold: true);
        artist = Ui.Text(root.Target, "Artist", new Color(179 / 255f, 179 / 255f, 179 / 255f, 1));
        time = Ui.Text(root.Target, "Time", new Color(150 / 255f, 150 / 255f, 155 / 255f, 1), align: "Right");
        Ui.Active(root, false);
        return true;
    }

    static void Swap(ref GameRef? held, nint tex, nint image)
    {
        if (held is not null && !Unity.IsNull(held.Target)) Unity.Destroy(held.Target);
        held?.Dispose();
        held = new GameRef(tex);
        Unity.Call(image, "set_texture", tex);
    }

    /// <summary>Shows the record for <paramref name="np"/>, or hides it (null).</summary>
    public void Show(NowPlaying? np, AlbumArt art, RecordLook look)
    {
        if (np is null)
        {
            Buttons.Clear();
            if (root is not null && !Unity.IsNull(root.Target)) Ui.Active(root, false);
            return;
        }
        if (!Build()) return;
        float sh = Ui.Screen("get_height"), sw = Ui.Screen("get_width");
        var s = Math.Clamp(look.Size / 100f, 0.5f, 2f) * Math.Clamp(sh / 1080f, 0.5f, 2.5f);
        float h = RecordArt.Height * s, margin = 20 * s;
        var w = (look.RecordOnly ? RecordArt.RecordX * 2 : RecordArt.Width) * s;
        if (!look.RecordOnly && panelDrawn != (np.Playing, s))
        {
            Swap(ref panelTex, Textures.From(RecordArt.Panel(np.Playing, s)), panel!.Target);
            panelDrawn = (np.Playing, s);
        }
        var vinylPx = (int)MathF.Round(RecordArt.RecordR * 2 * s);
        if (vinylScale != s)
        {
            Swap(ref vinylTex, Textures.From(RecordArt.Vinyl(vinylPx)), vinyl!.Target);
            vinylScale = s;
        }
        Unity.Call(label!.Target, "set_texture", art.Round);

        var right = look.Corner.EndsWith("right", StringComparison.OrdinalIgnoreCase);
        var top = look.Corner.StartsWith("Top", StringComparison.OrdinalIgnoreCase);
        var anchor = new Vector2(right ? 1 : 0, top ? 1 : 0);
        Ui.Place(root!.Target, anchor, anchor, new(right ? -margin : margin, top ? -margin : margin), new(w, h));
        // A little pop when the song changes.
        var scale = new Vector3(1 + look.Pop, 1 + look.Pop, 1);
        UnityProperties.Set(Unity.Call(root.Target, "get_transform"), "set_localScale", scale);
        if (group is not null) UnityProperties.Set(group.Target, "set_alpha", Math.Clamp(look.Opacity, 0.1f, 1f));

        // Children in record pixels (y down in the art, up in Unity).
        Vector2 At(float x, float y, float height) => new(x * s, h - (y + height) * s);
        Ui.Place(vinyl!.Target, new(0, 0), new(0.5f, 0.5f), new(RecordArt.RecordX * s, h - RecordArt.RecordY * s), new(vinylPx, vinylPx));
        var labelPx = vinylPx * RecordArt.LabelOf;
        Ui.Place(label.Target, new(0.5f, 0.5f), new(0.5f, 0.5f), new(0, 0), new(labelPx, labelPx));
        var turn = new Quaternion(0, 0, MathF.Sin(-look.Angle / 2), MathF.Cos(-look.Angle / 2));
        Unity.Call(Unity.Call(vinyl.Target, "get_transform"), "set_localRotation", (nint)(&turn));

        var full = !look.RecordOnly;
        foreach (var part in new[] { panel!, title!, artist!, time!, barBack!, barFill! }) Ui.Visible(part, full);
        Buttons.Clear();
        if (full)
        {
            Ui.Place(panel!.Target, new(0, 0), new(0, 0), new(0, 0), new(w, h));
            var textW = (RecordArt.Width - RecordArt.TextX - 14) * s;
            Ui.Place(title!.Target, new(0, 0), new(0, 0), At(RecordArt.TextX, 24, 26), new(textW, 26 * s));
            Ui.Place(artist!.Target, new(0, 0), new(0, 0), At(RecordArt.TextX, 48, 22), new(textW, 22 * s));
            Ui.Place(time!.Target, new(0, 0), new(0, 0), At(RecordArt.TextX + 110, 70, 20), new(textW - 114 * s, 20 * s));
            Ui.SetText(title, np.Title, 17 * s);
            Ui.SetText(artist, np.Artist, 13.5f * s);
            Ui.SetText(time, np.Duration > 0 ? $"{RecordArt.Clock(np.PositionNow)} / {RecordArt.Clock(np.Duration)}" : "", 11.5f * s);
            var barW = (RecordArt.Width - RecordArt.TextX - 18) * s;
            var fraction = np.Duration > 0 ? (float)Math.Clamp(np.PositionNow / np.Duration, 0, 1) : 0;
            Ui.Place(barBack!.Target, new(0, 0), new(0, 0), At(RecordArt.TextX, RecordArt.BarY, 3), new(np.Duration > 0 ? barW : 0, 3 * s));
            Ui.Place(barFill!.Target, new(0, 0), new(0, 0), At(RecordArt.TextX, RecordArt.BarY, 3), new(barW * fraction, 3 * s));
            Ui.Colour(barFill, look.ArtColour ? art.Accent : Green);
            float left = right ? sw - margin - w : margin, bottom = top ? sh - margin - h : margin;
            foreach (var (c, x, y, hw, hh) in RecordArt.Buttons())
                Buttons.Add((c, left + x * s, bottom + h - y * s, hw * s, hh * s));
        }
        Ui.Active(root, true);
    }

    public void Dispose()
    {
        Buttons.Clear();
        panelDrawn = null;
        vinylScale = 0;
        Ui.Forget(new[] { title, artist, time });
        foreach (var t in new[] { panelTex, vinylTex })
            if (t is not null && !Unity.IsNull(t.Target)) Unity.Destroy(t.Target);
        Cleanup.Run(() => canvas?.Dispose(), () => root?.Dispose(), () => panel?.Dispose(), () => vinyl?.Dispose(), () => label?.Dispose(),
            () => title?.Dispose(), () => artist?.Dispose(), () => time?.Dispose(), () => barBack?.Dispose(), () => barFill?.Dispose(),
            () => group?.Dispose(), () => panelTex?.Dispose(), () => vinylTex?.Dispose());
        canvas = null;
        root = panel = vinyl = label = title = artist = time = barBack = barFill = group = panelTex = vinylTex = null;
    }
}
