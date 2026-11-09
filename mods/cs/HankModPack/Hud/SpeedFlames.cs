using Jibbers.Sdk;
using Jibbers.Sdk.Interop;

namespace HankModPack;

/// <summary>
/// Flammes animées autour de la fenêtre de vitesse. Plus on va vite, plus il y en a, plus elles sont grandes
/// et plus la lueur derrière la fenêtre est forte. Les couleurs sont celles du thème choisi (Flame1 au cœur, Flame2 au bout).
/// Un jeu de particules : chaque flamme est une petite image (même texture) qui monte, ondule, rétrécit et s'éteint.
/// </summary>
sealed class SpeedFlames : IDisposable
{
    const int PoolSize = 44;

    sealed class Particle
    {
        public OwnedGameObject Owner = null!;
        public nint Raw, Rect;
        public bool Active;
        public float Age, Life, X, Y, Rise, Sway, Phase, Size, Wobble;
    }

    readonly List<Particle> pool = new();
    readonly List<GameRef> textures = new();
    OwnedGameObject? glow;
    nint glowRaw;
    readonly Random rnd = new();
    readonly Color core, tip;
    readonly float margin, w, h, scale;
    float level, spawnAcc, clock;

    public SpeedFlames(OwnedGameObject canvas, float margin, float w, float h, float scale, Look look)
    {
        (this.margin, this.w, this.h, this.scale) = (margin, w, h, scale);
        core = HudThemes.Hex(look.Flame1);
        tip = HudThemes.Hex(look.Flame2);

        var glowTex = HudArt.MakeTexture(GlowPixels(64), 64, 64);
        var flameTex = HudArt.MakeTexture(FlamePixels(64, 96), 64, 96);
        textures.Add(new GameRef(glowTex));
        textures.Add(new GameRef(flameTex));

        // Lueur derrière la fenêtre (créée en premier : dessinée sous les flammes).
        glow = OwnedGameObject.Create("Speed.Glow");
        glowRaw = glow.AddComponent("UnityEngine.UI.RawImage");
        UnityProperties.Set(glowRaw, "set_raycastTarget", false);
        Unity.Call(glowRaw, "set_texture", glowTex);
        UnityProperties.Set(glowRaw, "set_color", new Color(tip.R, tip.G, tip.B, 0));
        var gr = Unity.Call(glowRaw, "get_rectTransform");
        Place(gr, 0.5f, 0.5f, -margin - w / 2, margin + h / 2, w * 1.9f, h * 2.1f);
        glow.AttachTo(canvas.Transform);

        for (var i = 0; i < PoolSize; i++)
        {
            var owner = OwnedGameObject.Create("Speed.Flame" + i);
            var raw = owner.AddComponent("UnityEngine.UI.RawImage");
            UnityProperties.Set(raw, "set_raycastTarget", false);
            Unity.Call(raw, "set_texture", flameTex);
            UnityProperties.Set(raw, "set_color", new Color(1, 1, 1, 0));
            var rect = Unity.Call(raw, "get_rectTransform");
            Place(rect, 0.5f, 0f, 0, 0, 10, 15);
            owner.AttachTo(canvas.Transform);
            pool.Add(new Particle { Owner = owner, Raw = raw, Rect = rect });
        }
    }

    /// <summary>Ancre en bas à droite de l'écran ; <paramref name="pivotX"/>/<paramref name="pivotY"/> : point du bloc posé sur la position.</summary>
    static void Place(nint rect, float pivotX, float pivotY, float x, float y, float width, float height)
    {
        UnityProperties.Set(rect, "set_anchorMin", new Vector2(1, 0));
        UnityProperties.Set(rect, "set_anchorMax", new Vector2(1, 0));
        UnityProperties.Set(rect, "set_pivot", new Vector2(pivotX, pivotY));
        UnityProperties.Set(rect, "set_anchoredPosition", new Vector2(x, y));
        UnityProperties.Set(rect, "set_sizeDelta", new Vector2(width, height));
    }

    /// <param name="target">Niveau voulu de 0 (aucune flamme) à 1 (feu complet).</param>
    int activeCount; bool glowShown = true;

