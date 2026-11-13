using System.Diagnostics;
using Jibbers.Sdk;
using Jibbers.Sdk.Interop;
using RickBackflip;
using Jibbers.Sdk.Game.Event;
using EnvManager = Jibbers.Sdk.Game.Env.Manager;
using static HankModPack.PadArt;
using Rgba = HankModPack.ButtonArt.Rgba;

namespace HankModPack;

/// <summary>
/// Replaces the game's input display (the F1 overlay with "Left Tuck / Right Grab", the two sticks and the buttons 1-4) with a drawing of a real controller
/// (PlayStation 4 / 5, Xbox 360 / One, white, black or in the colours of a HUD theme) that lights up what is pressed, with the sticks and triggers moving.
/// Without a controller it shows a keyboard (WASD, Shift, Ctrl, Space, arrows) and a mouse instead.
/// </summary>
public sealed unsafe class PadOverlayMod : PackPart
{
    // The quick menu switches the overlay on and off, and the replay editor has its own Input Visualizer tab.
    protected override bool HiddenInModsTab(Setting s) => s.Key is "manual" or "replay_colors" or "replay_scale" or "replay_position";
    protected override string PartId => "pad_overlay";
    public override string Name => "Hank Mod Pack: Input Visualizer";
    public override string Description => "Replaces the game's input display (F1) with a controller or keyboard and mouse that light up while you play. PlayStation 4 / 5 and Xbox 360 / One, white, black or in the colours of the HUD theme.";

    [Choice("Show", "With the game's input display (F1)", "With the game's input display (F1)", "Always", "Off", Key = "show",
        Tooltip = "The overlay appears when you turn the game's own input display on with F1, and replaces it. 'Always' shows it all the time.")]
    public string ShowMode { get; private set; } = "With the game's input display (F1)";

    [Toggle("Show the overlay", false, Key = "manual", Tooltip = "Shows the overlay without the game's own input display. Also switched from the quick menu (Controller overlay on / off).")]
    public bool Manual { get; private set; }

    [Choice("Controller", "Auto", "Auto", "PlayStation 4", "Xbox One", Key = "pad", Tooltip = "Auto: PlayStation 4 for a PlayStation pad, Xbox One for the others.")]
    public string PadKind { get; private set; } = "Auto";

    [Choice("Input shown", "Auto", "Auto", "Controller", "Keyboard and mouse", Key = "input", Tooltip = "Auto: the controller when one is connected, otherwise the keyboard and mouse.")]
    public string InputMode { get; private set; } = "Auto";

    [Choice("Colours", "Black", "White", "Black", "HUD theme", Key = "colors", Tooltip = "HUD theme: colours, border and shape of the theme chosen in Settings (Quick menu > Settings > Theme).")]
    public string Colors { get; private set; } = "Black";

    [Choice("Position", "Bottom centre", "Bottom centre", "Bottom left", "Bottom right", "Top centre", "Top left", "Top right", Key = "position")]
    public string Position { get; private set; } = "Bottom centre";

    [Slider("Size (%)", 100, 40, 200, 5, Key = "scale")]
    public float ScalePercent { get; private set; } = 100;

    [Slider("Opacity (%)", 100, 30, 100, 5, Key = "opacity")]
    public float OpacityPercent { get; private set; } = 100;

    [Choice("Replay: colours", "Same as in game", "Same as in game", "White", "Black", "HUD theme", Key = "replay_colors", Tooltip = "Colours used in the replay. They do not change the game's settings: back in game, everything is as before.")]
    public string ReplayColors { get; private set; } = "Same as in game";

    [Slider("Replay: size (%)", 100, 40, 200, 5, Key = "replay_scale", Tooltip = "Size in the replay only.")]
    public float ReplayScale { get; private set; } = 100;

    [Choice("Replay: position", "Same as in game", "Same as in game", "Bottom centre", "Bottom left", "Bottom right", "Top centre", "Top left", "Top right", Key = "replay_position", Tooltip = "Position in the replay only.")]
    public string ReplayPosition { get; private set; } = "Same as in game";

    [Toggle("Hide the game's own input display", true, Key = "hide_game", Tooltip = "Hides the game's buttons (Left Tuck, Right Grab, sticks, 1-4) so only this overlay shows.")]
    public bool HideGame { get; private set; } = true;

    // ---------------------------------------------------------------- State

