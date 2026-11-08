using System.Diagnostics;
using System.Text.Json.Nodes;
using Jibbers.Sdk;
using Jibbers.Sdk.Interop;
using RickBackflip;

namespace HankModPack;

/// <summary>
/// The quick menu: while D-pad down is held (the free look), a panel on the left shows what each button does.
/// Buttons: look behind, camera list (POV / Follow cam / Drone / game camera), spawn menu, Fun on-off, Overlay on-off.
/// After a choice the panel stays 1.5 s so the change can be seen, then goes away.
/// </summary>
public sealed unsafe partial class LookAroundMod
{
    // ---------------------------------------------------------------- Settings (they live in the Quick Menu mod)

    static QuickMenuMod Q => QuickMenuMod.Instance!;
    bool EditorFreeze => Q.EditorFreeze;
    bool ShowMenu => Q.Enabled && Q.ShowMenu;
    string IconStyle => Q.IconStyle;
    float MenuScale => Q.MenuScale;
    float MenuOpacity => Q.MenuOpacity;
    float MenuLinger => Q.MenuLinger;
    string RearButtonName => Q.RearButtonName;
    string CameraButtonName => Q.CameraButtonName;
    string SpawnButtonName => Q.SpawnButtonName;
    string FunButtonName => Q.FunButtonName;
    string OverlayButtonName => Q.OverlayButtonName;
    string PovButtonName => Q.PovButtonName;
    string FollowButtonName => Q.FollowButtonName;
    string DroneButtonName => Q.DroneButtonName;
    string DefaultButtonName => Q.DefaultButtonName;
    string DirectPovName => Q.DirectPovName;
    string DirectFollowName => Q.DirectFollowName;
    string DirectDroneName => Q.DirectDroneName;
    string DirectGameName => Q.DirectGameName;
    string HudPrevName => Q.HudPrevName;
    string HudNextName => Q.HudNextName;
    string HudButtonName => Q.HudButtonName;
    string EditorButtonName => Q.EditorButtonName;
    string ModsButtonName => Q.ModsButtonName;

    /// <summary>The free look zoom uses L1 / R1 too: while the menu is shown, a button that changes the HUD does not zoom.</summary>
    bool ZoomBlocked(PadBtn b) => ShowMenu && (Parse(HudPrevName) == b || Parse(HudNextName) == b);

    // ---------------------------------------------------------------- Buttons

    static readonly Dictionary<string, PadBtn> ButtonByName = new()
    {
        ["A / Cross"] = PadBtn.A, ["B / Circle"] = PadBtn.B, ["X / Square"] = PadBtn.X, ["Y / Triangle"] = PadBtn.Y,
        ["LB / L1"] = PadBtn.LB, ["RB / R1"] = PadBtn.RB, ["LT / L2"] = PadBtn.LT, ["RT / R2"] = PadBtn.RT,
        ["LSB / L3"] = PadBtn.L3, ["RSB / R3"] = PadBtn.R3, ["View / Share"] = PadBtn.Back, ["Menu / Options"] = PadBtn.Start,
    };

    static readonly PadBtn[] Mappable =
        { PadBtn.A, PadBtn.B, PadBtn.X, PadBtn.Y, PadBtn.LB, PadBtn.RB, PadBtn.LT, PadBtn.RT, PadBtn.L3, PadBtn.R3, PadBtn.Back, PadBtn.Start };

    static PadBtn? Parse(string name) => ButtonByName.TryGetValue(name, out var b) ? b : null;

    PadBtn? RearButton => Parse(RearButtonName);

    static PadButton ToPad(PadBtn b) => b switch
    {
        PadBtn.A => PadButton.A, PadBtn.B => PadButton.B, PadBtn.X => PadButton.X, PadBtn.Y => PadButton.Y,
        PadBtn.LB => PadButton.LeftShoulder, PadBtn.RB => PadButton.RightShoulder, PadBtn.L3 => PadButton.LeftStick, PadBtn.R3 => PadButton.RightStick,
        PadBtn.Back => PadButton.Back, PadBtn.Start => PadButton.Start, PadBtn.DpadDown => PadButton.DpadDown, _ => PadButton.None,
    };

