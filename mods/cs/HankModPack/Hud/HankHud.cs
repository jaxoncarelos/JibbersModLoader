using System.Diagnostics;
using System.Text.Json.Nodes;
using Jibbers.Sdk;
using Jibbers.Sdk.Game.Player;
using Jibbers.Sdk.Interop;
using RickBackflip;
using EnvManager = Jibbers.Sdk.Game.Env.Manager;

namespace HankModPack;

/// <summary>
/// Remplace le HUD du jeu par deux fenêtres, dans le thème de ton choix (11 thèmes) :
/// en bas à gauche le spawnpoint, sa stance (Regular, Switch L, Switch R) et la vitesse de respawn ;
/// en bas à droite la vitesse actuelle en km/h.
/// Les valeurs sont lues dans les textes du HUD du jeu, qui est ensuite rendu invisible.
/// </summary>
public sealed class HankHudMod : PackPart
{
    // Shown in Options > Mods only what the quick menu does not change (HUD on/off and the theme are there).
    protected override bool HiddenInModsTab(Setting s) => s.Key is "show" or "hidden" or "theme";
    protected override string PartId => "hank_hud";
    public override string Name => "Hank Mod Pack: HUD";
    public override string Description => "Replaces the game's spawnpoint and speed HUD with two windows. 11 themes: White, Black, Vaporwave, Cyberpunk, Terminal, Ice, Arcade, Rounded, Hip Hop, Freestyle, Graffiti.";

    [Toggle("Show HUD", true, Key = "show", Tooltip = "Shows the two windows. Turn off to get the game's own HUD back.")]
    public bool Show { get; private set; }

    [Toggle("Hide the HUD completely", false, Key = "hidden", Tooltip = "Hides the two windows AND keeps the game's own HUD hidden, so nothing of the HUD shows while playing. Also switched from the quick menu (D-pad down + L2). Menus and windows still show.")]
    public bool Hidden { get; private set; }

    [Choice("HUD theme", "White", "White", "White (no background)", "Black", "Black (no background)", "Vaporwave", "Vaporwave (no background)", "Cyberpunk", "Cyberpunk (no background)", "Terminal", "Terminal (no background)", "Ice", "Ice (no background)", "Arcade", "Arcade (no background)", "Rounded", "Rounded (no background)", "Hip Hop", "Hip Hop (no background)", "Freestyle", "Freestyle (no background)", "Graffiti", "Graffiti (no background)", "Sunset", "Sunset (no background)", "Ocean", "Ocean (no background)", "Lava", "Lava (no background)", "Forest", "Forest (no background)", "Candy", "Candy (no background)", "Gold", "Gold (no background)", "Midnight", "Midnight (no background)", "Sakura", "Sakura (no background)", "Steel plate", "Steel plate (no background)", "Wood", "Wood (no background)", "Wool", "Wool (no background)", "Winter", "Winter (no background)", "Summer", "Summer (no background)", "X Games", "X Games (no background)", "SLVSH", "SLVSH (no background)", "Skis", "Skis (no background)", "Toxic", "Toxic (no background)", "Text only",
        Key = "theme", Tooltip = "Colours and shape of the two windows.")]
    public string Theme { get; private set; } = "White";

    [Toggle("Hide HUD when a menu is open", true, Key = "hide_menus",
        Tooltip = "Hides the HUD while an interface is open in game (/place, pause menu...). It stays visible in Options > Mods so you can pick the theme.")]
    public bool HideOnMenus { get; private set; }

    [Toggle("Speed flames", true, Key = "flames", Tooltip = "Animated flames around the speed window, in the colours of the theme. The faster you go, the bigger the fire.")]
    public bool Flames { get; private set; }

    [Slider("Flames start at (km/h)", 0, 0, 100, 5, Key = "flames_start", Tooltip = "Speed where the first sparks appear (0 = from the start). The fire is at its maximum at 150 km/h and stays the same above.")]
    public float FlamesStart { get; private set; }

    [Slider("HUD size (%)", 100, 50, 200, 5, Key = "scale", Tooltip = "Size of both windows.")]
    public float ScalePercent { get; private set; }