    public void Update(float dt, float target)
    {
        // Nothing to draw: no game call at all.
        if (target < 0.005f && level < 0.005f && activeCount == 0)
        {
            if (glowShown) { UnityProperties.Set(glowRaw, "set_color", new Color(tip.R, tip.G, tip.B, 0)); glowShown = false; }
            return;
        }
        glowShown = true;
        dt = Math.Clamp(dt, 0.001f, 0.06f);
        clock += dt;
        level += (target - level) * Math.Min(1, dt * 4);

        // Lueur : pulse doucement, plus forte avec la vitesse.
        var pulse = 0.75f + 0.25f * MathF.Sin(clock * (4 + 8 * level));
        UnityProperties.Set(glowRaw, "set_color", new Color(tip.R, tip.G, tip.B, Math.Clamp(level * 0.55f * pulse, 0, 0.7f)));

        // Naissance des flammes : de plus en plus nombreuses.
        spawnAcc += (level < 0.005f ? 0 : 6 + 60 * level) * dt;
        while (spawnAcc >= 1)
        {
            spawnAcc -= 1;
            Particle? p = null;
            foreach (var candidate in pool) if (!candidate.Active) { p = candidate; break; }
            if (p is null) break;
            Spawn(p);
        }

        activeCount = 0;
        foreach (var p in pool)
        {
            if (!p.Active) continue;
            p.Age += dt;
            if (p.Age >= p.Life)
            {
                p.Active = false;
                UnityProperties.Set(p.Raw, "set_color", new Color(1, 1, 1, 0));
                continue;
            }
            activeCount++;
            var t = p.Age / p.Life;
            var x = p.X + MathF.Sin(p.Phase + p.Age * p.Wobble) * p.Sway * (0.4f + t);
            var y = p.Y + p.Rise * p.Age * (1 - 0.35f * t);
            UnityProperties.Set(p.Rect, "set_anchoredPosition", new Vector2(x, y));
            var s = (1 - 0.7f * t) * (0.6f + 0.4f * level);
            UnityProperties.Set(p.Rect, "set_localScale", new Vector3(s, s, 1));

            // Couleur : cœur -> bout, puis fondu.
            var r = core.R + (tip.R - core.R) * t;
            var g = core.G + (tip.G - core.G) * t;
            var b = core.B + (tip.B - core.B) * t;
            var a = MathF.Pow(1 - t, 1.2f) * (0.55f + 0.45f * level);
            UnityProperties.Set(p.Raw, "set_color", new Color(r, g, b, a));
        }
    }

    void Spawn(Particle p)
    {
        // Point de départ sur le contour de la fenêtre : surtout le bas et les côtés, un peu le haut.
        var edge = (float)rnd.NextDouble();
        float lx, ly;
        if (edge < 0.45f) { lx = (float)rnd.NextDouble() * w; ly = 0; }                     // bas
        else if (edge < 0.62f) { lx = 0; ly = (float)rnd.NextDouble() * h * 0.8f; }         // côté gauche
        else if (edge < 0.79f) { lx = w; ly = (float)rnd.NextDouble() * h * 0.8f; }         // côté droit
        else { lx = (float)rnd.NextDouble() * w; ly = h * 0.9f; }                           // haut

        var big = 0.7f + 0.9f * level;
        p.Size = (14 + (float)rnd.NextDouble() * 16) * scale * big;
        p.Life = 0.45f + (float)rnd.NextDouble() * 0.5f;
        p.Age = 0;
        p.X = -margin - w + lx;
        p.Y = margin + ly - 4 * scale;
        p.Rise = (45 + (float)rnd.NextDouble() * 70) * scale * (0.6f + level);
        p.Sway = (3 + (float)rnd.NextDouble() * 7) * scale;
        p.Phase = (float)rnd.NextDouble() * 6.28f;
        p.Wobble = 6 + (float)rnd.NextDouble() * 6;
        p.Active = true;
        UnityProperties.Set(p.Rect, "set_sizeDelta", new Vector2(p.Size, p.Size * 1.5f));
    }

    public void Dispose()
    {
        foreach (var p in pool) { try { p.Owner.Dispose(); } catch { } }
        pool.Clear();
        try { glow?.Dispose(); } catch { }
        glow = null;
        foreach (var t in textures)
        {
            try { if (!Unity.IsNull(t.Target)) Unity.Destroy(t.Target); } catch { }
            t.Dispose();
        }
        textures.Clear();
    }

    // ---------------------------------------------------------------- Textures (blanches : la couleur vient de la teinte de chaque image)

    static byte[] GlowPixels(int n)
    {
        var data = new byte[n * n * 4];
        for (var y = 0; y < n; y++)
        for (var x = 0; x < n; x++)
        {
            var dx = (x + 0.5f) / n * 2 - 1;
            var dy = (y + 0.5f) / n * 2 - 1;
            var r = MathF.Sqrt(dx * dx + dy * dy);
            var a = Math.Clamp(1 - r, 0, 1);
            a *= a;
            var i = (y * n + x) * 4;
            data[i] = data[i + 1] = data[i + 2] = 255;
            data[i + 3] = (byte)(a * 255);
        }
        return data;
    }

    /// <summary>Une flamme en goutte inversée : large en bas, pointue en haut, bords doux (ligne 0 = bas de l'image).</summary>
    static byte[] FlamePixels(int w, int h)
    {
        var data = new byte[w * h * 4];
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            var u = ((x + 0.5f) / w - 0.5f) * 2;   // -1..1
            var v = (y + 0.5f) / h;                // 0 (bas) .. 1 (pointe)
            var half = v < 0.28f ? MathF.Sqrt(v / 0.28f) : MathF.Pow((1 - v) / 0.72f, 0.85f);
            half *= 0.92f;
            var a = half <= 0.001f ? 0 : Math.Clamp(1 - MathF.Abs(u) / half, 0, 1);
            a = MathF.Pow(a, 0.7f) * (1 - 0.25f * v);
            var i = (y * w + x) * 4;
            data[i] = data[i + 1] = data[i + 2] = 255;
            data[i + 3] = (byte)(Math.Clamp(a, 0, 1) * 255);
        }
        return data;
    }
}