    sealed class View
    {
        public Part Part = null!; public GameRef Raw = null!, Rect = null!;
        public float X, Y, W, H;                       // top-left inside the base picture, scaled
        public Rgba LastColor; public float LastA = -1, LastX = float.NaN, LastY = float.NaN, LastF = -1;
        public bool Lit;
    }

    OwnedGameObject? root;
    readonly List<GameRef> textures = new();
    readonly List<View> views = new();
    readonly List<(GameRef Tmp, string Part, Label Label)> labels = new();
    string signature = "";
    bool rootShown = true;
    float k = 1, opacity = 1;
    bool padMode;
    Pal pal = White();
    long lastTry, nextScan, lastFrame;

    UiVisibility ui = new();
    GameRef? gameRoot;
    nint gameTr;
    bool gameWasOn, gameLogged;

    readonly bool[] connected = new bool[4];
    int frame;
    bool anyPad;
    float dotX, dotY, lastMx = float.NaN, lastMy = float.NaN;

    internal static PadOverlayMod? Instance;
    bool InReplayNow => HankModPack.CameraOwnership.InReplay;
    // What the replay editor changes (Input Visualizer tab) lives only until the replay ends: back in game, everything is as it was.
    string? rpColors, rpPosition; float? rpScale;
    string EffColors => InReplayNow ? rpColors ?? (ReplayColors != "Same as in game" ? ReplayColors : Colors) : Colors;
    string EffPosition => InReplayNow ? rpPosition ?? (ReplayPosition != "Same as in game" ? ReplayPosition : Position) : Position;
    float EffScale => InReplayNow ? rpScale ?? ReplayScale : ScalePercent;
    static bool Themed(string c) => c == "HUD theme" || c.StartsWith("Theme: ");

    internal bool ReplayShown => replayOn;
    internal void SetReplayShown(bool on) { replayOn = on; over = null; }
    internal string ReplayColorsNow => EffColors;
    internal void SetReplayColors(string c) => rpColors = c;
    internal float ReplayScaleNow => EffScale;
    internal void SetReplayScale(float s) => rpScale = s;
    internal string ReplayPositionNow => EffPosition;
    internal void SetReplayPosition(string p) => rpPosition = p;
    internal static readonly string[] ColourChoices = new[] { "White", "Black", "HUD theme" }.Concat(HudThemes.Names.Where(n => !n.EndsWith("(no background)") && n != "Text only").Select(n => "Theme: " + n)).ToArray();
    internal static readonly string[] PositionChoices = { "Bottom centre", "Bottom left", "Bottom right", "Top centre", "Top left", "Top right" };

    bool replayOn;
    long quiet;
    bool lastWant, wantBeforeReplay;             // what the overlay was doing in the game before a replay: it comes back to that afterwards
    bool? over;                                  // the quick menu's choice, until the game's own display is switched
    /// <summary>Whether the overlay is on screen right now.</summary>
    internal bool Visible => root is not null && rootShown && !Unity.IsNull(root.Target);

    /// <summary>Quick menu: turns the overlay on or off, whatever the game's own input display (F1) is doing.</summary>
    internal void ToggleManual() { over = !Visible; Log("Controller overlay: " + (over == true ? "switched on" : "switched off") + " from the quick menu"); }

    public override void OnLoad()
    {
        Instance = this;
        ImportPackSettings();
        // The replay's own Input Visualizer button raises this event each time it is pressed: we follow it (and replace the game's display).
        ToggleReplayInputVisualizer.Subscribe(_ => MainThread.Run(() => { replayOn = !replayOn; over = null; Log("Controller overlay: the replay's input visualizer is now " + (replayOn ? "on" : "off")); }));
        EnterReplay.Subscribe(_ => MainThread.Run(() => { wantBeforeReplay = lastWant; over = null; quiet = Environment.TickCount64 + 4000; replayOn = false; rpColors = rpPosition = null; rpScale = null; }));
        ExitReplay.Subscribe(_ => MainThread.Run(() => { over = wantBeforeReplay; quiet = Environment.TickCount64 + 4000; replayOn = false; rpColors = rpPosition = null; rpScale = null; }));
        ChangeScene.Subscribe(_ => MainThread.Run(() => { quiet = Environment.TickCount64 + 4000; replayOn = false; rpColors = rpPosition = null; rpScale = null; }));
        EnvManager.UpdateMethod.Before((_, _) => Frame());
        Log("Controller overlay ready.");
    }