    // ---------------------------------------------------------------- État

    OwnedGameObject? root;
    GameRef? leftText, rightText;
    SpeedFlames? flames; float flameAcc;
    (string, int, string, string, string, bool) lastTextKey;
    long inGameSince;
    int cursorBaseline = -1, cursorLogs;
    int lastCursorKey = -2;
    long lastFrame;
    bool rootShown = true;
    unsafe void ShowRoot(bool shown)
    {
        if (rootShown == shown || root is null || Unity.IsNull(root.Target)) return;
        Unity.Call(root.Target, "SetActive", (nint)(&shown));
        rootShown = shown;
    }

    long disabledSince;
    long nextDisabledRefresh;
    readonly List<GameRef> textures = new();
    bool needRebuild;
    long lastBuildTry;
    string shownLeft = "", shownRight = "";

    // Vitesse calculée (secours si le nombre du jeu est introuvable).
    nint lastRiderPtr;
    Vector3 lastPosition;
    long lastSample;
    bool haveSample;
    Vector3 velocity;
    int? lastGameKmh;
    public static HankHudMod? Instance { get; private set; }

    /// <summary>An interface (pause menu, /place...) is open in game: the mouse cursor is where the game normally hides it.</summary>
    public bool InterfaceUp { get; private set; }


    string? pendingTheme;
    long pendingAt;

    /// <summary>The theme shown right now in the quick menu: the one just picked with L1 / R1, even before the HUD itself has switched to it.</summary>
    public string DisplayTheme => pendingTheme ?? Theme;

    /// <summary>The theme's look (colours and shape), for the quick menu.</summary>
    internal Look CurrentLook => HudThemes.Get(DisplayTheme);

    /// <summary>The name of the theme after (or before) the one shown, in the list order.</summary>
    public string NeighbourTheme(int step)
    {
        var names = HudThemes.Names;
        var i = Math.Max(0, Array.IndexOf(names, DisplayTheme));
        return names[((i + step) % names.Length + names.Length) % names.Length];
    }

    /// <summary>
    /// Picks the next / previous theme (quick menu: L1 / R1). The HUD is rebuilt only once the presses stop for a moment,
    /// so going quickly through several themes does not rebuild the windows (and their flames) for each press.
    /// </summary>
    public void CycleTheme(int step)
    {
        pendingTheme = NeighbourTheme(step);
        pendingAt = Stopwatch.GetTimestamp();
        Log($"HUD: theme picked in the quick menu: {pendingTheme}");
    }

    void ApplyPendingTheme()
    {
        if (pendingTheme is null || Stopwatch.GetElapsedTime(pendingAt).TotalSeconds < 0.5) return;
        var t = pendingTheme;
        pendingTheme = null;
        try { SetSetting("theme", System.Text.Json.Nodes.JsonValue.Create(t)!); Log($"HUD: theme applied: {t}"); }
        catch (Exception e) { Log("HUD: could not change the theme: " + e.Message); }
    }

    long lastGameChange;

    readonly GameHudReader game = new();

    public override void OnLoad()
    {
        ImportPackSettings();
        Instance = this;
        Jibbers.Sdk.Game.Event.ChangeScene.Subscribe(_ => OnDeactivated());
        EnvManager.UpdateMethod.Before((_, _) => Frame());
        Log("HUD ready.");
    }

    protected override void OnPackSettingChanged(string key, JsonNode value)
    {
        if (key is "scale" or "show" or "hidden" or "enabled" or "theme" or "flames") { needRebuild = true; lastBuildTry = 0; }
    }

    protected override void OnDeactivated()
    {
        Cleanup(Teardown, game.Restore);
        InterfaceUp = false;
    }

    protected override void OnPackUnload()
    {
        try
        {
            if (MainThread.IsCurrent) { Teardown(); game.Restore(); }
            else MainThread.Run(() => { Teardown(); game.Restore(); });
        }
        catch (Exception e) { Log("Unload: " + e.Message); }
    }

