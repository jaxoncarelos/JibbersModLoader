using static Spotify.Pixels;

namespace Spotify;

/// <summary>What the player can be told to do.</summary>
internal enum Command
{
    Previous, PlayPause, Next,
    /// <summary>Jump by a number of seconds / to a second of the song.</summary>
    SeekBy, SeekTo,
    /// <summary>Shuffle on/off; repeat off, playlist, song.</summary>
    Shuffle, Repeat,
}

/// <summary>
/// The corner record's pictures, at 1.0 scale 440 x 120 pixels: the panel behind the text with the
/// back / play-pause / skip buttons, and the vinyl (grooves and a sheen, turned as a whole so it
/// reads as spinning even with plain album art). Plus where the buttons are, for clicks. No game calls.
/// </summary>
internal static class RecordArt
{
    public const int Width = 440, Height = 120;
    /// <summary>The record: centre and radius (at 1.0 scale); the album art label is 0.46 of it.</summary>
    public const float RecordX = 60, RecordY = 60, RecordR = 56, LabelOf = 0.46f;
    /// <summary>Where the title and artist start, and the progress bar.</summary>
    public const float TextX = 128, BarY = 96;

    static readonly Rgba Green = Rgba.Of(30, 215, 96), Light = Rgba.Of(235, 235, 235);

    /// <summary>The buttons (centre x, y; click box half-width, half-height), in record pixels at 1.0 scale.</summary>
    public static IEnumerable<(Command Command, float X, float Y, float HalfW, float HalfH)> Buttons()
    {
        var i = 0;
        foreach (var c in new[] { Command.Previous, Command.PlayPause, Command.Next })
            yield return (c, TextX + 10 + i++ * 34, 80, 15, 12);
    }

    /// <summary>The panel and buttons.</summary>
    public static Pixels Panel(bool playing, float scale)
    {
        var p = new Pixels((int)MathF.Round(Width * scale), (int)MathF.Round(Height * scale));
        float s = scale;
        p.RoundRect(52 * s, 16 * s, p.W - 54 * s, p.H - 30 * s, 16 * s, Rgba.Of(18, 18, 20, 225));
        foreach (var (c, x, y, _, _) in Buttons())
            Icon(p, c, playing, x * s, y * s, 6 * s, Light);
        return p;
    }

    /// <summary>One control's icon on its own, <paramref name="size"/> pixels square.</summary>
    public static Pixels IconPicture(Command c, bool playing, int size, bool dark)
    {
        var p = new Pixels(size, size);
        Icon(p, c, playing, size / 2f, size / 2f, size * 0.3f, dark ? Rgba.Of(18, 18, 18) : Light);
        return p;
    }

    /// <summary>The phone app's background: a dark rounded phone with a border, and the white disc behind the play button.</summary>
    public static Pixels AppBackground(int w, int h, float scale, float playX, float playY, float playR)
    {
        var p = new Pixels(w, h);
        p.RoundRect(0, 0, w, h, 30 * scale, Rgba.Of(60, 60, 66));
        p.RoundRect(2 * scale, 2 * scale, w - 4 * scale, h - 4 * scale, 28 * scale, Rgba.Of(18, 18, 18));
        p.Circle(playX * scale, playY * scale, playR * scale, Rgba.Of(255, 255, 255));
        return p;
    }

    static void Icon(Pixels p, Command c, bool playing, float cx, float cy, float s, Rgba colour)
    {
        switch (c)
        {
            case Command.PlayPause when playing:
                p.RoundRect(cx - s * 0.75f, cy - s, s * 0.5f, s * 2, 0, colour);
                p.RoundRect(cx + s * 0.25f, cy - s, s * 0.5f, s * 2, 0, colour);
                break;
            case Command.PlayPause:
                p.Triangle(cx - s * 0.5f, cy - s, cx + s * 0.7f, cy, cx - s * 0.5f, cy + s, colour);
                break;
            case Command.Next:
                p.Triangle(cx - s * 0.9f, cy - s, cx + s * 0.3f, cy, cx - s * 0.9f, cy + s, colour);
                p.RoundRect(cx + s * 0.4f, cy - s, s * 0.35f, s * 2, 0, colour);
                break;
            case Command.Previous:
                p.Triangle(cx + s * 0.9f, cy - s, cx - s * 0.3f, cy, cx + s * 0.9f, cy + s, colour);
                p.RoundRect(cx - s * 0.75f, cy - s, s * 0.35f, s * 2, 0, colour);
                break;
        }
    }

