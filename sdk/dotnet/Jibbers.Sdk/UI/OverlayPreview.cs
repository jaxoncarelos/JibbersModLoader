using System.IO.Compression;
using System.Net;
using System.Text;

namespace Jibbers.Sdk.UI;

/// <summary>
/// Design overlays without starting the game: saves a frame as a web page (its shapes as a PNG,
/// its text laid out on top) that any browser opens. Text uses a system font, so sizes are close
/// to, not exactly, the game's.
/// </summary>
/// <example><code>OverlayPreview.Save(BuildScoreboard(sampleGame), "preview/scoreboard.html");</code></example>
public static class OverlayPreview
{
    /// <summary>Writes <paramref name="htmlPath"/> and a PNG beside it.</summary>
    public static void Save(OverlayFrame frame, string htmlPath, float sharpness = 2) => Save(new[] { (frame, Path.GetFileNameWithoutExtension(htmlPath)) }, htmlPath, sharpness);

    /// <summary>Writes several frames onto one page, side by side, with their PNGs beside it.</summary>
    public static void Save(IEnumerable<(OverlayFrame Frame, string Name)> frames, string htmlPath, float sharpness = 2)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(htmlPath))!;
        Directory.CreateDirectory(dir);
        var html = new StringBuilder("""
            <!doctype html><meta charset="utf-8"><title>Overlay preview</title>
            <style>
            body{margin:0;padding:40px;display:flex;flex-wrap:wrap;gap:48px;align-items:flex-start;min-height:100vh;box-sizing:border-box;
              background:linear-gradient(160deg,#9fb4c9 0%,#d9e3ec 38%,#f4f7fa 60%,#7d8fa3 100%);font-family:'Segoe UI',system-ui,sans-serif}
            figure{margin:0}figcaption{font:600 12px 'Segoe UI',sans-serif;color:#33404f;margin-bottom:8px;letter-spacing:.08em;text-transform:uppercase}
            .f{position:relative}.f>img{position:absolute;left:0;top:0}
            .t{position:absolute;display:flex;white-space:nowrap;overflow:hidden;text-overflow:ellipsis;line-height:1.1}
            .t>span{overflow:hidden;text-overflow:ellipsis;width:100%}
            </style>
            """);
        foreach (var (frame, name) in frames)
        {
            var png = $"{Slug(name)}.png";
            var raster = frame.Rasterize(sharpness);
            File.WriteAllBytes(Path.Combine(dir, png), Png(raster.Width, raster.Height, raster.ToRgba32(), bottomUp: true));
            html.Append($"<figure><figcaption>{WebUtility.HtmlEncode(name)}</figcaption><div class=f style=\"width:{frame.Width}px;height:{frame.Height}px\">");
            html.Append($"<img src=\"{png}\" width={frame.Width} height={frame.Height}>");
            foreach (var t in frame.Texts)
            {
                var (h, v) = Align(t.Align);
                var c = $"rgba({(int)(t.Color.R * 255)},{(int)(t.Color.G * 255)},{(int)(t.Color.B * 255)},{t.Color.A:0.###})";
                html.Append($"<div class=t style=\"left:{t.X}px;top:{t.Y}px;width:{t.W}px;height:{t.H}px;font-size:{t.Size}px;color:{c};align-items:{v};text-align:{h};")
                    .Append($"font-weight:{(t.Bold ? 700 : 400)};letter-spacing:{t.Spacing / 100:0.###}em;{(t.Wrap ? "white-space:normal;" : "")}\"><span>{WebUtility.HtmlEncode(t.Text)}</span></div>");
            }
            html.Append("</div></figure>");
        }
        File.WriteAllText(htmlPath, html.ToString());
    }

    static (string H, string V) Align(HudTextAlignment a) => a switch
    {
        HudTextAlignment.TopLeft => ("left", "flex-start"), HudTextAlignment.Top => ("center", "flex-start"), HudTextAlignment.TopRight => ("right", "flex-start"),
        HudTextAlignment.Left => ("left", "center"), HudTextAlignment.Center => ("center", "center"), HudTextAlignment.Right => ("right", "center"),
        HudTextAlignment.BottomLeft => ("left", "flex-end"), HudTextAlignment.Bottom => ("center", "flex-end"), _ => ("right", "flex-end"),
    };

    static string Slug(string name) => new(name.Select(c => char.IsAsciiLetterOrDigit(c) ? char.ToLowerInvariant(c) : '-').ToArray());

    /// <summary>A minimal RGBA PNG encoder.</summary>
    internal static byte[] Png(int width, int height, byte[] rgba, bool bottomUp)
    {
        var raw = new byte[height * (width * 4 + 1)];
        for (var y = 0; y < height; y++)
            Buffer.BlockCopy(rgba, (bottomUp ? height - 1 - y : y) * width * 4, raw, y * (width * 4 + 1) + 1, width * 4);
        using var compressed = new MemoryStream();
        using (var z = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true)) z.Write(raw);
        using var png = new MemoryStream();
        png.Write(new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A });
        var header = new byte[13];
        WriteBig(header, 0, width);
        WriteBig(header, 4, height);
        header[8] = 8; // bits per channel
        header[9] = 6; // RGBA
        Chunk(png, "IHDR", header);
        Chunk(png, "IDAT", compressed.ToArray());
        Chunk(png, "IEND", Array.Empty<byte>());
        return png.ToArray();
    }

    static void Chunk(Stream s, string type, byte[] data)
    {
        var len = new byte[4];
        WriteBig(len, 0, data.Length);
        s.Write(len);
        var body = Encoding.ASCII.GetBytes(type).Concat(data).ToArray();
        s.Write(body);
        var crc = new byte[4];
        WriteBig(crc, 0, (int)Crc(body));
        s.Write(crc);
    }

    static void WriteBig(byte[] b, int at, int v) { b[at] = (byte)(v >> 24); b[at + 1] = (byte)(v >> 16); b[at + 2] = (byte)(v >> 8); b[at + 3] = (byte)v; }

    static readonly uint[] crcTable = Enumerable.Range(0, 256).Select(n =>
    {
        var c = (uint)n;
        for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
        return c;
    }).ToArray();

    static uint Crc(byte[] data)
    {
        var c = 0xFFFFFFFFu;
        foreach (var b in data) c = crcTable[(c ^ b) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFFu;
    }
}