    void Teardown()
    {
        var (r, l, s) = (root, leftText, rightText);
        var tex = textures.ToArray();
        var fx = flames;
        flames = null;
        try { fx?.Dispose(); } catch { }
        (root, leftText, rightText) = (null, null, null);
        textures.Clear();
        shownLeft = shownRight = "";
        lastTextKey = default;
        r?.Dispose();
        l?.Dispose();
        s?.Dispose();
        foreach (var t in tex)
        {
            try { if (!Unity.IsNull(t.Target)) Unity.Destroy(t.Target); } catch { }
            t.Dispose();
        }
    }

    // ---------------------------------------------------------------- Chaque frame (thread principal)

    void Frame()
    {
        try
        {
            ApplyPendingTheme();
            if (!Enabled || !Show)
            {
                // Mod coupé : le HUD du jeu doit réapparaître (on le cherche de nouveau pendant quelques secondes, au cas où il aurait été recréé).
                if (root is not null) Teardown();
                var t0 = Stopwatch.GetTimestamp();
                if (disabledSince == 0) disabledSince = t0;
                if (Stopwatch.GetElapsedTime(disabledSince, t0).TotalSeconds < 6 && t0 >= nextDisabledRefresh)
                {
                    nextDisabledRefresh = t0 + Stopwatch.Frequency;
                    game.Refresh(Log);
                    game.RestoreAll();
                }
                return;
            }
            disabledSince = 0;
            if (needRebuild) { needRebuild = false; Teardown(); }

            game.Refresh(Log);
            var inGame = game.InGame;
            var optionsRow = game.PreviewOpen; // la ligne « HUD theme » de Options > Mods est affichée
            // HUD caché complètement (D-pad bas + L2) : ni nos fenêtres ni celles du jeu ; seule l'aperçu des options reste.
            if (Hidden && !optionsRow)
            {
                ShowRoot(false);
                if (inGame) game.HideOriginals();
                InterfaceUp = false;
                return;
            }
            // Dans les menus : pas de HUD, sauf l'aperçu quand la section « HUD » de Options > Mods est ouverte.
            var preview = !inGame && optionsRow;
            if (!inGame) inGameSince = 0;
            if (!inGame) InterfaceUp = false;
            if (!inGame && !preview)
            {
                if (root is not null) Teardown();
                return;
            }
            if (inGame)
            {
                game.HideOriginals();
                Measure();
            }
            // En jeu, une interface ouverte (/place, menu pause...) cache le HUD, sauf pour choisir le thème dans Options > Mods.
            var ui = inGame && !optionsRow && (InterfaceOpenCached() || game.PlacerOpen);
            InterfaceUp = ui;
            if (ui && HideOnMenus || PlayCamerasMod.EditorHold)   // the camera editor window hides the HUD, which comes back when it closes
            {
                ShowRoot(false);
                return;
            }

            if (root is not null && (Unity.IsNull(root.Target) || leftText is null || rightText is null)) Teardown();
            if (root is null) { rootShown = true; if (!Build()) return; }
            ShowRoot(true);

            var look = HudThemes.Get(Theme);
            var computed = (int)Math.Round(Math.Min(velocity.Magnitude * 3.6f, 999));
            // Valeur du jeu ; si elle reste figée alors que le coureur bouge, on prend la vitesse calculée.
            var g = game.Kmh;
            if (g != lastGameKmh) { lastGameKmh = g; lastGameChange = Stopwatch.GetTimestamp(); }
            var stale = g is null || (Stopwatch.GetElapsedTime(lastGameChange).TotalSeconds > 1 && Math.Abs(computed - g.Value) > 4);
            var nowTicks = Stopwatch.GetTimestamp();
            var dt = lastFrame == 0 ? 0.016f : (float)Stopwatch.GetElapsedTime(lastFrame, nowTicks).TotalSeconds;
            lastFrame = nowTicks;
            // Aperçu dans le menu : la vitesse monte et descend pour montrer les flammes.
            var kmh = preview ? (int)Math.Clamp(80 + 95 * Math.Sin(nowTicks / (double)Stopwatch.Frequency * 0.8), 0, 160) : stale ? computed : g!.Value;
            // Flames: 30 updates a second are enough (each particle costs several game calls).
            flameAcc += dt;
            if (flameAcc >= 1f / 30f)
            {
                flames?.Update(flameAcc, Flames ? Math.Clamp((kmh - FlamesStart) / Math.Max(10f, 150f - FlamesStart), 0, 1) : 0);
                flameAcc = 0;
            }

            var textKey = (Theme, kmh, game.SpawnName, game.Stance, game.RespawnSpeed, preview);
            if (textKey == lastTextKey) return;
            lastTextKey = textKey;
            var right = $"<size=48%><color={look.Label}>SPEED</color></size>\n<color={look.Value}>{kmh}</color>\n<size=45%><color={look.Label}>KM/H</color></size>";
            if (right != shownRight && !Unity.IsNull(rightText!.Target))
            {
                shownRight = right;
                Unity.Call(rightText.Target, "set_text", Il2Cpp.String(right));
            }

            var name = preview ? "SmallJump" : Safe(game.SpawnName, "-");
            var stance = preview ? "Regular" : Safe(game.Stance, "Regular");
            var respawn = preview ? "60 km/h" : Safe(game.RespawnSpeed, "-");
            var left = $"<size=42%><color={look.Label}>SPAWNPOINT</color></size>\n<size=62%><color={look.Value}>{name}</color></size>\n"
                     + $"<size=42%><color={look.Label}>STANCE</color></size>  <size=55%><color={look.Accent}>{stance}</color></size>\n"
                     + $"<size=42%><color={look.Label}>RESPAWN SPEED</color></size>  <size=55%><color={look.Accent}>{respawn}</color></size>";
            if (left != shownLeft && !Unity.IsNull(leftText!.Target))
            {
                shownLeft = left;
                Unity.Call(leftText.Target, "set_text", Il2Cpp.String(left));
            }
        }
        catch (Exception e) { Log("HUD frame: " + e.Message); }
    }