    protected override void OnDeactivated() { Teardown(); RestoreGame(); }
    protected override void OnPackUnload()
    {
        try { if (MainThread.IsCurrent) { Teardown(); RestoreGame(); } else MainThread.Run(() => { Teardown(); RestoreGame(); }); }
        catch (Exception e) { Log("Unload: " + e.Message); }
    }

    protected override void OnPackSettingChanged(string key, System.Text.Json.Nodes.JsonNode value)
    {
        if (key == "hide_game") MainThread.Later(() => { if (!HideGame) { ui.Dispose(); ui = new(); } });
    }

    void RestoreGame()
    {
        ui.Dispose(); ui = new();
        if (gameRoot is not null) { gameRoot.Dispose(); gameRoot = null; }
        gameWasOn = gameLogged = false;
    }

    // ---------------------------------------------------------------- The game's own input display

    /// <summary>The game's input display: the root that holds all its button and stick visualizers (found through their classes, not their texts).</summary>
    readonly List<nint> visTr = new();   // the game's visualizers themselves, when their common root cannot be used

    void ScanGame()
    {
        if (gameRoot is not null && Unity.IsNull(gameRoot.Target)) RestoreGame();
        var now = Stopwatch.GetTimestamp();
        if (now < nextScan) return;
        nextScan = now + Stopwatch.Frequency;
        try
        {
            var all = Jibbers.Interop.Game.InputButtonVisualizer.Live().Select(x => x.Ptr)
                .Concat(Jibbers.Interop.Game.InputStickVisualizer.Live().Select(x => x.Ptr)).Where(p => p != 0 && !Unity.IsNull(p)).ToList();
            visTr.Clear();
            if (all.Count == 0) { if (!gameLogged) { Log("Controller overlay: no game input display found yet (it is looked for every second)"); gameLogged = true; } return; }
            visTr.AddRange(all.Select(Unity.Transform).Where(t => t != 0));
            List<nint> Chain(nint obj)
            {
                var l = new List<nint>(); var tr = Unity.Transform(obj);
                for (var i = 0; i < 20 && tr != 0 && !Unity.IsNull(tr); i++) { l.Add(tr); tr = Unity.Call(tr, "get_parent"); }
                return l;
            }
            var chains = all.Select(Chain).ToList();
            var common = chains[0].FirstOrDefault(t => chains.All(c => c.Contains(t)));
            var count = common == 0 ? -1 : Unity.GetComponentsInChildren(common, Unity.Class("UnityEngine.RectTransform"), true).Length;
            if (common == 0 || count > 60)
            {
                // No small common root (the replay's display sits in a bigger panel): each visualizer is hidden on its own.
                if (!gameLogged) { Log($"Controller overlay: the game's input display has {(common == 0 ? "no common root" : count + " objects")}: hiding its {visTr.Count} visualizers one by one"); gameLogged = true; }
                return;
            }
            if (gameRoot is not null && gameTr == common) return;
            if (gameRoot is not null) RestoreGame();   // the replay has its own display: the root changed
            gameRoot = new GameRef(common); gameTr = common;
            Log($"Controller overlay: found the game's input display: '{Unity.Name(common)}' ({all.Count} visualizers, {count} objects)");
        }
        catch (Exception e) { if (!gameLogged) { Log("Controller overlay: could not look for the game's input display: " + e.Message); gameLogged = true; } }
    }

    bool GameOn()
    {
        try
        {
            if (gameRoot is not null && !Unity.IsNull(gameRoot.Target))
            {
                var go = Unity.Call(gameTr, "get_gameObject");
                return go != 0 && Unity.Call<byte>(go, "get_activeInHierarchy") != 0;
            }
            foreach (var t in visTr)
            {
                if (Unity.IsNull(t)) continue;
                var go = Unity.Call(t, "get_gameObject");
                if (go != 0 && Unity.Call<byte>(go, "get_activeInHierarchy") != 0) return true;
            }
        }
        catch { }
        return false;
    }

    void HideGameDisplay()
    {
        if (gameRoot is not null) ui.Hide(gameTr);
        foreach (var t in visTr) if (!Unity.IsNull(t)) ui.Hide(t);
    }

    // ---------------------------------------------------------------- Each frame

