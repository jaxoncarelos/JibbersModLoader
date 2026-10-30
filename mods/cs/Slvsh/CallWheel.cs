using Jibbers.Sdk;
using Jibbers.Sdk.UI;

namespace Slvsh;

/// <summary>
/// Draws the radial menu for calling a set: the slices around a ring (number key on each, the one
/// under the right stick lit), the part being picked in the middle, and the line so far underneath.
/// Pure, so it is tested and previewed outside the game.
/// </summary>
internal static class CallWheel
{
    public const float Size = 600;
    const float Cx = Size / 2, Cy = 250, Outer = 230, Ring = 96, Inner = Outer - Ring;

    public static OverlayFrame Build(CallMenu menu, int lit, string backKey, string closeKey)
    {
        var f = new OverlayFrame(Size, Size);
        var labels = menu.Labels;
        var n = Math.Max(labels.Count, 1);
        var sweep = 360f / n;
        // A plain ring split by dotted dividers; the slice under the stick gets a mint edge and label.
        f.Circle(Cx, Cy, Outer + 6, Look.Panel);
        f.Circle(Cx, Cy, Outer, Look.Inset);
        for (var i = 0; i < labels.Count; i++)
        {
            if (labels.Count > 1)
                for (var r = Inner + 8; r < Outer - 4; r += 9)
                {
                    var (dx, dy) = Point(i * sweep, r);
                    f.Circle(dx, dy, 2.2f, Look.Panel);
                }
            var on = i == lit;
            if (on) f.Arc(Cx, Cy, Outer - 6, 6, i * sweep + 3, sweep - 6, Look.Mint);
            var (x, y) = Point(i * sweep + sweep / 2, Outer - Ring / 2);
            var key = (i + 1) % 10;
            f.Text(key.ToString(), x - 50, y - 24, 100, 14, 11, on ? Look.Mint : Look.Caption, HudTextAlignment.Center, bold: true);
            f.Text(labels[i], x - 54, y - 8, 108, 22, labels[i].Length > 10 ? 13 : 16, on ? Look.Mint : Look.Text, HudTextAlignment.Center, bold: on);
        }

        // The middle: which part this is, and what's picked for this trick so far.
        f.Circle(Cx, Cy, Inner - 10, Look.Panel);
        f.Text("CALL YOUR SET", Cx - 100, Cy - 62, 200, 14, 10, Look.Caption, HudTextAlignment.Center, spacing: Look.Tracked);
        f.Text(menu.Title, Cx - 110, Cy - 42, 220, 30, 24, Look.Mint, HudTextAlignment.Center, bold: true);
        var picked = menu.Current == CallMenu.Step.Next ? "Add one, or call the line" : menu.Picks.Count == 0 ? $"Trick {menu.Done.Count + 1}" : string.Join(" ", menu.Picks);
        f.Text(picked, Cx - 105, Cy - 4, 210, 40, 13, Look.Text, HudTextAlignment.Center, wrap: true);
        f.Text($"{backKey} back · {closeKey} close", Cx - 100, Cy + 44, 200, 14, 10, Look.Dim, HudTextAlignment.Center);

        // The line so far.
        if (menu.Done.Count > 0)
        {
            f.Rect(20, Size - 110, Size - 40, 96, Look.Corner, Look.Panel);
            f.Text("YOUR LINE", 36, Size - 100, 200, 14, 10, Look.Caption, spacing: Look.Tracked);
            var rows = menu.Done.Select((t, i) => $"{i + 1}. {t.Describe()}").ToList();
            f.Text(string.Join("\n", rows.TakeLast(4)), 36, Size - 82, Size - 72, 66, 13, Look.Text, wrap: true);
        }
        return f;
    }

    /// <summary>The slice a stick (or mouse) pushed to (<paramref name="x"/>, <paramref name="y"/>, +Y up) points at, from 12 o'clock clockwise.</summary>
    public static int SliceAt(float x, float y, int slices)
    {
        if (slices <= 0) return -1;
        var degrees = MathF.Atan2(x, y) * 180 / MathF.PI;
        if (degrees < 0) degrees += 360;
        return Math.Min((int)(degrees / (360f / slices)), slices - 1);
    }

    static (float X, float Y) Point(float degrees, float radius)
    {
        var a = degrees * MathF.PI / 180;
        return (Cx + radius * MathF.Sin(a), Cy - radius * MathF.Cos(a));
    }
}