    static bool IsDown(PadBtn b, Pads p) => b switch
    {
        PadBtn.LT => p.LT > 0.5f,
        PadBtn.RT => p.RT > 0.5f,
        _ => (p.Buttons & ToPad(b)) != 0,
    };

    // ---------------------------------------------------------------- State

    enum MenuPage { Main, Camera }

    readonly QuickMenuOverlay overlay = new();
    HashSet<PadBtn> prevDown = new();
    bool menuOpen, needRelease;
    MenuPage page;
    long menuHideUntil;    // the panel stays hidden until then (the camera is being moved)
    long lingerEnd;        // 0 = open while the D-pad is held; otherwise the time the panel goes away
    string? highlight;     // the row to show lit
    PadStyle style = PadStyle.Xbox;
    long nextStyleCheck;
    string lastControllers = "";

    void OpenMenu()
    {
        overlay.Logger ??= m => Log(m);
        menuOpen = true; page = MenuPage.Main; lingerEnd = 0; highlight = null; menuHideUntil = 0;
        RefreshStyle(true);
        Log($"quick menu: open (pictures: {style}, camera: {PlayCamerasMod.Instance?.CurrentName ?? "-"})");
    }

    protected override void OnDeactivated()
    {
        Cleanup(
            () => { if (editorOpen) CloseEditor(); },
            () => { if (modsOpen) CloseMods(); },
            () => { if (keysOpen) CloseKeys(); },
            () => Stop("disabled or scene changed"),
            mute.Restore, upMute.Restore, Pointer.Restore, Unfreeze);
        menuOpen = false; lingerEnd = 0; needRelease = true;
        upFlipAt = 0; upSince = 0; spawnPrev = 0;
        lastSelf = 0; bonesOf = 0; lSh = 0; rSh = 0;
        boneRoots.Dispose();
        haveRider = false; natKnown = false; bodyDone = false;
        overlay.Hide();
    }

    protected override void OnPackUnload()
    {
        try { MainThread.Run(() => { if (editorOpen) CloseEditor(); if (modsOpen) CloseMods(); if (keysOpen) CloseKeys(); overlay.Dispose(); }); } catch { }
    }

    // ---------------------------------------------------------------- Which pad pictures

    PadStyle CurrentStyle()
    {
        if (IconStyle == "PlayStation") return PadStyle.PlayStation;
        if (IconStyle == "Xbox") return PadStyle.Xbox;
        RefreshStyle(false);
        return style;
    }

    void RefreshStyle(bool force)
    {
        var now = Stopwatch.GetTimestamp();
        if (!force && now < nextStyleCheck) return;
        nextStyleCheck = now + Stopwatch.Frequency * 3;
        var names = JoystickNames();
        var key = string.Join(" | ", names);
        style = names.Any(n => ButtonArt.StyleFromName(n) == PadStyle.PlayStation) ? PadStyle.PlayStation : PadStyle.Xbox;
        if (key != lastControllers)
        {
            lastControllers = key;
            Log($"quick menu: controllers [{key}] -> {style} pictures (Quick menu > Button pictures can force them)");
        }
    }

    static string[] JoystickNames()
    {
        try
        {
            var arr = Unity.CallStatic(Unity.Class("UnityEngine.Input"), "GetJoystickNames");
            return Il2Cpp.ArrayObjects(arr).Select(Il2Cpp.ReadString).Where(n => !string.IsNullOrWhiteSpace(n)).ToArray();
        }
        catch { return Array.Empty<string>(); }
    }

    // ---------------------------------------------------------------- Every frame

