namespace RickBackflip;

/// <summary>
/// Dessine le nom d'un trick façon jeu de snowboard arcade : grosses lettres arrondies inclinées, dégradé,
/// contour sombre, ombre rose. Tout est généré en code (police pixel 5x7 lissée) : aucune image ni police externe.
/// Le résultat est une petite animation (apparition en rebond, maintien, fondu) affichable comme un GIF.
/// </summary>
internal static class TrickBanner
{
    // Police 5x7. Chaque lettre = 7 lignes de 5 colonnes ('#' = plein).
    static readonly Dictionary<char, string[]> Font = Build();

    static Dictionary<char, string[]> Build()
    {
        var f = new Dictionary<char, string[]>();
        void G(char c, params string[] rows) => f[c] = rows;
        G('A', ".###.", "#...#", "#...#", "#####", "#...#", "#...#", "#...#");
        G('B', "####.", "#...#", "#...#", "####.", "#...#", "#...#", "####.");
        G('C', ".####", "#....", "#....", "#....", "#....", "#....", ".####");
        G('D', "####.", "#...#", "#...#", "#...#", "#...#", "#...#", "####.");
        G('E', "#####", "#....", "#....", "####.", "#....", "#....", "#####");
        G('F', "#####", "#....", "#....", "####.", "#....", "#....", "#....");
        G('G', ".####", "#....", "#....", "#.###", "#...#", "#...#", ".###.");
        G('H', "#...#", "#...#", "#...#", "#####", "#...#", "#...#", "#...#");
        G('I', "#####", "..#..", "..#..", "..#..", "..#..", "..#..", "#####");
        G('J', "..###", "...#.", "...#.", "...#.", "...#.", "#..#.", ".##..");
        G('K', "#...#", "#..#.", "#.#..", "##...", "#.#..", "#..#.", "#...#");
        G('L', "#....", "#....", "#....", "#....", "#....", "#....", "#####");
        G('M', "#...#", "##.##", "#.#.#", "#.#.#", "#...#", "#...#", "#...#");
        G('N', "#...#", "##..#", "#.#.#", "#..##", "#...#", "#...#", "#...#");
        G('O', ".###.", "#...#", "#...#", "#...#", "#...#", "#...#", ".###.");
        G('P', "####.", "#...#", "#...#", "####.", "#....", "#....", "#....");
        G('Q', ".###.", "#...#", "#...#", "#...#", "#.#.#", "#..#.", ".##.#");
        G('R', "####.", "#...#", "#...#", "####.", "#.#..", "#..#.", "#...#");
        G('S', ".####", "#....", "#....", ".###.", "....#", "....#", "####.");
        G('T', "#####", "..#..", "..#..", "..#..", "..#..", "..#..", "..#..");
        G('U', "#...#", "#...#", "#...#", "#...#", "#...#", "#...#", ".###.");
        G('V', "#...#", "#...#", "#...#", "#...#", "#...#", ".#.#.", "..#..");
        G('W', "#...#", "#...#", "#...#", "#.#.#", "#.#.#", "##.##", "#...#");
        G('X', "#...#", "#...#", ".#.#.", "..#..", ".#.#.", "#...#", "#...#");
        G('Y', "#...#", "#...#", ".#.#.", "..#..", "..#..", "..#..", "..#..");
        G('Z', "#####", "....#", "...#.", "..#..", ".#...", "#....", "#####");
        G('0', ".###.", "#...#", "#..##", "#.#.#", "##..#", "#...#", ".###.");
        G('1', "..#..", ".##..", "..#..", "..#..", "..#..", "..#..", ".###.");
        G('2', ".###.", "#...#", "....#", "...#.", "..#..", ".#...", "#####");
        G('3', "####.", "....#", "....#", ".###.", "....#", "....#", "####.");
        G('4', "...#.", "..##.", ".#.#.", "#..#.", "#####", "...#.", "...#.");
        G('5', "#####", "#....", "####.", "....#", "....#", "#...#", ".###.");
        G('6', ".###.", "#....", "#....", "####.", "#...#", "#...#", ".###.");
        G('7', "#####", "....#", "...#.", "..#..", ".#...", ".#...", ".#...");
        G('8', ".###.", "#...#", "#...#", ".###.", "#...#", "#...#", ".###.");
        G('9', ".###.", "#...#", "#...#", ".####", "....#", "....#", ".###.");
        G('-', ".....", ".....", ".....", "#####", ".....", ".....", ".....");
        G('+', ".....", "..#..", "..#..", "#####", "..#..", "..#..", ".....");
        G('!', "..#..", "..#..", "..#..", "..#..", "..#..", ".....", "..#..");
        G('.', ".....", ".....", ".....", ".....", ".....", ".##..", ".##..");
        G('\'', "..#..", "..#..", ".#...", ".....", ".....", ".....", ".....");
        G('?', ".###.", "#...#", "....#", "...#.", "..#..", ".....", "..#..");
        return f;
    }