    void Frame()
    {
        try
        {
            if (!Enabled || ShowMode == "Off") { if (root is not null) Teardown(); if (gameRoot is not null || ui.Count > 0) RestoreGame(); return; }
            frame++;
            ScanGame();
            var on = GameOn();
            if (on != gameWasOn) { gameWasOn = on; if (Environment.TickCount64 > quiet) over = null;   // the game flips its own display when a replay starts or ends: that is not the player choosing
                Log("Controller overlay: the game's input display is now " + (on ? "on" : "off")); }
            if (HideGame) HideGameDisplay();
            var want = over ?? (ShowMode == "Always" || Manual || on || (InReplayNow && replayOn));
            if (!InReplayNow) lastWant = want;
            if (!want) { if (root is not null) ShowRoot(false); return; }

            ReadInput(out var pad);
            var wantPad = InputMode == "Controller" || (InputMode == "Auto" && anyPad);
            var look = EffColors == "HUD theme" ? HankHudMod.Instance?.CurrentLook : EffColors.StartsWith("Theme: ") ? HudThemes.Get(EffColors[7..]) : null;
            var sig = $"{wantPad}|{PadKind}|{EffColors}|{look?.Name}|{EffScale}|{OpacityPercent}|{EffPosition}";
            if (root is null || sig != signature || Unity.IsNull(root.Target))
            {
                var now = Stopwatch.GetTimestamp();
                if (lastTry != 0 && Stopwatch.GetElapsedTime(lastTry, now).TotalSeconds < 0.3) { if (root is not null) Update(pad); return; }
                // The drawing is made in the background (and kept): changing the size, position or opacity never redraws it,
                // and a new theme appears when it is ready while the old one stays on screen.
                var kind = KindNow();
                var artKey = $"{wantPad}|{kind}|{EffColors}|{look?.Name}";
                if (!artCache.TryGetValue(artKey, out var art))
                {
                    if (artTask is null || artTaskKey != artKey)
                    {
                        var l = look is not null && look.NoBg ? HudThemes.WithWindow(look) : look;
                        var colors = EffColors;
                        var p = colors == "White" ? White() : Themed(colors) && l is not null ? FromLook(l) : Black();
                        var wp = wantPad;
                        artTaskKey = artKey; artPal = p;
                        artTask = Task.Run(() => wp ? Controller(kind, p, false) : KeyboardMouse(p, false));
                    }
                    if (!artTask.IsCompleted) { if (root is not null) Update(pad); return; }
                    if (artTask.IsFaulted) { Log("Controller overlay: could not draw: " + artTask.Exception?.GetBaseException().Message); artTask = null; lastTry = now; return; }
                    art = artTask.Result; artCache[artKey] = art; artKeyPal[artKey] = artPal; artTask = null;
                    if (artCache.Count > 8) { var drop = artCache.Keys.First(); artCache.Remove(drop); artKeyPal.Remove(drop); }
                }
                lastTry = now;
                Build(wantPad, art, artKeyPal[artKey], sig);
                if (root is null) return;
            }
            ShowRoot(true);
            Update(pad);
        }
        catch (Exception e) { if (frame % 600 == 1) Log("Controller overlay: " + e.Message); }
    }

    unsafe void ShowRoot(bool shown)
    {
        if (rootShown == shown || root is null || Unity.IsNull(root.Target)) return;
        Unity.Call(root.Target, "SetActive", (nint)(&shown));
        rootShown = shown;
    }

    void ReadInput(out PadState pad)
    {
        pad = default;
        if (InReplayNow) { ReadReplay(out pad); return; }
        var probe = frame % 120 == 1;
        anyPad = false;
        for (var slot = 0; slot < connected.Length; slot++)
        {
            if (!connected[slot] && !probe) continue;
            var p = Gamepad.Read(slot);
            connected[slot] = p.Connected;
            if (!p.Connected) continue;
            anyPad = true;
            if (!pad.Connected) pad = p;
        }
        if (!pad.Connected) return;
        var d = pad.WithDeadzone(0.12f);
        pad = pad with { LeftX = d.LeftX, LeftY = d.LeftY, RightX = d.RightX, RightY = d.RightY, LeftTrigger = Math.Max(d.LeftTrigger, pad.LeftTrigger), RightTrigger = Math.Max(d.RightTrigger, pad.RightTrigger) };
    }


    // ---------------------------------------------------------------- Replay: what the replayed rider does (read from the game's own input display)

    sealed record BtnV(nint Ptr, nint Fill, Color Active, int Action, bool Rag, bool Tuck, float X);
    sealed record StickV(nint Ptr, nint[] Kids, float X) { public readonly float[] Range = new float[8]; }
    List<BtnV> gBtns = new(); List<StickV> gSticks = new();
    long nextVis;