    void MenuTick(nint self, Pads pad, long now)
    {
        if (editorOpen) { EditorTick(pad, now); return; }
        if (modsOpen) { ModsTick(pad, now); return; }
        if (keysOpen) { KeysTick(pad, now); return; }
        var cur = new HashSet<PadBtn>();
        foreach (var b in Mappable) if (IsDown(b, pad)) cur.Add(b);
        var pressed = cur.Where(b => !prevDown.Contains(b)).ToHashSet();
        prevDown = cur;

        var looking = phase == Phase.Looking;
        // D-pad down held + L3: the button assignment window (always, whatever L3 is assigned to).
        if (pressed.Contains(PadBtn.L3)) Log($"quick menu: L3 pressed (looking {looking}, menu open {menuOpen}, shown {ShowMenu}, page {page}, busy {pendingAct is not null})");
        if (looking && menuOpen && ShowMenu && page == MenuPage.Main && pendingAct is null && pressed.Contains(PadBtn.L3)) { OpenKeys(); return; }
        if (pendingAct is { } pa)
        {
            if (!(looking && menuOpen)) { pendingAct = null; highlight = null; }
            else if (now >= pendingAt) { pendingAct = null; highlight = null; pa(); }
        }
        else if (looking && menuOpen && ShowMenu) HandlePresses(pressed, now);

        if (lingerEnd != 0 && now >= lingerEnd) { menuOpen = false; lingerEnd = 0; highlight = null; }
        // The panel gets out of the way while the camera is being moved (right stick or the look-behind button), and comes back a moment after.
        if (looking && (Math.Abs(pad.RightX) > 0.01f || Math.Abs(pad.RightY) > 0.01f || RearButton is { } rbk && IsDown(rbk, pad)))
            menuHideUntil = now + Stopwatch.Frequency * 4 / 10;
        var show = ShowMenu && menuOpen && (lingerEnd != 0 || phase == Phase.Looking) && now >= menuHideUntil;
        if (show) Draw(pad);
        else overlay.Hide();
    }

    // A short flash on the row that was just selected, then its action runs (the panel stays as it is, nothing waits for the button to be released).
    Action? pendingAct; long pendingAt;
    void Flash(string row, long now, Action act) { highlight = row; pendingAct = act; pendingAt = now + Stopwatch.Frequency * 18 / 100; }

    void Linger(string row, long now)
    {
        highlight = row;
        lingerEnd = now + (long)(MenuLinger * Stopwatch.Frequency);
    }

    void HandlePresses(HashSet<PadBtn> pressed, long now)
    {
        if (pressed.Count == 0) return;
        bool P(string name) => Parse(name) is { } b && pressed.Contains(b);

        if (page == MenuPage.Main)
        {
            if (PlayCamerasMod.Instance is not null && P(CameraButtonName)) { Flash("camera", now, () => { page = MenuPage.Camera; Log("quick menu: camera list"); }); return; }
            if (P(SpawnButtonName)) { Flash("spawn", now, OpenSpawn); return; }
            if (P(ModsButtonName)) { Flash("mods", now, OpenMods); return; }
            if (PlayCamerasMod.Instance is not null && P(EditorButtonName)) { Flash("editor", now, OpenEditor); return; }
            if (HankHudMod.Instance is { } hud0 && P(HudButtonName))
            {
                var visible = hud0.Enabled && hud0.Show && !hud0.Hidden;
                try
                {
                    if (!visible) { hud0.SetSetting("hidden", JsonValue.Create(false)!); hud0.SetSetting("show", JsonValue.Create(true)!); }
                    else hud0.SetSetting("hidden", JsonValue.Create(true)!);
                }
                catch (Exception e) { Once("hud-hide", e); }
                Log($"quick menu: HUD -> {(visible ? "hidden" : "shown")}");
                Linger("hudonoff", now);
                return;
            }
            if (HankHudMod.Instance is { } hud)
            {
                if (P(HudNextName)) { hud.CycleTheme(1); Linger("hudnext", now); return; }
                if (P(HudPrevName)) { hud.CycleTheme(-1); Linger("hudprev", now); return; }
            }
            if (P(FunButtonName)) { Toggle(Shared.Events, "Fun", "fun", now); return; }
            if (P(OverlayButtonName)) { Toggle(Shared.Main, "Overlay", "overlay", now); return; }
            // Straight to a camera, without the camera list.
            if (PlayCamerasMod.Instance is { } pcm)
                foreach (var (button, camera) in new[] { (DirectPovName, "POV"), (DirectFollowName, "Follow cam"), (DirectDroneName, "Drone"), (DirectGameName, "Game camera") })
                    if (P(button))
                    {
                        // Pressing the button of the camera already in use switches it off (back to the game's camera), like an ON / OFF button.
                        var target = camera != "Game camera" && pcm.CurrentName == camera ? "Game camera" : camera;
                        pcm.Select(target); Log($"quick menu: camera -> {target} (direct)"); Linger("direct:" + camera, now); return;
                    }
            return;
        }

        // Camera list
        foreach (var (button, camera) in new[] { (PovButtonName, "POV"), (FollowButtonName, "Follow cam"), (DroneButtonName, "Drone"), (DefaultButtonName, "Game camera") })
            if (P(button))
            {
                PlayCamerasMod.Instance?.Select(camera);
                Log($"quick menu: camera -> {camera}");
                Linger("cam:" + camera, now);
                return;
            }
        if (P(CameraButtonName)) { page = MenuPage.Main; Log("quick menu: back to the main list"); }
    }