    /// <summary>Score de difficulté d'un trick (tours + flips), de 0 à environ 10.</summary>
    public static double Score(string name)
    {
        double score = 0;
        foreach (var part in name.Split(" - ", StringSplitOptions.RemoveEmptyEntries))
        {
            double degrees = 0, mult = 1;
            var flips = false;
            var axis = false;
            foreach (var w in part.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (int.TryParse(w, out var d)) degrees = Math.Max(degrees, d);
                else
                {
                    var l = w.ToLowerInvariant();
                    if (l is "dub" or "double") mult = Math.Max(mult, 2);
                    else if (l is "trip" or "triple") mult = Math.Max(mult, 3);
                    else if (l is "quad") mult = Math.Max(mult, 4);
                    if (l.Contains("flip")) flips = true;
                    if (l is "cork" or "misty" or "bio" or "lincoln") axis = true;
                }
            }
            score += degrees / 360.0 + (flips ? mult * 1.2 : 0);
            if (axis) score += 0.8 + (mult - 1) * 0.6;       // les spins sur axe (cork, misty...) valent plus qu'un spin à plat
            else if (!flips && mult > 1) score += mult * 0.3;
        }
        return score;
    }

    /// <summary>Intensité de 0 (petit trick) à 1 (énorme) : pilote couleur, lueur et taille.</summary>
    public static double Intensity(string name) => Math.Clamp(Score(name) / 7.0, 0, 1);

    /// <summary>Estimation de la difficulté d'un trick : 1 (simple), 2 (bon), 3 (énorme).</summary>
    public static int Tier(string name)
    {
        var score = Score(name);
        return score < 2.5 ? 1 : score < 4.5 ? 2 : 3;
    }

    static readonly string[][] Mentions =
    {
        new[] { "NICE!", "CLEAN!", "GOOD!" },
        new[] { "SWEET!", "SMOOTH!", "STYLISH!" },
        new[] { "SICK!", "RAD!", "GNARLY!" },
        new[] { "CRAZY!", "WILD!", "HUGE!" },
        new[] { "INSANE!", "UNREAL!", "LEGENDARY!" },
    };

    /// <summary>La mention affichée sous le trick : de « NICE! » pour un petit trick à « INSANE! » / « LEGENDARY! » pour un énorme.</summary>
    public static string Subtitle(string trickName)
    {
        var i = Intensity(trickName);
        var level = i < 0.12 ? 0 : i < 0.28 ? 1 : i < 0.45 ? 2 : i < 0.68 ? 3 : 4;
        var hash = 17;
        foreach (var c in trickName) hash = hash * 31 + c; // même trick = même mot (pas de hasard qui changerait au redessin)
        return Mentions[level][(hash & 0x7fffffff) % Mentions[level].Length];
    }

    /// <summary>Coupe le nom en lignes de 16 caractères au plus (sur les espaces et les « - » des rails).</summary>
    static List<string> Wrap(string text)
    {
        var lines = new List<string>();
        var cur = "";
        foreach (var w in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (cur.Length > 0 && cur.Length + 1 + w.Length > 16) { lines.Add(cur); cur = w; }
            else cur = cur.Length == 0 ? w : cur + " " + w;
        }
        if (cur.Length > 0) lines.Add(cur);
        if (lines.Count > 3) lines = new List<string> { lines[0], lines[1], string.Join(' ', lines.Skip(2)) };
        return lines;
    }

    const int U = 7; // taille d'un pixel de police (px à 100 %)