    /// <summary>Le texte du jeu vient de l'interface : « &lt; » est neutralisé pour que TextMeshPro ne le lise pas comme une balise.</summary>
    static string Safe(string s, string fallback) =>
        string.IsNullOrWhiteSpace(s) ? fallback : new string(s.Where(c => !char.IsControl(c)).ToArray()).Replace("<", "‹");

    /// <summary>État du curseur : 0 = caché, 1 = visible, plus 2 par niveau de verrouillage (0 libre, 1 verrouillé, 2 confiné). -1 si illisible.</summary>
    static int ReadCursor()
    {
        try
        {
            var k = Unity.Class("UnityEngine.Cursor");
            var visible = Il2Cpp.Unbox<byte>(Unity.CallStatic(k, "get_visible")) != 0;
            var lockState = Il2Cpp.Unbox<int>(Unity.CallStatic(k, "get_lockState"));
            return (visible ? 1 : 0) + 2 * lockState;
        }
        catch { return -1; }
    }

    /// <summary>
    /// Vrai quand une interface est ouverte en jeu (/place, menu pause...) : le jeu montre alors le curseur.
    /// L'état normal du curseur en jeu est appris tout seul : celui qu'on voit après 4 s de jeu continu.
    /// Les changements d'état sont écrits dans le log (« HUD cursor ») pour vérifier.
    /// </summary>
    int cursorIn; bool cursorCached;
    bool InterfaceOpenCached()
    {
        if (cursorIn-- > 0) return cursorCached;
        cursorIn = 2;
        return cursorCached = InterfaceOpen();
    }