    void Toggle(PackPart? mod, string name, string row, long now)
    {
        if (mod is null) return;
        var on = !mod.Enabled;
        try { mod.SetSetting("enabled", JsonValue.Create(on)!); }
        catch (Exception e) { Log($"quick menu: could not switch {name}: {e.Message}"); return; }
        Log($"quick menu: {name} -> {(on ? "ON" : "OFF")}");
        Linger(row, now);
    }

    void OpenSpawn()
    {
        Log("quick menu: spawn menu");
        menuOpen = false; lingerEnd = 0;
        overlay.Hide();
        Stop("menu");
        needRelease = true;     // the D-pad is still held: do not start the free look again until it is let go
        try
        {
            var spawn = LoadedMods().FirstOrDefault(mod => mod.Id == "join_spawn" && mod.Enabled);
            if (spawn?.OnCommand("join_spawn.open", new JsonObject()) is null)
                Log("Install Join Spawn from the catalog to use the spawn menu.");
        }
        catch (Exception e) { Once("spawn", e); }
    }

    // ---------------------------------------------------------------- Drawing

    void Draw(Pads pad)
    {
        var rows = new List<QuickMenuOverlay.Row>();
        string title;
        var cams = PlayCamerasMod.Instance;
        if (page == MenuPage.Main)
        {
            title = "QUICK MENU";
            // Every action is listed, also the ones with no button (shown as "not assigned").
            QuickMenuOverlay.Row R(string name, string label, string badge, bool on, string hl)
                => Parse(name) is { } bt ? new(bt, label, badge, on, highlight == hl) : new(PadBtn.A, label, "", false, false, HasIcon: false, Value: "not assigned");
            // Order: the right stick (view) and look behind first, then the actions by button (Cross, Square, Circle, Triangle, L2, R2, Options, Share, L3...),
            // the ones with no button after them, and the theme rows (L1 / R1) last.
            var items = new List<(int Order, QuickMenuOverlay.Row Row)>();
            static int Order(PadBtn? b) => b switch
            {
                PadBtn.A => 0, PadBtn.X => 1, PadBtn.B => 2, PadBtn.Y => 3, PadBtn.LT => 4, PadBtn.RT => 5, PadBtn.Start => 6, PadBtn.Back => 7,
                PadBtn.L3 => 8, PadBtn.R3 => 9, PadBtn.LB => 10, PadBtn.RB => 11, _ => 100,
            };
            // Only the actions that have a button are listed (the whole list is in "Button assignment").
            void Add(string name, string label, string badge, bool on, string hl) { if (Parse(name) is not null) items.Add((Order(Parse(name)), R(name, label, badge, on, hl))); }
            rows.Add(new(PadBtn.RightStick, "Free look", "", false, false));
            if (Parse(RearButtonName) is not null) rows.Add(R(RearButtonName, "Look behind", "", false, "rear") with { Highlight = rear > 0.05f });
            if (cams is not null) Add(CameraButtonName, "Camera: " + cams.CurrentName, "", false, "camera");
            if (LoadedMods().Any(mod => mod.Id == "join_spawn" && mod.Enabled)) Add(SpawnButtonName, "Spawn menu", "", false, "spawn");
            if (cams is not null) Add(EditorButtonName, "Camera editor", "", false, "editor");
            Add(ModsButtonName, "Mods", "", false, "mods");
            if (Shared.Events is { } fun) Add(FunButtonName, "Fun overlays", fun.Enabled ? "ON" : "OFF", fun.Enabled, "fun");
            if (HankHudMod.Instance is { } hh) Add(HudButtonName, "HUD", hh.Enabled && hh.Show && !hh.Hidden ? "ON" : "OFF", hh.Enabled && hh.Show && !hh.Hidden, "hudonoff");
            if (Shared.Main is { } ovl) Add(OverlayButtonName, "Banner overlays", ovl.Enabled ? "ON" : "OFF", ovl.Enabled, "overlay");
            if (cams is not null)
            {
                Add(DirectPovName, "POV camera", cams.CurrentName == "POV" ? "ON" : "", true, "direct:POV");
                Add(DirectFollowName, "Follow cam", cams.CurrentName == "Follow cam" ? "ON" : "", true, "direct:Follow cam");
                Add(DirectDroneName, "Drone camera", cams.CurrentName == "Drone" ? "ON" : "", true, "direct:Drone");
                Add(DirectGameName, "Game camera", cams.CurrentName == "Game camera" ? "ON" : "", true, "direct:Game camera");
            }
            items.Add((Order(PadBtn.L3), new(PadBtn.L3, "Button assignment", "", false, highlight == "keys")));
            rows.AddRange(items.OrderBy(i => i.Order).Select(i => i.Row));
            if (cams is not null && cams.CurrentName != "Game camera") rows.Add(new(PadBtn.A, "Hold D-pad up: game camera", "", false, false, HasIcon: false));
            if (HankHudMod.Instance is { } hd)
            {
                rows.Add(R(HudPrevName, "Previous: " + hd.NeighbourTheme(-1), "", false, "hudprev"));
                rows.Add(R(HudNextName, "Next: " + hd.NeighbourTheme(1), "", false, "hudnext"));
                rows.Add(new(PadBtn.A, "Current theme: " + hd.DisplayTheme, "", false, highlight is "hudprev" or "hudnext", HasIcon: false));
            }
        }
        else
        {
            title = "CAMERA";
            var current = cams?.CurrentName ?? "Game camera";
            foreach (var (button, camera) in new[] { (PovButtonName, "POV"), (FollowButtonName, "Follow cam"), (DroneButtonName, "Drone"), (DefaultButtonName, "Game camera") })
                if (Parse(button) is { } b)
                    rows.Add(new(b, camera, current == camera ? "ON" : "", true, highlight == "cam:" + camera));
            rows.Add(new(PadBtn.A, "Hold D-pad up: back to the game camera", "", false, false, HasIcon: false));
        }
        // Smaller window and text: the scale shrinks when the list is long so it always fits the screen.
        var units = 66f + 14f + rows.Sum(r => r.Small ? 40f : 62f);
        var percent = Math.Min(MenuScale * 0.8f, 62000f / units);   // never taller than ~60% of the screen: it stays clear of the HUD windows
        overlay.Show(title, rows, CurrentStyle(), percent, MenuOpacity, HankHudMod.Instance?.CurrentLook);
    }
}