    /// <summary>Construit l'animation d'un trick. Renvoie null si le nom ne contient rien de dessinable.</summary>
    public static GifAnimation? Render(string trickName, int tier)
    {
        var text = new string(trickName.ToUpperInvariant().Where(c => Font.ContainsKey(c) || c == ' ').ToArray()).Trim();
        if (text.Length == 0) return null;
        if (text.Length > 60) text = text[..60];
        var lines = Wrap(text);
        var sub = Subtitle(trickName);

        const int pad = 70;                 // marge pour l'ombre, le contour et le rebond
        var lineH = 8 * U;                  // 7 lignes de police + interligne
        var maxChars = lines.Max(l => l.Length);
        int Width(string l) => l.Length * 6 * U;
        var subScale = 0.75;
        var subW = (int)(sub.Length * 6 * U * subScale);
        var textW = Math.Max(maxChars * 6 * U, subW) + 4 * U; // + inclinaison
        var textH = lines.Count * lineH + (int)(8 * U * subScale) + U;
        int W = textW + pad * 2, H = textH + pad * 2;

        var fill = new float[W * H];       // masque des lettres (haut en bas)
        var bands = new List<(double Top, double Height, byte Kind)>(); // bandes de texte : dégradé et couleur par ligne

        void Stamp(string line, int top, double scale, byte k)
        {
            var u = U * scale;
            bands.Add((top, 7 * u, k));
            var lineW = line.Length * 6 * u;
            var x0 = pad + (textW - lineW) / 2.0;
            for (var i = 0; i < line.Length; i++)
            {
                if (!Font.TryGetValue(line[i], out var rows)) continue;
                for (var r = 0; r < 7; r++)
                    for (var c = 0; c < 5; c++)
                    {
                        if (rows[r][c] != '#') continue;
                        var skew = (6 - r) * u * 0.28; // plus on monte, plus on penche à droite
                        var bx = x0 + (i * 6 + c) * u + skew;
                        var by = top + r * u;
                        var e = u * 0.08; // les blocs se recouvrent un peu : pas de trous aux coins des diagonales
                        for (var yy = (int)(by - e); yy < (int)(by + u + e + 1); yy++)
                            for (var xx = (int)(bx - e); xx < (int)(bx + u + e + 1); xx++)
                            {
                                if (xx < 0 || yy < 0 || xx >= W || yy >= H) continue;
                                var idx = yy * W + xx;
                                fill[idx] = 1;
                            }
                    }
            }
        }

        var y = pad;
        foreach (var l in lines) { Stamp(l, y, 1.0, 0); y += lineH; }
        Stamp(sub, y + U / 2, subScale, 1);

        // Lettres arrondies : flou puis seuil adouci.
        var soft = Blur(Blur(fill, W, H, 3), W, H, 2);
        var alpha = new float[W * H];
        for (var i = 0; i < alpha.Length; i++) alpha[i] = Smooth(0.30f, 0.62f, soft[i]);

        // Contour épais : le masque dilaté puis adouci.
        var outline = Blur(Dilate(alpha, W, H, 9), W, H, 2);
        for (var i = 0; i < outline.Length; i++) outline[i] = Smooth(0.2f, 0.6f, outline[i]);
        var halo = Blur(Dilate(outline, W, H, 5), W, H, 3);

        var rgba = new byte[W * H * 4]; // haut en bas pour l'instant
        for (var yy = 0; yy < H; yy++)
            for (var xx = 0; xx < W; xx++)
            {
                var i = yy * W + xx;
                float r = 0, g = 0, b = 0, a = 0;
                void Over(float cr, float cg, float cb, float ca)
                {
                    var oa = ca + a * (1 - ca);
                    if (oa <= 0) return;
                    r = (cr * ca + r * a * (1 - ca)) / oa;
                    g = (cg * ca + g * a * (1 - ca)) / oa;
                    b = (cb * ca + b * a * (1 - ca)) / oa;
                    a = oa;
                }
                // ombre rose décalée, halo blanc léger, contour sombre, remplissage dégradé
                var sx = xx - 7; var sy = yy - 7;
                if (sx >= 0 && sy >= 0) Over(0.88f, 0.10f, 0.49f, outline[sy * W + sx] * 0.95f);
                Over(1f, 1f, 1f, halo[i] * 0.85f * (1 - outline[i]));
                Over(0.16f, 0.04f, 0.29f, outline[i]);
                if (alpha[i] > 0)
                {
                    var band = bands.OrderBy(bd => Math.Abs(yy - (bd.Top + bd.Height / 2))).First();
                    var t = (float)Math.Clamp((yy - band.Top) / band.Height, 0, 1);
                    var kindOf = band.Kind;
                    float fr, fg, fb;
                    if (kindOf == 0) { fr = 1f; fg = 0.96f - 0.50f * t; fb = 0.48f - 0.40f * t; }  // jaune -> orange
                    else { fr = 0.45f - 0.35f * t; fg = 0.95f - 0.35f * t; fb = 1f; }                // cyan -> bleu
                    Over(fr, fg, fb, alpha[i]);
                }
                rgba[i * 4] = (byte)(Math.Clamp(r, 0, 1) * 255);
                rgba[i * 4 + 1] = (byte)(Math.Clamp(g, 0, 1) * 255);
                rgba[i * 4 + 2] = (byte)(Math.Clamp(b, 0, 1) * 255);
                rgba[i * 4 + 3] = (byte)(Math.Clamp(a, 0, 1) * 255);
            }

        // Animation : rebond d'entrée, maintien, fondu.
        var frames = new List<byte[]>();
        var delays = new List<int>();
        foreach (var (scale, opacity) in new[] { (0.35, 0.4), (0.7, 0.8), (1.14, 1.0), (0.97, 1.0), (1.04, 1.0), (1.0, 1.0) })
        {
            frames.Add(Transform(rgba, W, H, scale, opacity));
            delays.Add(30);
        }
        frames.Add(Transform(rgba, W, H, 1.0, 1.0));
        delays.Add(1100);
        foreach (var opacity in new[] { 0.8, 0.6, 0.4, 0.2, 0.05 })
        {
            frames.Add(Transform(rgba, W, H, 1.0 + (1 - opacity) * 0.06, opacity));
            delays.Add(40);
        }
        frames.Add(new byte[W * H * 4]); // transparent à la fin
        delays.Add(100);
        return GifAnimation.FromFrames(W, H, frames, delays);
    }