    bool InterfaceOpen()
    {
        var key = ReadCursor();
        if (key < 0) return false;
        if (key != lastCursorKey && cursorLogs < 30)
        {
            lastCursorKey = key;
            cursorLogs++;
            Log($"HUD cursor: visible={(key & 1) != 0}, lock={key >> 1}{(cursorBaseline < 0 ? " (learning)" : cursorBaseline == key ? " (normal)" : " (interface open, HUD hidden)")}");
        }
        var now = Stopwatch.GetTimestamp();
        if (cursorBaseline < 0)
        {
            if (inGameSince == 0) inGameSince = now;
            else if (Stopwatch.GetElapsedTime(inGameSince, now).TotalSeconds >= 4)
            {
                cursorBaseline = key;
                Log($"HUD cursor: normal state in game = visible={(key & 1) != 0}, lock={key >> 1}.");
            }
            return false;
        }
        // Only a cursor that appears where the game normally hides it counts (if the game keeps it visible while playing, this cannot tell and never hides),
        // and only when it stays for a moment, so a short change (respawn, controller) does not make the HUD blink out.
        var open = (key & 1) != 0 && (cursorBaseline & 1) == 0;
        if (!open) { openSince = 0; return false; }
        if (openSince == 0) openSince = now;
        return Stopwatch.GetElapsedTime(openSince, now).TotalSeconds >= 0.3;
    }
    long openSince;

    /// <summary>
    /// Vitesse calculée à partir du déplacement du coureur : on compare sa position à celle d'il y a au moins 0,1 s
    /// (une mesure image par image serait trop bruitée), puis on lisse.
    /// </summary>
    void Measure()
    {
        var rider = RagdollSpawner.First()?.Rider;
        if (rider is not { } r || r.Ptr == 0)
        {
            haveSample = false;
            velocity = new Vector3(0, 0, 0);
            return;
        }

        var now = Stopwatch.GetTimestamp();
        if (haveSample && r.Ptr == lastRiderPtr && Stopwatch.GetElapsedTime(lastSample, now).TotalSeconds < 0.1) return;   // sampled 10 times a second
        var position = Unity.Call<Vector3>(Unity.Transform(r.Ptr), "get_position");
        if (r.Ptr != lastRiderPtr) { lastRiderPtr = r.Ptr; haveSample = false; }
        if (!haveSample)
        {
            (lastPosition, lastSample, haveSample) = (position, now, true);
            velocity = new Vector3(0, 0, 0);
            return;
        }

        var seconds = Stopwatch.GetElapsedTime(lastSample, now).TotalSeconds;
        if (seconds < 0.1) return;

        var delta = position - lastPosition;
        (lastPosition, lastSample) = (position, now);
        var instant = delta * (float)(1 / seconds);
        if (seconds > 1 || instant.Magnitude > 90)
        {
            // Téléportation, respawn ou longue pause : on repart de zéro.
            velocity = new Vector3(0, 0, 0);
            return;
        }
        velocity = new Vector3(velocity.X * 0.5f + instant.X * 0.5f, velocity.Y * 0.5f + instant.Y * 0.5f, velocity.Z * 0.5f + instant.Z * 0.5f);
    }

    // ---------------------------------------------------------------- Construction des deux fenêtres

    bool Build()
    {
        // Pas plus d'un essai par seconde (la police du jeu n'est pas toujours prête au chargement).
        var now = Stopwatch.GetTimestamp();
        if (lastBuildTry != 0 && Stopwatch.GetElapsedTime(lastBuildTry, now).TotalSeconds < 1) return false;
        lastBuildTry = now;

        var font = TextBanner.FindFont();
        if (font == 0) return false;

        OwnedGameObject? canvas = null;
        try
        {
            canvas = OverlayCanvas.BuildRoot("HankModPack.Hud");
            var k = ScalePercent / 100f;
            const float margin = 16;
            var look = HudThemes.Get(Theme);

            leftText = Window(canvas, "Spawn", false, margin, 196 * k, 92 * k, font, look);
            // Les flammes sont créées avant la fenêtre de vitesse : elles sont dessinées derrière elle.
            if (Flames) flames = new SpeedFlames(canvas, margin, 136 * k, 100 * k, k, look);
            rightText = Window(canvas, "Speed", true, margin, 136 * k, 100 * k, font, look);
            root = canvas;
            shownLeft = shownRight = "";
            return true;
        }
        catch (Exception e)
        {
            Log("HUD build failed: " + e.Message);
            canvas?.Dispose();
            Teardown();
            return false;
        }
    }