    /// <summary>The vinyl, <paramref name="size"/> pixels square: black, grooves, a sheen, the spindle. The label (album art) goes on top.</summary>
    public static Pixels Vinyl(int size)
    {
        var p = new Pixels(size, size);
        float c = size / 2f, r = size / 2f - 1;
        p.Circle(c, c, r, Rgba.Of(14, 14, 16));
        for (var g = r * 0.93f; g > r * 0.45f; g -= r * 0.07f)
            p.Ring(c, c, g, MathF.Max(r / 60, 0.6f), Rgba.Of(60, 60, 66, 110));
        p.Wedge(c, c, r * 0.95f, 0, 0.5f, Rgba.Of(255, 255, 255, 18));
        return p;
    }

    /// <summary>A plain green label for songs without art.</summary>
    public static Pixels PlainLabel(int size)
    {
        var p = new Pixels(size, size);
        p.Circle(size / 2f, size / 2f, size / 2f - 0.5f, Green);
        p.Circle(size / 2f, size / 2f, size * 0.06f / LabelOf, Rgba.Of(20, 20, 22));
        return p;
    }

    /// <summary>
    /// Cuts square album art (straight RGBA, any row order) to a round label in place, with the
    /// spindle hole in the middle.
    /// </summary>
    public static void RoundLabel(byte[] rgba, int size)
    {
        float c = size / 2f, r = size / 2f - 0.5f, hole = size * 0.06f / LabelOf;
        for (var y = 0; y < size; y++)
            for (var x = 0; x < size; x++)
            {
                var d = MathF.Sqrt((x + 0.5f - c) * (x + 0.5f - c) + (y + 0.5f - c) * (y + 0.5f - c));
                var i = (y * size + x) * 4;
                if (d < hole)
                {
                    rgba[i] = 20; rgba[i + 1] = 20; rgba[i + 2] = 22; rgba[i + 3] = 255;
                    continue;
                }
                var a = Math.Clamp(r - d + 0.5f, 0, 1);
                rgba[i + 3] = (byte)(rgba[i + 3] * a);
            }
    }

    public static string Clock(double seconds)
    {
        var s = (long)Math.Max(seconds, 0);
        return $"{s / 60}:{s % 60:00}";
    }
}

/// <summary>Colours taken from album art.</summary>
internal static class ArtColour
{
    /// <summary>
    /// The art's accent: the average of its colourful pixels (weighted by how saturated they are),
    /// lifted to be bright enough to read on a dark panel. Spotify green for grey art.
    /// </summary>
    public static (float R, float G, float B) Accent(byte[] rgba)
    {
        double r = 0, g = 0, b = 0, weight = 0;
        for (var i = 0; i + 3 < rgba.Length; i += 16) // every fourth pixel is plenty
        {
            int max = Math.Max(rgba[i], Math.Max(rgba[i + 1], rgba[i + 2])), min = Math.Min(rgba[i], Math.Min(rgba[i + 1], rgba[i + 2]));
            double w = (max - min) / 255.0 * (max / 255.0);
            r += rgba[i] * w; g += rgba[i + 1] * w; b += rgba[i + 2] * w; weight += w;
        }
        if (weight < rgba.Length / 16 * 0.02) return (30 / 255f, 215 / 255f, 96 / 255f);
        var (fr, fg, fb) = ((float)(r / weight / 255), (float)(g / weight / 255), (float)(b / weight / 255));
        var top = MathF.Max(fr, MathF.Max(fg, fb));
        if (top < 0.85f && top > 0) (fr, fg, fb) = (fr * 0.85f / top, fg * 0.85f / top, fb * 0.85f / top);
        return (fr, fg, fb);
    }
}