    static float Smooth(float a, float b, float x)
    {
        var t = Math.Clamp((x - a) / (b - a), 0f, 1f);
        return t * t * (3 - 2 * t);
    }

    /// <summary>Flou de boîte séparable de rayon <paramref name="r"/>.</summary>
    static float[] Blur(float[] src, int w, int h, int r)
    {
        var tmp = new float[src.Length];
        var dst = new float[src.Length];
        var n = 2 * r + 1;
        for (var y = 0; y < h; y++)
        {
            float sum = 0;
            for (var x = -r; x <= r; x++) sum += src[y * w + Math.Clamp(x, 0, w - 1)];
            for (var x = 0; x < w; x++)
            {
                tmp[y * w + x] = sum / n;
                sum += src[y * w + Math.Min(x + r + 1, w - 1)] - src[y * w + Math.Max(x - r, 0)];
            }
        }
        for (var x = 0; x < w; x++)
        {
            float sum = 0;
            for (var y = -r; y <= r; y++) sum += tmp[Math.Clamp(y, 0, h - 1) * w + x];
            for (var y = 0; y < h; y++)
            {
                dst[y * w + x] = sum / n;
                sum += tmp[Math.Min(y + r + 1, h - 1) * w + x] - tmp[Math.Max(y - r, 0) * w + x];
            }
        }
        return dst;
    }

    /// <summary>Maximum glissant (carré de rayon r) : épaissit un masque.</summary>
    static float[] Dilate(float[] src, int w, int h, int r)
    {
        var tmp = new float[src.Length];
        var dst = new float[src.Length];
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                float m = 0;
                for (var k = Math.Max(0, x - r); k <= Math.Min(w - 1, x + r); k++) m = Math.Max(m, src[y * w + k]);
                tmp[y * w + x] = m;
            }
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                float m = 0;
                for (var k = Math.Max(0, y - r); k <= Math.Min(h - 1, y + r); k++) m = Math.Max(m, tmp[k * w + x]);
                dst[y * w + x] = m;
            }
        return dst;
    }

    /// <summary>Redimensionne autour du centre et applique l'opacité ; sortie en lignes bas-en-haut (Unity).</summary>
    static byte[] Transform(byte[] src, int w, int h, double scale, double opacity)
    {
        var dst = new byte[src.Length];
        double cx = w / 2.0, cy = h / 2.0;
        for (var y = 0; y < h; y++)
        {
            var sy = (y - cy) / scale + cy;
            var oy = h - 1 - y; // retourne : Unity lit de bas en haut
            for (var x = 0; x < w; x++)
            {
                var sx = (x - cx) / scale + cx;
                if (sx < 0 || sy < 0 || sx >= w - 1 || sy >= h - 1) continue;
                int x0 = (int)sx, y0 = (int)sy;
                double fx = sx - x0, fy = sy - y0;
                var o = (oy * w + x) * 4;
                double a = 0, r = 0, g = 0, b = 0;
                for (var k = 0; k < 4; k++)
                {
                    var px = x0 + (k & 1); var py = y0 + (k >> 1);
                    var wgt = ((k & 1) == 0 ? 1 - fx : fx) * ((k >> 1) == 0 ? 1 - fy : fy);
                    var i = (py * w + px) * 4;
                    var pa = src[i + 3] / 255.0 * wgt;
                    r += src[i] * pa; g += src[i + 1] * pa; b += src[i + 2] * pa; a += pa;
                }
                if (a <= 0) continue;
                dst[o] = (byte)(r / a);
                dst[o + 1] = (byte)(g / a);
                dst[o + 2] = (byte)(b / a);
                dst[o + 3] = (byte)(a * opacity * 255);
            }
        }
        return dst;
    }
}