    // Only the field names that do not change between game versions are used (the others are obfuscated).
    void RefreshVisualizers()
    {
        var now = Stopwatch.GetTimestamp();
        if (now < nextVis) return;
        nextVis = now + Stopwatch.Frequency;
        try
        {
            gBtns = Jibbers.Interop.Game.InputButtonVisualizer.Live().Where(v => v.Ptr != 0 && !Unity.IsNull(v.Ptr))
                .Select(v => new BtnV(v.Ptr, v.fill, v.activeColor, v.actionId, v.useRagdollState, v.ragdollStateTuck, Unity.Call<Vector3>(Unity.Transform(v.Ptr), "get_position").X)).ToList();
            var rect = Unity.Class("UnityEngine.RectTransform");
            gSticks = Jibbers.Interop.Game.InputStickVisualizer.Live().Where(v => v.Ptr != 0 && !Unity.IsNull(v.Ptr))
                .Select(v => new StickV(v.Ptr, Unity.GetComponentsInChildren(v.Ptr, rect, true).Where(r => r != 0 && r != Unity.Transform(v.Ptr)).Take(8).ToArray(),
                    Unity.Call<Vector3>(Unity.Transform(v.Ptr), "get_position").X)).ToList();
        }
        catch (Exception e) { if (frame % 600 == 1) Log("Controller overlay: replay inputs: " + e.Message); }
    }

    float Pressed(BtnV b)
    {
        try
        {
            if (b.Fill == 0 || Unity.IsNull(b.Fill)) return 0;
            if (Unity.Call<int>(b.Fill, "get_type") == 3) return Math.Clamp(Unity.Call<float>(b.Fill, "get_fillAmount"), 0, 1);   // an Image drawn as a filled bar
            var c = Unity.Call<Color>(b.Fill, "get_color");
            var d = Math.Abs(c.R - b.Active.R) + Math.Abs(c.G - b.Active.G) + Math.Abs(c.B - b.Active.B);
            return d < 0.15f && c.A > 0.3f ? 1 : 0;
        }
        catch { return 0; }
    }

    (float X, float Y) StickValue(StickV s)
    {
        try
        {
            // The thumb is the child that moves: the one with the widest travel seen so far.
            var best = -1; var bx = 0f; var by = 0f;
            for (var i = 0; i < s.Kids.Length; i++)
            {
                if (Unity.IsNull(s.Kids[i])) continue;
                var o = Unity.Call<Vector2>(s.Kids[i], "get_anchoredPosition");
                s.Range[i] = Math.Max(s.Range[i], Math.Max(Math.Abs(o.X), Math.Abs(o.Y)));
                if (best < 0 || s.Range[i] > s.Range[best]) { best = i; bx = o.X; by = o.Y; }
                else if (i == best) { bx = o.X; by = o.Y; }
            }
            if (best < 0) return (0, 0);
            var o2 = Unity.Call<Vector2>(s.Kids[best], "get_anchoredPosition");
            var r = Math.Max(s.Range[best], 8f);
            return (Math.Clamp(o2.X / r, -1, 1), Math.Clamp(o2.Y / r, -1, 1));
        }
        catch { return (0, 0); }
    }

    /// <summary>In a replay the pad drawn is the replayed rider's: the live pad would show what you do, not what happened.</summary>
    void ReadReplay(out PadState pad)
    {
        anyPad = true;
        RefreshVisualizers();
        var buttons = PadButton.None; float lt = 0, rt = 0, lx = 0, ly = 0, rx = 0, ry = 0;
        var tuck = gBtns.Where(b => b.Rag && b.Tuck).OrderBy(b => b.X).ToList();
        var grab = gBtns.Where(b => b.Rag && !b.Tuck).OrderBy(b => b.X).ToList();
        var face = gBtns.Where(b => !b.Rag).OrderBy(b => b.Action).ThenBy(b => b.X).ToList();
        if (tuck.Count > 0 && Pressed(tuck[0]) > 0.5f) buttons |= PadButton.LeftShoulder;
        if (tuck.Count > 1 && Pressed(tuck[1]) > 0.5f) buttons |= PadButton.RightShoulder;
        if (grab.Count > 0) lt = Pressed(grab[0]);
        if (grab.Count > 1) rt = Pressed(grab[1]);
        var faceBtns = new[] { PadButton.A, PadButton.B, PadButton.X, PadButton.Y };
        for (var i = 0; i < face.Count && i < 4; i++) if (Pressed(face[i]) > 0.5f) buttons |= faceBtns[i];
        var sticks = gSticks.OrderBy(s => s.X).ToList();
        if (sticks.Count > 0) (lx, ly) = StickValue(sticks[0]);
        if (sticks.Count > 1) (rx, ry) = StickValue(sticks[1]);
        pad = new PadState(true, 0, lx, ly, rx, ry, lt, rt, buttons);
    }

