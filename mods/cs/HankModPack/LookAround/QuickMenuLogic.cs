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
    // Fixed buttons (they cannot be changed): look behind = R3, Mods = Share, Settings = Options.
    string RearButtonName => "RSB / R3";
    string CameraButtonName => Assigned("menu_camera", Q.CameraButtonName);
    string SpawnButtonName => Assigned("menu_spawn", Q.SpawnButtonName);
    string FunButtonName => Assigned("menu_fun", Q.FunButtonName);
    string OverlayButtonName => Assigned("menu_overlay", Q.OverlayButtonName);
    string PadOvButtonName => Assigned("menu_padov", Q.PadOvButtonName);
    string PovButtonName => Assigned("cam_pov", Q.PovButtonName);
    string FollowButtonName => Assigned("cam_follow", Q.FollowButtonName);
    string DroneButtonName => Assigned("cam_drone", Q.DroneButtonName);
    string DefaultButtonName => Assigned("cam_default", Q.DefaultButtonName);
    string DirectPovName => Assigned("menu_cam_pov", Q.DirectPovName);
    string DirectFollowName => Assigned("menu_cam_follow", Q.DirectFollowName);
    string DirectDroneName => Assigned("menu_cam_drone", Q.DirectDroneName);
    string DirectGameName => Assigned("menu_cam_game", Q.DirectGameName);
    string HudPrevName => Assigned("menu_hud_prev", Q.HudPrevName);
    string HudNextName => Assigned("menu_hud_next", Q.HudNextName);
    string ThemesButtonName => Assigned("menu_themes", Q.ThemesButtonName);
    string HudButtonName => Assigned("menu_hud", Q.HudButtonName);
    string EditorButtonName => "Menu / Options";
    string ModsButtonName => "View / Share";
    string SetButtonName(int i) => Assigned("menu_set" + (i + 1), i == 0 ? Q.Set1ButtonName : i == 1 ? Q.Set2ButtonName : Q.Set3ButtonName);
    string DetachButtonName => Assigned("menu_detach", Q.DetachButtonName);
    string ShortcutsButtonName => Assigned("menu_shortcuts", Q.ShortcutsButtonName);

    /// <summary>What an action is given: a keyboard key ("Key: F6") when it has one, otherwise its pad button.</summary>
    static string Assigned(string key, string pad) => Q.GetKeyboard(key) is { } k ? "Key: " + k : pad;

    static Key? ParseKey(string name) => name.StartsWith("Key: ") && Enum.TryParse<Key>(name[5..], out var k) ? k : null;
    static bool IsAssigned(string name) => Parse(name) is not null || ParseKey(name) is not null;
    HashSet<Key> kbPrev = new(), kbPressed = new();

    /// <summary>The free look zoom uses L1 / R1 too: while the menu is shown, a button that changes the HUD does not zoom.</summary>
    bool ZoomBlocked(PadBtn b) => ShowMenu && (Parse(HudPrevName) == b || Parse(HudNextName) == b || Parse(DirectPovName) == b || Parse(DetachButtonName) == b
        || Parse(PadOvButtonName) == b || Parse(ThemesButtonName) == b || Parse(ShortcutsButtonName) == b || Parse(SpawnButtonName) == b
        || Parse(CameraButtonName) == b || Parse(FollowButtonName) == b || Parse(DroneButtonName) == b || Parse(ModsButtonName) == b
        || Parse(FunButtonName) == b || Parse(OverlayButtonName) == b || Parse(HudButtonName) == b || Parse(DefaultButtonName) == b
        || Parse(Q.Set1ButtonName) == b || Parse(Q.Set2ButtonName) == b || Parse(Q.Set3ButtonName) == b);   // a button that runs a menu action must not also zoom

    // ---------------------------------------------------------------- Buttons

    static string KeyLabel(Key k) => k switch { >= Key.D0 and <= Key.D9 => ((int)k - (int)Key.D0).ToString(), Key.Backquote => "`", Key.Semicolon => ";", Key.Comma => ",", Key.Period => ".", Key.Slash => "/", Key.Minus => "-", _ => k.ToString().ToUpperInvariant() };

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
    /// <summary>Whether the look behind button (pad) or key (keyboard) is held.</summary>
    bool RearHeld(Pads pad) => RearButton is { } rb && IsDown(rb, pad) || ParseKey(RearButtonName) is { } rk && Input.IsDown(rk);

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

    enum MenuPage { Main, Camera, Settings }

    readonly QuickMenuOverlay overlay = new();
    HashSet<PadBtn> prevDown = new();
    bool menuOpen, needRelease, stickyMuted;
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
            () => { if (bindsOpen) CloseBinds(); },
            () => { if (setsOpen) CloseSets(); },
            () => { if (themeOpen) CloseTheme(); },
            () => { if (stickyMuted) { mute.Restore(); stickyMuted = false; } },
            () => Stop("disabled or scene changed"),
            mute.Restore, upMute.Restore, Pointer.Restore, Unfreeze, spawnerLookup.Invalidate);
        menuOpen = false; lingerEnd = 0; needRelease = true;
        upFlipAt = 0; upSince = 0; spawnPrev = 0;
        lastSelf = 0; bonesOf = 0; lSh = 0; rSh = 0;
        boneRoots.Dispose();
        haveRider = false; natKnown = false; bodyDone = false;
        overlay.Hide();
    }

    protected override void OnPackUnload()
    {
        try { MainThread.Run(() => { if (editorOpen) CloseEditor(); if (modsOpen) CloseMods(); if (bindsOpen) CloseBinds(); if (setsOpen) CloseSets(); if (themeOpen) CloseTheme(); overlay.Dispose(); }); } catch { }
    }

    // ---------------------------------------------------------------- Which pad pictures

    /// <summary>The button pictures to use anywhere in the pack (replay editor too): the choice in Settings, or the real controller (a Sony pad seen as an Xbox one by Steam Input is still a PlayStation pad).</summary>
    internal static PadStyle IconStyleNow()
        => instance is { } me ? me.CurrentStyle() : QuickMenuMod.Instance?.IconStyle == "PlayStation" ? PadStyle.PlayStation : PadStyle.Xbox;

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
        var sony = SonyPad.Find();
        style = names.Any(n => ButtonArt.StyleFromName(n) == PadStyle.PlayStation) || sony.Count > 0 ? PadStyle.PlayStation : PadStyle.Xbox;
        key += sony.Count > 0 ? " + Sony device" : "";
        if (key != lastControllers)
        {
            lastControllers = key;
            Log($"quick menu: controllers [{key}] -> {style} pictures (Quick menu > Button pictures can force them){(sony.Count > 0 ? "; Sony device: " + sony[0] : "")}");
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
        if (bindsOpen) { BindsTick(pad, now); return; }
        if (setsOpen) { SetsTick(pad, now); return; }
        if (themeOpen) { ThemeTick(pad, now); return; }
        var cur = new HashSet<PadBtn>();
        foreach (var b in Mappable) if (IsDown(b, pad)) cur.Add(b);
        var pressed = cur.Where(b => !prevDown.Contains(b)).ToHashSet();
        prevDown = cur;
        var kbNow = new HashSet<Key>();
        if (Input.GameFocused) foreach (var k in BindableKeys) if (Input.IsDown(k)) kbNow.Add(k);
        kbPressed = kbNow.Where(k => !kbPrev.Contains(k)).ToHashSet();
        kbPrev = kbNow;

        var looking = phase == Phase.Looking;
        // A sub-page of the quick menu (Camera list, Settings) stays open when D-pad down is let go: the controls are off until it is closed or a choice is made.
        if (menuOpen && page != MenuPage.Main && !looking && !stickyMuted && lingerEnd == 0)
        {
            try { mute.Mute(); stickyMuted = true; } catch (Exception e) { Once("sticky-mute", e); }
            Log($"quick menu: {page} page stays open without D-pad down");
        }
        else if (stickyMuted && (!menuOpen || page == MenuPage.Main || lingerEnd != 0))
        {
            try { mute.Restore(); } catch { }
            stickyMuted = false; needRelease = true;
            if (!looking) { menuOpen = lingerEnd != 0; if (lingerEnd == 0) { highlight = null; overlay.Hide(); } }
        }
        looking |= stickyMuted;
        if (pendingAct is { } pa)
        {
            if (!(looking && menuOpen)) { pendingAct = null; highlight = null; }
            else if (now >= pendingAt) { pendingAct = null; highlight = null; pa(); }
        }
        else if (looking && menuOpen && ShowMenu) HandlePresses(pressed, now);

        if (lingerEnd != 0 && now >= lingerEnd) { menuOpen = false; lingerEnd = 0; highlight = null; }
        // The panel gets out of the way while the camera is being moved (right stick or the look-behind button), and comes back a moment after.
        if (looking && (Math.Abs(pad.RightX) > 0.01f || Math.Abs(pad.RightY) > 0.01f || RearHeld(pad)))
            menuHideUntil = now + Stopwatch.Frequency * 4 / 10;
        var show = ShowMenu && menuOpen && (lingerEnd != 0 || phase == Phase.Looking || stickyMuted) && now >= menuHideUntil;
        if (show) Draw(pad);
        else overlay.Hide();
    }

    /// <summary>The actions of the quick menu that have a button or a key now, as rows with the button pictures (for the windows that must not clash with them).</summary>
    List<QuickMenuOverlay.Row> QuickMenuAssignmentRows()
    {
        var items = new List<(int Order, QuickMenuOverlay.Row Row)>();
        void A(string name, string action)
        {
            if (Parse(name) is { } bt) items.Add((Order(bt), new(bt, action, "", false, false)));
            else if (ParseKey(name) is { } k) items.Add((100, new(PadBtn.A, action, "", false, false, HasIcon: false, Value: KeyLabel(k))));
        }
        A(CameraButtonName, "Cameras"); A(SpawnButtonName, "Spawn menu"); A(EditorButtonName, "Settings / Binds / Themes"); A(ThemesButtonName, "Themes"); A(ModsButtonName, "Mods");
        for (var si = 0; si < QuickMenuMod.SetCount; si++) A(SetButtonName(si), Q.GetSetName(si));
        A(ShortcutsButtonName, "Quick Actions"); A(DetachButtonName, "Detach both skis"); A(FunButtonName, "Fun overlays"); A(HudButtonName, "HUD on / off");
        A(OverlayButtonName, "Banner overlays"); A(PadOvButtonName, "Input visualizer"); A(DirectPovName, "POV camera"); A(DirectFollowName, "Follow cam"); A(DirectDroneName, "Drone camera");
        A(DirectGameName, "Game camera"); A(HudPrevName, "Theme previous"); A(HudNextName, "Theme next"); A(RearButtonName, "Look behind");
        return items.OrderBy(i => i.Order).Select(i => i.Row).ToList();
    }

    /// <summary>The order of the buttons in the quick menu: Cross, Circle, Square, Triangle, R1, R2, L1, L2, L3, Share, Options; R3 (look behind) is at the very bottom.</summary>
    static int Order(PadBtn? b) => b switch
    {
        PadBtn.A => 0, PadBtn.B => 1, PadBtn.X => 2, PadBtn.Y => 3, PadBtn.RB => 4, PadBtn.RT => 5, PadBtn.LB => 6, PadBtn.LT => 7,
        PadBtn.L3 => 8, PadBtn.Back => 9, PadBtn.Start => 10, PadBtn.R3 => 200, _ => 100,
    };

    /// <summary>A window opens: it mutes the controls itself, so the sub-page's own mute ends.</summary>
    void ReleaseSticky() { if (stickyMuted) { try { mute.Restore(); } catch { } stickyMuted = false; } }

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
        if (pressed.Count == 0 && kbPressed.Count == 0) return;
        bool P(string name) => Parse(name) is { } b ? pressed.Contains(b) : ParseKey(name) is { } k && kbPressed.Contains(k);

        if (page == MenuPage.Main)
        {
            if (PlayCamerasMod.Instance is not null && P(CameraButtonName)) { Flash("camera", now, () => { page = MenuPage.Camera; Log("quick menu: camera list"); }); return; }
            if (P(SpawnButtonName)) { Flash("spawn", now, OpenSpawn); return; }
            if (P(ModsButtonName)) { Flash("mods", now, OpenMods); return; }
            if (P(ShortcutsButtonName)) { Flash("sets", now, () => OpenSets(true)); return; }
            for (var si = 0; si < QuickMenuMod.SetCount; si++)
            {
                var set = si;
                if (P(SetButtonName(si))) { Flash("set" + si, now, () => OpenSets(true, set)); return; }
            }
            if (P(DetachButtonName)) { var reply = RunAny("detach left ; detach right"); Log($"quick menu: detach both skis{(reply.Length > 0 ? " (" + reply + ")" : "")}"); Linger("detach", now); return; }
            if (P(EditorButtonName)) { Flash("settings", now, () => { page = MenuPage.Settings; Log("quick menu: settings"); }); return; }
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
            if (HankHudMod.Instance is not null && P(ThemesButtonName)) { Flash("themes", now, OpenTheme); return; }
            if (P(PadOvButtonName)) { PadOverlayMod.Instance?.ToggleManual(); Linger("padov", now); return; }
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

        // Settings: camera settings (the camera editor) or binds
        if (page == MenuPage.Settings)
        {
            if (PlayCamerasMod.Instance is not null && pressed.Contains(PadBtn.A)) { Flash("set:cam", now, OpenEditor); return; }
            if (pressed.Contains(PadBtn.X)) { Flash("set:binds", now, OpenBinds); return; }
            if (HankHudMod.Instance is not null && pressed.Contains(PadBtn.RB)) { Flash("set:theme", now, OpenTheme); return; }
            if (pressed.Contains(PadBtn.B) || P(EditorButtonName)) { page = MenuPage.Main; Log("quick menu: back to the main list"); }
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
        menuOpen = false; lingerEnd = 0; ReleaseSticky();
        overlay.Hide();
        Stop("menu");
        needRelease = true;     // the D-pad is still held: do not start the free look again until it is let go
        try
        {
            var spawn = LoadedMods().FirstOrDefault(mod => mod.Id == "join_spawn" && mod.Enabled);
            if (spawn?.OnCommand("join_spawn.open", new JsonObject()) is null)
                Log("Enable Hank Mod Pack: Improved Spawn to use the spawn menu.");
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
                => Parse(name) is { } bt ? new(bt, label, badge, on, highlight == hl)
                 : ParseKey(name) is { } kk ? new(PadBtn.A, label, badge, on, highlight == hl, HasIcon: false, Value: KeyLabel(kk))
                 : new(PadBtn.A, label, "", false, false, HasIcon: false, Value: "not assigned");
            // Order: the right stick (view) and look behind first, then the actions by button (Cross, Square, Circle, Triangle, L2, R2, Options, Share, L3...),
            // the ones with no button after them, and the theme rows (L1 / R1) last.
            var items = new List<(int Order, QuickMenuOverlay.Row Row)>();
            // Only the actions that have a button are listed (the whole list is in Settings > Binds & buttons).
            void Add(string name, string label, string badge, bool on, string hl) { if (IsAssigned(name)) items.Add((Order(Parse(name)), R(name, label, badge, on, hl))); }
            if (cams is not null) Add(CameraButtonName, "Cameras", "", false, "camera");
            if (LoadedMods().Any(mod => mod.Id == "join_spawn" && mod.Enabled)) Add(SpawnButtonName, "Spawn menu", "", false, "spawn");
            Add(EditorButtonName, "Settings / Binds / Themes", "", false, "settings");
            if (HankHudMod.Instance is not null) Add(ThemesButtonName, "Themes", "", false, "themes");
            Add(ModsButtonName, "Mods", "", false, "mods");
            Add(ShortcutsButtonName, "Quick Actions", "", false, "sets");
            for (var si = 0; si < QuickMenuMod.SetCount; si++) Add(SetButtonName(si), Q.GetSetName(si), "", false, "set" + si);
            Add(DetachButtonName, "Detach both skis", "", false, "detach");
            if (Shared.Events is { } fun) Add(FunButtonName, "Fun overlays", fun.Enabled ? "ON" : "OFF", fun.Enabled, "fun");
            if (HankHudMod.Instance is { } hh) Add(HudButtonName, "HUD", hh.Enabled && hh.Show && !hh.Hidden ? "ON" : "OFF", hh.Enabled && hh.Show && !hh.Hidden, "hudonoff");
            if (Shared.Main is { } ovl) Add(OverlayButtonName, "Banner overlays", ovl.Enabled ? "ON" : "OFF", ovl.Enabled, "overlay");
            if (PadOverlayMod.Instance is { } pov) Add(PadOvButtonName, "Input visualizer", pov.Visible ? "ON" : "OFF", pov.Visible, "padov");
            if (cams is not null)
            {
                Add(DirectPovName, "POV camera", cams.CurrentName == "POV" ? "ON" : "", true, "direct:POV");
                Add(DirectFollowName, "Follow cam", cams.CurrentName == "Follow cam" ? "ON" : "", true, "direct:Follow cam");
                Add(DirectDroneName, "Drone camera", cams.CurrentName == "Drone" ? "ON" : "", true, "direct:Drone");
                Add(DirectGameName, "Game camera", cams.CurrentName == "Game camera" ? "ON" : "", true, "direct:Game camera");
            }
            rows.AddRange(items.OrderBy(i => i.Order).Select(i => i.Row));
            if (cams is not null && cams.CurrentName != "Game camera") rows.Add(new(PadBtn.A, "Hold D-pad up: game camera", "", false, false, HasIcon: false));
            if (HankHudMod.Instance is { } hd && (IsAssigned(HudPrevName) || IsAssigned(HudNextName)))
            {
                rows.Add(R(HudPrevName, "Previous: " + hd.NeighbourTheme(-1), "", false, "hudprev"));
                rows.Add(R(HudNextName, "Next: " + hd.NeighbourTheme(1), "", false, "hudnext"));
                rows.Add(new(PadBtn.A, "Current theme: " + hd.DisplayTheme, "", false, highlight is "hudprev" or "hudnext", HasIcon: false));
            }
            // Free look and look behind (R3) at the very bottom.
            rows.Add(new(PadBtn.RightStick, "Free look", "", false, false));
            if (IsAssigned(RearButtonName)) rows.Add(R(RearButtonName, "Look behind", "", false, "rear") with { Highlight = rear > 0.05f });
        }
        else if (page == MenuPage.Settings)
        {
            title = "SETTINGS";
            if (cams is not null) rows.Add(new(PadBtn.A, "Camera settings", "", false, highlight == "set:cam"));
            rows.Add(new(PadBtn.X, "Binds & buttons", "", false, highlight == "set:binds"));
            if (HankHudMod.Instance is { } hs) rows.Add(new(PadBtn.RB, "Theme", "", false, highlight == "set:theme", Value: hs.DisplayTheme));
            rows.Add(new(PadBtn.B, "Back", "", false, false));
        }
        else
        {
            title = "CAMERA";
            var current = cams?.CurrentName ?? "Game camera";
            foreach (var (button, camera) in new[] { (PovButtonName, "POV"), (FollowButtonName, "Follow cam"), (DroneButtonName, "Drone"), (DefaultButtonName, "Game camera") })
                if (Parse(button) is { } b)
                    rows.Add(new(b, camera, current == camera ? "ON" : "", true, highlight == "cam:" + camera));
                else if (ParseKey(button) is { } kc)
                    rows.Add(new(PadBtn.A, camera, current == camera ? "ON" : "", true, highlight == "cam:" + camera, HasIcon: false, Value: KeyLabel(kc)));
            rows.Add(new(PadBtn.A, "Hold D-pad up: back to the game camera", "", false, false, HasIcon: false));
        }
        // Smaller window and text: the scale shrinks when the list is long so it always fits the screen.
        var units = 66f + 14f + rows.Sum(r => r.Small ? 40f : 62f);
        var percent = Math.Min(MenuScale * 0.8f, 62000f / units);   // never taller than ~60% of the screen: it stays clear of the HUD windows
        overlay.Show(title, rows, CurrentStyle(), percent, MenuOpacity, HankHudMod.Instance?.CurrentLook);
    }
}