    /// <summary>
    /// Une fenêtre : une image (forme, ombre, bordure, dégradé) avec le texte posé dessus.
    /// L'inclinaison éventuelle tourne l'image et le texte ensemble. Renvoie le texte.
    /// </summary>
    GameRef Window(OwnedGameObject canvas, string name, bool rightSide, float margin, float w, float h, nint font, Look look)
    {
        var side = rightSide ? 1f : 0f;
        var sign = rightSide ? -1f : 1f; // vers l'intérieur de l'écran
        var pad = HudArt.Padding(look);

        var (rgba, texW, texH) = HudArt.Render(look, w, h, rightSide);
        var tex = HudArt.MakeTexture(rgba, texW, texH);
        textures.Add(new GameRef(tex));

        using var win = OwnedGameObject.Create(name + ".Window");
        var raw = win.AddComponent("UnityEngine.UI.RawImage");
        UnityProperties.Set(raw, "set_raycastTarget", false);
        Unity.Call(raw, "set_texture", tex);
        Anchor(Unity.Call(raw, "get_rectTransform"), side, sign * (margin - pad), margin - pad, w + 2 * pad, h + 2 * pad);
        var tilt = rightSide ? -look.Tilt : look.Tilt;
        if (tilt != 0) UnityProperties.Set(Unity.Call(raw, "get_rectTransform"), "set_localEulerAngles", new Vector3(0, 0, tilt));

        using var child = OwnedGameObject.Create(name + ".Text");
        var tmp = child.AddComponent("TMPro.TextMeshProUGUI");
        Unity.Call(tmp, "set_font", font);
        UnityProperties.Set(tmp, "set_raycastTarget", false);
        UnityProperties.Set(tmp, "set_richText", true);
        UnityProperties.Set(tmp, "set_alignment", Unity.Class("TMPro.TextAlignmentOptions").Field("Center").Get<int>(0));
        UnityProperties.Set(tmp, "set_fontSize", 40f);
        // Le texte se réduit tout seul pour tenir dans la fenêtre (une seule ligne par ligne de texte).
        if (!Il2Cpp.ClassOf(tmp).Method("set_textWrappingMode", 1).IsNull) UnityProperties.Set(tmp, "set_textWrappingMode", 0);
        else UnityProperties.Set(tmp, "set_enableWordWrapping", (byte)0);
        UnityProperties.Set(tmp, "set_enableAutoSizing", (byte)1);
        UnityProperties.Set(tmp, "set_fontSizeMin", 6f);
        UnityProperties.Set(tmp, "set_fontSizeMax", 60f);
        if (look.Bold) UnityProperties.Set(tmp, "set_fontStyle", 1);
        if (HudThemes.NeedsOutline(look)) HudThemes.Outline(tmp);
        var rect = Unity.Call(tmp, "get_rectTransform");
        UnityProperties.Set(rect, "set_anchorMin", new Vector2(0, 0));
        UnityProperties.Set(rect, "set_anchorMax", new Vector2(1, 1));
        UnityProperties.Set(rect, "set_pivot", new Vector2(0.5f, 0.5f));
        UnityProperties.Set(rect, "set_offsetMin", new Vector2(pad + 8, pad + 6));
        UnityProperties.Set(rect, "set_offsetMax", new Vector2(-(pad + 8), -(pad + 6)));
        child.AttachTo(win.Transform);
        Unity.Call(tmp, "set_text", Il2Cpp.String(" "));

        win.AttachTo(canvas.Transform);
        return new GameRef(tmp);
    }

    /// <summary>Ancre en bas à gauche (side 0) ou en bas à droite (side 1) ; le coin du bloc le plus proche du bord sert de pivot.</summary>
    static void Anchor(nint rect, float side, float x, float y, float w, float h)
    {
        var anchor = new Vector2(side, 0);
        UnityProperties.Set(rect, "set_anchorMin", anchor);
        UnityProperties.Set(rect, "set_anchorMax", anchor);
        UnityProperties.Set(rect, "set_pivot", new Vector2(side, 0));
        UnityProperties.Set(rect, "set_anchoredPosition", new Vector2(x, y));
        UnityProperties.Set(rect, "set_sizeDelta", new Vector2(w, h));
    }
}