    // ---------------------------------------------------------------- Building

    PadArt.Kind KindNow()
    {
        if (PadKind == "PlayStation 4") return PadArt.Kind.Ps4;
        if (PadKind == "Xbox One") return PadArt.Kind.XOne;
        try { return SonyPad.Find().Count > 0 ? PadArt.Kind.Ps4 : PadArt.Kind.XOne; } catch { return PadArt.Kind.XOne; }
    }

    readonly Dictionary<string, PadArt.Art> artCache = new();
    readonly Dictionary<string, Pal> artKeyPal = new();
    Task<PadArt.Art>? artTask; string artTaskKey = ""; Pal artPal = White();

    unsafe void Build(bool wantPad, PadArt.Art art, Pal palette, string sig)
    {
        var font = TextBanner.FindFont();
        if (font == 0) return;
        Teardown();
        OwnedGameObject? canvas = null;
        try
        {
            pal = palette;
            padMode = wantPad;
            k = 0.85f * EffScale / 100f;
            opacity = Math.Clamp(OpacityPercent / 100f, 0.3f, 1f);

            canvas = OverlayCanvas.BuildRoot("HankModPack.PadOverlay", 30000);   // above the replay's timeline and buttons
            using var baseGo = OwnedGameObject.Create("Pad");
            var raw = baseGo.AddComponent("UnityEngine.UI.RawImage");
            UnityProperties.Set(raw, "set_raycastTarget", false);
            Unity.Call(raw, "set_texture", Own(HudArt.MakeTexture(art.Base, art.PxW, art.PxH)));
            UnityProperties.Set(raw, "set_color", new Color(1, 1, 1, opacity));
            var rect = Unity.Call(raw, "get_rectTransform");
            var (ax, ay) = EffPosition switch
            {
                "Bottom left" => (0f, 0f), "Bottom right" => (1f, 0f), "Top centre" => (0.5f, 1f), "Top left" => (0f, 1f), "Top right" => (1f, 1f), _ => (0.5f, 0f),
            };
            var anchor = new Vector2(ax, ay);
            UnityProperties.Set(rect, "set_anchorMin", anchor);
            UnityProperties.Set(rect, "set_anchorMax", anchor);
            UnityProperties.Set(rect, "set_pivot", anchor);
            UnityProperties.Set(rect, "set_anchoredPosition", new Vector2(ax == 0 ? 16 : ax == 1 ? -16 : 0, ay == 0 ? 20 : -20));
            UnityProperties.Set(rect, "set_sizeDelta", new Vector2(art.W * k, art.H * k));
            baseGo.AttachTo(canvas.Transform);

            foreach (var part in art.Parts)
            {
                using var go = OwnedGameObject.Create(part.Name);
                var r = go.AddComponent("UnityEngine.UI.RawImage");
                UnityProperties.Set(r, "set_raycastTarget", false);
                Unity.Call(r, "set_texture", Own(HudArt.MakeTexture(part.Rgba, part.PxW, part.PxH)));
                var rt = Unity.Call(r, "get_rectTransform");
                var v = new View { Part = part, Raw = new GameRef(r), Rect = new GameRef(rt),
                    X = (art.OffsetX + part.Cx - part.W / 2) * k, Y = (art.OffsetY + part.Cy - part.H / 2) * k, W = part.W * k, H = part.H * k };
                Place(rt, v.X, v.Y, v.W, v.H);
                UnityProperties.Set(r, "set_color", new Color(1, 1, 1, 0));
                go.AttachTo(baseGo.Transform);
                views.Add(v);
            }
            foreach (var l in art.Labels)
            {
                var tmp = Label(baseGo, l.Text, font, l.Size * k, (art.OffsetX + l.Cx - 30) * k, (art.OffsetY + l.Cy - 15) * k, 60 * k, 30 * k);
                labels.Add((new GameRef(tmp), l.Part, l));
            }
            root = canvas; signature = sig; rootShown = true;
            Log($"Controller overlay: built ({(wantPad ? "controller, " + PadKind : "keyboard and mouse")}, {EffColors}{(InReplayNow ? ", replay" : "")})");
        }
        catch (Exception e)
        {
            Log("Controller overlay: build failed: " + e.Message);
            canvas?.Dispose();
            Teardown();
        }
    }

