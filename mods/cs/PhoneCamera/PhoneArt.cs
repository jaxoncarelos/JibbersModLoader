namespace PhoneCamera;

/// <summary>
/// The phone on screen, in pixels at 1080p (y down): its size, where the camera's picture shows
/// (16:9 sideways, 9:16 upright) and the record button. Held sideways the button is on the right;
/// upright, underneath.
/// </summary>
internal readonly record struct PhoneLayout(int W, int H, float ScreenX, float ScreenY, float ScreenW, float ScreenH, float ButtonX, float ButtonY, float ButtonR)
{
    public static PhoneLayout For(bool portrait) => portrait
        ? new(350, 660, 17, 20, 315, 560, 175, 620, 26)
        : new(660, 350, 20, 17, 560, 315, 620, 175, 26);
}

/// <summary>
/// Draws the phone's body: a dark rounded case with a see-through hole where the picture shows
/// (red-edged while recording), the status bar's backing along the top of the picture, and the
/// record button (a white ring round a red dot, a red square while recording). Straight RGBA,
/// rows top-down. No game calls.
/// </summary>
internal static class PhoneArt
{
    public static byte[] Draw(PhoneLayout l, bool recording)
    {
        var px = new float[l.W * l.H * 4];
        void Blend(int x, int y, float r, float g, float b, float a)
        {
            if (a <= 0 || x < 0 || y < 0 || x >= l.W || y >= l.H) return;
            var i = (y * l.W + x) * 4;
            var keep = 1 - a;
            px[i] = r * a + px[i] * keep;
            px[i + 1] = g * a + px[i + 1] * keep;
            px[i + 2] = b * a + px[i + 2] * keep;
            px[i + 3] = a + px[i + 3] * keep;
        }
        // Signed distance to a rounded box, and an anti-aliased coverage from it.
        static float Box(float x, float y, float bx, float by, float bw, float bh, float r)
        {
            var qx = MathF.Abs(x - (bx + bw / 2)) - (bw / 2 - r);
            var qy = MathF.Abs(y - (by + bh / 2)) - (bh / 2 - r);
            return MathF.Sqrt(MathF.Max(qx, 0) * MathF.Max(qx, 0) + MathF.Max(qy, 0) * MathF.Max(qy, 0)) + MathF.Min(MathF.Max(qx, qy), 0) - r;
        }
        static float Cover(float d) => Math.Clamp(0.5f - d, 0, 1);

        for (var y = 0; y < l.H; y++)
            for (var x = 0; x < l.W; x++)
            {
                float fx = x + 0.5f, fy = y + 0.5f;
                // The case, its edge highlight, then the hole cut out.
                Blend(x, y, 18 / 255f, 18 / 255f, 22 / 255f, Cover(Box(fx, fy, 0, 0, l.W, l.H, 42)));
                var edge = MathF.Abs(Box(fx, fy, 3, 3, l.W - 6, l.H - 6, 39));
                Blend(x, y, 70 / 255f, 70 / 255f, 80 / 255f, Cover(edge - 1));
                var hole = Box(fx, fy, l.ScreenX, l.ScreenY, l.ScreenW, l.ScreenH, 14);
                if (hole < 0.5f)
                {
                    var i = (y * l.W + x) * 4;
                    var keep = Math.Clamp(hole + 0.5f, 0, 1);
                    for (var k = 0; k < 4; k++) px[i + k] *= keep;
                }
                if (recording)
                    Blend(x, y, 1, 60 / 255f, 60 / 255f, Cover(MathF.Abs(hole) - 1.5f));
                // The status bar's backing.
                Blend(x, y, 0, 0, 0, 120 / 255f * Cover(Box(fx, fy, l.ScreenX + 8, l.ScreenY + 8, l.ScreenW - 16, 30, 15)));
                // The record button.
                var d = MathF.Sqrt((fx - l.ButtonX) * (fx - l.ButtonX) + (fy - l.ButtonY) * (fy - l.ButtonY));
                Blend(x, y, 235 / 255f, 235 / 255f, 240 / 255f, Cover(MathF.Abs(d - l.ButtonR) - 2));
                var dot = recording ? Box(fx, fy, l.ButtonX - 10, l.ButtonY - 10, 20, 20, 4) : d - (l.ButtonR - 7);
                Blend(x, y, 1, 60 / 255f, 60 / 255f, Cover(dot));
            }

        // Premultiplied floats to straight bytes.
        var rgba = new byte[px.Length];
        for (var i = 0; i < px.Length; i += 4)
        {
            var a = px[i + 3];
            if (a <= 0) continue;
            for (var k = 0; k < 3; k++)
                rgba[i + k] = (byte)Math.Clamp(MathF.Round(px[i + k] / a * 255), 0, 255);
            rgba[i + 3] = (byte)Math.Clamp(MathF.Round(a * 255), 0, 255);
        }
        return rgba;
    }
}