    nint Own(nint tex) { textures.Add(new GameRef(tex)); return tex; }

    void Teardown()
    {
        var r = root; root = null; signature = "";
        foreach (var v in views) { v.Raw.Dispose(); v.Rect.Dispose(); }
        foreach (var l in labels) l.Tmp.Dispose();
        views.Clear(); labels.Clear();
        r?.Dispose();
        foreach (var t in textures)
        {
            try { if (!Unity.IsNull(t.Target)) Unity.Destroy(t.Target); } catch { }
            t.Dispose();
        }
        textures.Clear();
        rootShown = true;
    }

    static void Place(nint rect, float x, float y, float w, float h)
    {
        var anchor = new Vector2(0, 1);
        UnityProperties.Set(rect, "set_anchorMin", anchor);
        UnityProperties.Set(rect, "set_anchorMax", anchor);
        UnityProperties.Set(rect, "set_pivot", anchor);
        UnityProperties.Set(rect, "set_anchoredPosition", new Vector2(x, -y));
        UnityProperties.Set(rect, "set_sizeDelta", new Vector2(w, h));
    }

    static nint Label(OwnedGameObject parent, string text, nint font, float size, float x, float y, float w, float h)
    {
        using var go = OwnedGameObject.Create("Label");
        var tmp = go.AddComponent("TMPro.TextMeshProUGUI");
        Unity.Call(tmp, "set_font", font);
        UnityProperties.Set(tmp, "set_raycastTarget", false);
        UnityProperties.Set(tmp, "set_richText", false);
        var options = Unity.Class("TMPro.TextAlignmentOptions");
        var field = options.Field("Midline");
        UnityProperties.Set(tmp, "set_alignment", field.IsNull ? options.Field("Center").Get<int>(0) : field.Get<int>(0));
        UnityProperties.Set(tmp, "set_fontSize", size);
        UnityProperties.Set(tmp, "set_fontStyle", 1);
        if (!Il2Cpp.ClassOf(tmp).Method("set_textWrappingMode", 1).IsNull) UnityProperties.Set(tmp, "set_textWrappingMode", 0);
        else UnityProperties.Set(tmp, "set_enableWordWrapping", (byte)0);
        Place(Unity.Call(tmp, "get_rectTransform"), x, y, w, h);
        go.AttachTo(parent.Transform);
        Unity.Call(tmp, "set_text", Il2Cpp.String(text));
        return tmp;
    }

    // ---------------------------------------------------------------- What is pressed

    bool PressedPad(string name, PadState pad) => (name.Length == 2 && name[1] == 'g' ? name[..1] : name) switch
    {
        "A" => pad.Held(PadButton.A), "B" => pad.Held(PadButton.B), "X" => pad.Held(PadButton.X), "Y" => pad.Held(PadButton.Y),
        "LB" => pad.Held(PadButton.LeftShoulder), "RB" => pad.Held(PadButton.RightShoulder),
        "Up" => pad.Held(PadButton.DpadUp), "Down" => pad.Held(PadButton.DpadDown), "Left" => pad.Held(PadButton.DpadLeft), "Right" => pad.Held(PadButton.DpadRight),
        "Back" => pad.Held(PadButton.Back), "Start" => pad.Held(PadButton.Start),
        "LCap" => pad.Held(PadButton.LeftStick), "RCap" => pad.Held(PadButton.RightStick),
        _ => false,
    };

    static Rgba Mix(Rgba a, Rgba b, float t) => new(a.R + (b.R - a.R) * t, a.G + (b.G - a.G) * t, a.B + (b.B - a.B) * t, 1f);

    Rgba CapColor()
    {
        var s = pal.Stick; var b = pal.Body;
        return new Rgba(s.R * s.A + b.R * (1 - s.A), s.G * s.A + b.G * (1 - s.A), s.B * s.A + b.B * (1 - s.A), 1f);
    }

    static Color C(Rgba c, float aMul = 1f) => new(c.R, c.G, c.B, c.A * aMul);

    void Update(PadState pad)
    {
        var focused = Input.GameFocused;
        float mx = 0, my = 0;
        if (!padMode)
        {
            if (Pointer.Position(out var px, out var py))
            {
                if (!float.IsNaN(lastMx)) { mx = px - lastMx; my = py - lastMy; }
                lastMx = px; lastMy = py;
            }
            var tx = Math.Clamp(mx * 0.7f, -9, 9); var ty = Math.Clamp(-my * 0.7f, -9, 9);
            dotX += (tx - dotX) * 0.35f; dotY += (ty - dotY) * 0.35f;
        }

        foreach (var v in views)
        {
            var p = v.Part;
            bool lit;
            if (padMode) lit = PressedPad(p.Name, pad);
            else if (p.Name.StartsWith('K')) lit = focused && Input.IsDown(int.Parse(p.Name[1..]));
            else lit = focused && p.Name switch { "ML" => Input.IsDown(1), "MR" => Input.IsDown(2), "MM" => Input.IsDown(4), _ => false };

            switch (p.Kind)
            {
                case PartKind.Lit:
                    if (lit != v.Lit || v.LastA < 0) { v.Lit = lit; UnityProperties.Set(v.Raw.Target, "set_color", C(p.Name.Length == 2 && p.Name[1] == 'g' ? pal.LitText : pal.Lit, (lit ? 0.88f : 0f) * opacity)); v.LastA = 1; }
                    break;
                case PartKind.Cap:
                    {
                        var sx = p.Name[0] == 'L' ? pad.LeftX : pad.RightX; var sy = p.Name[0] == 'L' ? pad.LeftY : pad.RightY;
                        var x = v.X + sx * 12 * k; var y = v.Y - sy * 12 * k;
                        if (x != v.LastX || y != v.LastY) { v.LastX = x; v.LastY = y; UnityProperties.Set(v.Rect.Target, "set_anchoredPosition", new Vector2(x, -y)); }
                        if (lit != v.Lit || v.LastA < 0) { v.Lit = lit; UnityProperties.Set(v.Raw.Target, "set_color", lit ? C(Mix(pal.Lit, new Rgba(1, 1, 1, 1), 0.35f), opacity) : new Color(1, 1, 1, opacity)); v.LastA = 1; }
                        break;
                    }
                case PartKind.Fill:
                    {
                        var f = Math.Clamp(p.Name == "LT" ? pad.LeftTrigger : pad.RightTrigger, 0, 1);
                        if (Math.Abs(f - v.LastF) > 0.01f || v.LastA < 0)
                        {
                            v.LastF = f; v.LastA = 1;
                            UnityProperties.Set(v.Raw.Target, "set_color", C(pal.Lit, (f > 0.02f ? 0.9f : 0f) * opacity));
                            var hh = v.H * Math.Max(f, 0.001f);
                            UnityProperties.Set(v.Rect.Target, "set_anchoredPosition", new Vector2(v.X, -(v.Y + v.H - hh)));
                            UnityProperties.Set(v.Rect.Target, "set_sizeDelta", new Vector2(v.W, hh));
                        }
                        break;
                    }
                case PartKind.Dot:
                    {
                        var x = v.X + dotX * k; var y = v.Y + dotY * k;
                        if (x != v.LastX || y != v.LastY) { v.LastX = x; v.LastY = y; UnityProperties.Set(v.Rect.Target, "set_anchoredPosition", new Vector2(x, -y)); }
                        if (v.LastA < 0) { v.LastA = 1; UnityProperties.Set(v.Raw.Target, "set_color", C(pal.Lit, 0.9f * opacity)); }
                        break;
                    }
            }
        }
        // Letters on lit buttons / keys change colour so they stay readable.
        for (var i = 0; i < labels.Count; i++)
        {
            var (tmp, part, _) = labels[i];
            var v = views.FirstOrDefault(x => x.Part.Name == part);
            var lit = v?.Lit ?? false;
            var col = LabelColor(part, lit);
            if (labelLast.Count <= i) labelLast.Add(-1);
            var key = lit ? 1 : 0;
            if (labelLast[i] != key) { labelLast[i] = key; UnityProperties.Set(tmp.Target, "set_color", col); }
        }
    }
    readonly List<int> labelLast = new();

    Color LabelColor(string part, bool lit)
    {
        if (!padMode || part.StartsWith('K')) return C(lit ? pal.LitText : pal.Text, opacity);
        // Xbox face buttons keep their own colour.
        var c = part switch { "A" => Rgba.Hex(0x3DBB4A), "B" => Rgba.Hex(0xE5333F), "X" => Rgba.Hex(0x2F7FE0), _ => Rgba.Hex(0xF4C20D) };
        return lit ? C(pal.LitText, opacity) : C(c, opacity);
    }
}
