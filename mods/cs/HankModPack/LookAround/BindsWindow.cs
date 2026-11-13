using System.Diagnostics;
using Jibbers.Sdk;

namespace HankModPack;

/// <summary>
/// The binds window (Quick Menu > Settings > Binds): the list of the game's console commands, each with the key it is bound to now.
/// D-pad up / down choose a command (or click it), X (Square) runs it now without a bind, A then press the keyboard key to give it: the window runs the game's own
/// "bind &lt;key&gt; &lt;command&gt;". When that key already runs another command, you choose: replace it, pick another key, or keep it.
/// B or Esc closes. The game's controls are off while it is open.
/// </summary>
public sealed unsafe partial class LookAroundMod
{
    enum BindsStep { List, Listen, Conflict, Action }

    bool bindsOpen, bindsInit, bindsMuted;
    BindsStep bindsStep;
    int bindsSel, bindsTop;
    PadButton bindsPrev;
    HashSet<Key> bindsKeysPrev = new();
    List<string> bindsCmds = new();
    List<(string Name, List<string> Items)> bindsTabs = new();
    int bindsTab;
    bool bindsQuick, bindsKeyMode;
    int bindsAction, bindsActSel, bindsActTop, bindsActTab;
    string bindsChoice = "";
    List<int> bindsActs = new();
    List<GameConsole.Bind> bindsNow = new();
    string bindsPicked = "", bindsKeyName = "", bindsOld = "", bindsMsg = "";
    const int BindsVisible = 8;

    /// <summary>Favorite commands (not in the game's list, or with their arguments filled in): pinned at the top of "Favorites" / "Chat".</summary>
    static readonly (string Label, string Command)[] ExtraCommands =
    {
        ("Builder Mode", "/login bob"), ("Ghost Menu", "ghost_menu"), ("Add Ghost", "add_ghost"),
        ("Detach left ski", "detach left"), ("Detach right ski", "detach right"), ("Detach both skis", "detach left ; detach right"),
    };

    /// <summary>The quick menu's camera actions (only for the shortcut sets: the game's console cannot run them).</summary>
    static readonly (string Label, string Command)[] CameraChoices =
    {
        ("POV camera", "cam:POV"), ("Follow cam", "cam:Follow cam"), ("Drone camera", "cam:Drone"), ("Game camera", "cam:Game camera"),
    };

    /// <summary>The name shown for a command (pinned ones have a friendly name; the command itself is unchanged).</summary>
    static string Label(string command)
        => ExtraCommands.Concat(CameraChoices).Where(e => e.Command.Equals(command, StringComparison.OrdinalIgnoreCase)).Select(e => e.Label).FirstOrDefault() ?? command;

    /// <summary>The tabs of the command choice: favorites, every console command, chat commands, and (shortcut sets only) the camera actions.</summary>
    List<(string Name, List<string> Items)> PickerTabs(bool withCamera)
    {
        var all = GameConsole.Commands(m => Log(m));
        var tabs = new List<(string, List<string>)>
        {
            ("Favorites", Q.Favorites),
            ("Commands", ExtraCommands.Select(e => e.Command).Where(c => !c.StartsWith('/')).Concat(all.Where(c => !c.StartsWith('/'))).Distinct(StringComparer.OrdinalIgnoreCase).ToList()),
            ("Chat", ExtraCommands.Select(e => e.Command).Where(c => c.StartsWith('/')).ToList()),
        };
        if (withCamera) tabs.Add(("Camera", CameraChoices.Select(c => c.Command).ToList()));
        return tabs;
    }

    /// <summary>The name of a command in a list: favorites outside the Favorites tab have a star.</summary>
    static string Shown(string command, string tab) => (tab != "Favorites" && Q.IsFavorite(command) ? "* " : "") + Label(command);

    /// <summary>Y: adds the highlighted command to the favorites or takes it away. Refreshes the Favorites tab; returns the message.</summary>
    string ToggleFavorite(List<(string Name, List<string> Items)> tabs, string command)
    {
        var added = Q.ToggleFavorite(command);
        var fav = tabs.FindIndex(t => t.Name == "Favorites");
        if (fav >= 0) tabs[fav] = ("Favorites", Q.Favorites);
        return added ? $"{Label(command)} added to Favorites" : $"{Label(command)} removed from Favorites";
    }

    static string TabsText(List<(string Name, List<string> Items)> tabs, int current)
        => string.Join("   ", tabs.Select((t, i) => i == current ? $"[{t.Name}]" : t.Name));

    /// <summary>Runs a command: a camera action of the quick menu ("cam:POV") or a line of the game's console.</summary>
    string RunAny(string command)
    {
        if (command.StartsWith("cam:")) { PlayCamerasMod.Instance?.Select(command[4..]); Log($"quick menu: camera -> {command[4..]} (shortcut)"); return ""; }
        return GameConsole.Run(command, m => Log(m));
    }

    /// <summary>A message cut into lines that fit the panel (at most three).</summary>
    static IEnumerable<string> Wrap(string text, int width = 40)
    {
        var lines = new List<string>();
        var rest = text.Trim();
        while (rest.Length > 0 && lines.Count < 3)
        {
            if (rest.Length <= width) { lines.Add(rest); break; }
            var cut = rest.LastIndexOf(' ', width);
            if (cut < 10) cut = width;
            lines.Add(rest[..cut].Trim());
            rest = rest[cut..].Trim();
        }
        return lines;
    }

    /// <summary>Whether what a bind runs (its text) is this command.</summary>
    static bool RunsCommand(string text, string command)
        => text.Equals(command, StringComparison.OrdinalIgnoreCase)
           || text.Split('|', ';').Select(t => t.Trim()).Contains(command, StringComparer.OrdinalIgnoreCase)
           || text.Split(' ', '|', ';').Select(t => t.Trim()).Contains(command, StringComparer.OrdinalIgnoreCase);

    static readonly Key[] BindableKeys = Enum.GetValues<Key>()
        .Where(k => k is not (Key.MouseLeft or Key.MouseRight or Key.MouseMiddle or Key.Mouse4 or Key.Mouse5 or Key.Shift or Key.Control or Key.Alt
                              or Key.LeftShift or Key.RightShift or Key.LeftControl or Key.RightControl or Key.LeftAlt or Key.RightAlt)).ToArray();

    HashSet<Key> KeysDown()
    {
        var set = new HashSet<Key>();
        foreach (var k in BindableKeys) if (Input.IsDown(k)) set.Add(k);
        return set;
    }

    void OpenBinds()
    {
        Log("binds: window open");
        menuOpen = false; lingerEnd = 0; pendingAct = null; highlight = null; ReleaseSticky();
        overlay.Hide();
        Stop("binds");
        needRelease = true;
        bindsOpen = true; bindsInit = false; bindsStep = BindsStep.List; bindsSel = 0; bindsTop = 0; bindsMsg = "";
        bindsTabs = PickerTabs(false); bindsTabs.Insert(0, ("Buttons", new List<string>())); bindsTab = 0; bindsCmds = bindsTabs[0].Items;
        bindsNow = GameConsole.Binds(m => Log(m));
        Log($"binds: {bindsCmds.Count} command(s), {bindsNow.Count} bind(s) now");
        if (bindsCmds.Count == 0 && GameConsole.Problem.Length > 0) bindsMsg = GameConsole.Problem;
        try { mute.Mute(); bindsMuted = true; } catch (Exception e) { Once("binds-mute", e); }
        Pointer.Show();
    }

    void CloseBinds()
    {
        if (!bindsOpen) return;
        bindsOpen = false;
        overlay.Hide();
        Pointer.Restore();
        if (bindsMuted) { try { mute.Restore(); } catch { } bindsMuted = false; }
        needRelease = true;
        Log("binds: window closed");
    }

    string KeysOf(string command)
    {
        var keys = bindsNow.Where(b => RunsCommand(b.Text, command)).Select(b => b.KeyName.ToUpperInvariant()).Distinct().ToList();
        return string.Join(", ", keys);
    }

    void BindDone(string key, string command)
    {
        var reply = GameConsole.Run($"bind {key} {command}", m => Log(m));
        bindsNow = GameConsole.Binds(m => Log(m));
        var applied = bindsNow.Any(b => b.KeyName.Equals(key, StringComparison.OrdinalIgnoreCase) && RunsCommand(b.Text, command));
        bindsMsg = reply.Length > 0 ? reply : applied ? $"{key.ToUpperInvariant()} now runs {command}" : $"bind {key} {command} sent (check the log)";
        bindsStep = BindsStep.List;
    }

    // ------------------------------------------------------------ The "Buttons" tab: every button of the controller and what it does in the quick menu

    /// <summary>Every button of the controller, in the order of the quick menu. Free look (D-pad down held) is not a button that can be given away.</summary>
    static readonly (string Choice, PadBtn Icon, string? Fixed)[] PadRows =
    {
        ("A / Cross", PadBtn.A, null), ("B / Circle", PadBtn.B, null), ("X / Square", PadBtn.X, null), ("Y / Triangle", PadBtn.Y, null),
        ("RB / R1", PadBtn.RB, null), ("RT / R2", PadBtn.RT, null), ("LB / L1", PadBtn.LB, null), ("LT / L2", PadBtn.LT, null),
        ("LSB / L3", PadBtn.L3, null), ("View / Share", PadBtn.Back, "Mods window"), ("Menu / Options", PadBtn.Start, "Settings"), ("RSB / R3", PadBtn.R3, "Look behind"),
        ("", PadBtn.RightStick, "Free look"),
    };

    /// <summary>The actions that can be given a button (the ones of the quick menu's main list).</summary>
    static List<int> AssignableActions() => Enumerable.Range(0, KeyActions.Length).Where(i => KeyActions[i].Group == 0).ToList();

    /// <summary>The rows of the Buttons tab: a row per button, then a row per action that has a keyboard key.</summary>
    static List<(bool IsKey, int Pad, int Action)> QuickRows()
    {
        var list = new List<(bool, int, int)>();
        for (var i = 0; i < PadRows.Length; i++) list.Add((false, i, -1));
        foreach (var a in AssignableActions()) if (Q.GetKeyboard(KeyActions[a].Key) is not null) list.Add((true, -1, a));
        return list;
    }

    static bool IsLocked((bool IsKey, int Pad, int Action) r) => !r.IsKey && PadRows[r.Pad].Fixed is not null;

    /// <summary>The action that has this button now (-1 when none).</summary>
    static int ActionOn(string choice) => AssignableActions().Where(a => CurrentOf(Q, a) == choice).DefaultIfEmpty(-1).First();

    static void FreeButton(string choice)
    {
        foreach (var a in AssignableActions().Where(a => CurrentOf(Q, a) == choice).ToList()) QuickMenuMod.Instance?.SetChoice(KeyActions[a].Key, "None");
    }

    void BindsTick(Pads pad, long now)
    {
        if (!bindsInit) { bindsPrev = pad.Buttons; bindsKeysPrev = KeysDown(); bindsInit = true; }
        var edges = pad.Buttons & ~bindsPrev;
        bindsPrev = pad.Buttons;
        bool Hit(PadButton b) => (edges & b) != 0;
        var keys = KeysDown();
        var fresh = keys.Where(k => !bindsKeysPrev.Contains(k)).ToList();
        bindsKeysPrev = keys;
        var quick = bindsTab == 0;
        var qrows = quick ? QuickRows() : new List<(bool IsKey, int Pad, int Action)>();
        var count = quick ? qrows.Count : bindsCmds.Count;
        var vis = quick ? Math.Max(BindsVisible, count) : BindsVisible;
        var click = Pointer.Position(out var mx, out var my) && Pointer.Down(0) ? overlay.HitTest(mx, my) : null;
        // The next row up or down (the locked rows are skipped).
        int Move(int from, int dir)
        {
            for (var k = 1; k <= count; k++)
            {
                var j = ((from + dir * k) % count + count) % count;
                if (!(quick && IsLocked(qrows[j]))) return j;
            }
            return from;
        }

        switch (bindsStep)
        {
            case BindsStep.List:
                if (Hit(PadButton.B) || Hit(PadButton.Start) || Hit(PadButton.Back) || fresh.Contains(Key.Escape)) { CloseBinds(); return; }
                var tabMove = (Hit(PadButton.RightShoulder) || fresh.Contains(Key.Right) ? 1 : 0) - (Hit(PadButton.LeftShoulder) || fresh.Contains(Key.Left) ? 1 : 0);
                if (tabMove != 0)
                {
                    bindsTab = (bindsTab + tabMove + bindsTabs.Count) % bindsTabs.Count;
                    bindsCmds = bindsTabs[bindsTab].Items; bindsSel = 0; bindsTop = 0; bindsMsg = "";
                    break;
                }
                if (count > 0)
                {
                    if (Hit(PadButton.DpadDown) || fresh.Contains(Key.Down)) bindsSel = Move(bindsSel, 1);
                    if (Hit(PadButton.DpadUp) || fresh.Contains(Key.Up)) bindsSel = Move(bindsSel, -1);
                    if (bindsSel >= count) bindsSel = 0;
                    var rowHit = click is { } hit && hit.Row >= 1 && hit.Row - 1 < Math.Min(vis, count - bindsTop) ? bindsTop + hit.Row - 1 : -1;
                    if (rowHit >= 0 && !(quick && IsLocked(qrows[rowHit]))) bindsSel = rowHit; else rowHit = -1;
                    var confirm = rowHit >= 0 || Hit(PadButton.A) || fresh.Contains(Key.Enter);
                    if (quick)
                    {
                        var r = qrows[bindsSel];
                        if (confirm)
                        {
                            if (r.IsKey) { bindsAction = r.Action; bindsKeyMode = true; bindsQuick = true; bindsStep = BindsStep.Listen; bindsMsg = ""; }
                            else { bindsChoice = PadRows[r.Pad].Choice; bindsKeyMode = false; OpenActionChooser(); }
                        }
                        else if (Hit(PadButton.X) && !r.IsKey) { bindsChoice = PadRows[r.Pad].Choice; bindsKeyMode = true; OpenActionChooser(); }
                        else if (Hit(PadButton.Y))
                        {
                            if (r.IsKey) { SetKey(r.Action, "None"); bindsMsg = $"{KeyActions[r.Action].Label}: no key"; }
                            else { FreeButton(PadRows[r.Pad].Choice); bindsMsg = $"{PadRows[r.Pad].Choice} is free"; }
                        }
                    }
                    else if (confirm) { bindsPicked = bindsCmds[bindsSel]; bindsQuick = false; bindsStep = BindsStep.Listen; bindsMsg = ""; }
                    else if (Hit(PadButton.Y))
                    {
                        bindsMsg = ToggleFavorite(bindsTabs, bindsCmds[bindsSel]);
                        bindsCmds = bindsTabs[bindsTab].Items;
                        if (bindsSel >= bindsCmds.Count) bindsSel = Math.Max(0, bindsCmds.Count - 1);
                    }
                    else if (Hit(PadButton.X))
                    {
                        // Square: runs the command now, without a bind.
                        var cmd = bindsCmds[bindsSel];
                        var reply = RunAny(cmd);
                        bindsMsg = reply.Length > 0 ? reply : $"{Label(cmd)} sent";
                    }
                    if (bindsSel < bindsTop) bindsTop = bindsSel;
                    if (bindsSel >= bindsTop + vis) bindsTop = bindsSel - vis + 1;
                }
                break;

            case BindsStep.Action:
            {
                // Which action the button (or the key) gets.
                if (Hit(PadButton.B) || Hit(PadButton.Start) || fresh.Contains(Key.Escape)) { bindsStep = BindsStep.List; bindsMsg = ""; break; }
                var tabStep = (Hit(PadButton.RightShoulder) || fresh.Contains(Key.Right) ? 1 : 0) - (Hit(PadButton.LeftShoulder) || fresh.Contains(Key.Left) ? 1 : 0);
                if (tabStep != 0) { bindsActTab = (bindsActTab + tabStep + ActionTabs.Length) % ActionTabs.Length; FillActions(); }
                var n = bindsActs.Count;
                if (Hit(PadButton.DpadDown) || fresh.Contains(Key.Down)) bindsActSel = (bindsActSel + 1) % n;
                if (Hit(PadButton.DpadUp) || fresh.Contains(Key.Up)) bindsActSel = (bindsActSel + n - 1) % n;
                if (bindsActSel < bindsActTop) bindsActTop = bindsActSel;
                if (bindsActSel >= bindsActTop + BindsVisible) bindsActTop = bindsActSel - BindsVisible + 1;
                if (Hit(PadButton.A) || fresh.Contains(Key.Enter))
                {
                    var chosen = bindsActs[bindsActSel];
                    if (bindsKeyMode)
                    {
                        bindsAction = chosen; bindsQuick = true; bindsStep = BindsStep.Listen; bindsMsg = "";
                    }
                    else
                    {
                        FreeButton(bindsChoice);
                        if (chosen >= 0) SetKey(chosen, bindsChoice);
                        bindsMsg = chosen >= 0 ? $"{bindsChoice}: {KeyActions[chosen].Label}" : $"{bindsChoice} is free";
                        bindsStep = BindsStep.List;
                    }
                }
                break;
            }

            case BindsStep.Listen when bindsQuick:
            {
                // The next key pressed on the keyboard is the new one for this action of the quick menu. Esc cancels.
                if (fresh.Contains(Key.Escape)) { bindsStep = BindsStep.List; bindsMsg = ""; break; }
                if (fresh.Count > 0)
                {
                    SetKey(bindsAction, "Key: " + fresh[0]);
                    bindsMsg = $"{KeyActions[bindsAction].Label} -> {KeyLabel(fresh[0])} (key)";
                    bindsStep = BindsStep.List;
                }
                break;
            }

            case BindsStep.Listen:
                if (Hit(PadButton.B) || Hit(PadButton.Start) || fresh.Contains(Key.Escape)) { bindsStep = BindsStep.List; bindsMsg = ""; break; }
                if (fresh.Count > 0)
                {
                    var key = fresh[0];
                    var unity = Input.UnityKeyCode((int)key);
                    var name = unity is int u ? GameConsole.KeyName(u, m => Log(m)) : null;
                    if (name is null) { bindsMsg = $"{key} cannot be bound"; Log($"binds: no console name for key {key}"); break; }
                    bindsKeyName = name;
                    var old = bindsNow.FirstOrDefault(b => b.KeyName.Equals(name, StringComparison.OrdinalIgnoreCase));
                    if (old.KeyName is not null && !RunsCommand(old.Text, bindsPicked))
                    {
                        bindsOld = old.Text;
                        bindsStep = BindsStep.Conflict;
                        Log($"binds: {name} already runs '{old.Text}'");
                    }
                    else BindDone(name, bindsPicked);
                }
                break;

            case BindsStep.Conflict:
                if (Hit(PadButton.A) || fresh.Contains(Key.Enter)) BindDone(bindsKeyName, bindsPicked);
                else if (Hit(PadButton.X)) { bindsStep = BindsStep.Listen; bindsMsg = ""; }
                else if (Hit(PadButton.B) || Hit(PadButton.Start) || fresh.Contains(Key.Escape)) { bindsStep = BindsStep.List; bindsMsg = $"{bindsKeyName.ToUpperInvariant()} keeps {bindsOld}"; }
                break;
        }

        // ------------------------------------------------------------ Drawing
        var rows = new List<QuickMenuOverlay.Row>();
        string title;
        QuickMenuOverlay.Row Legend(params (PadBtn, string)[] items) => new(PadBtn.A, "", "", false, false, HasIcon: false, Small: true, Legend: items);
        if (bindsStep == BindsStep.List)
        {
            title = "BINDS" + (!quick && count > BindsVisible ? $"   {bindsSel + 1}/{count}" : "");
            rows.Add(new(PadBtn.A, TabsText(bindsTabs, bindsTab), "", false, false, HasIcon: false, Small: true));
            if (quick)
            {
                foreach (var i in Enumerable.Range(0, count).Skip(bindsTop).Take(vis))
                {
                    var r = qrows[i];
                    if (r.IsKey)
                        rows.Add(new(PadBtn.A, KeyActions[r.Action].Label, "", false, i == bindsSel, HasIcon: false, Value: KeyLabel(ParseKey("Key: " + Q.GetKeyboard(KeyActions[r.Action].Key)) ?? Key.A) + " (key)"));
                    else if (PadRows[r.Pad].Fixed is { } fixedLabel)
                        rows.Add(new(PadRows[r.Pad].Icon, fixedLabel, "X", false, true));          // cannot be changed: lit, with a red cross
                    else
                    {
                        var a = ActionOn(PadRows[r.Pad].Choice);
                        rows.Add(new(PadRows[r.Pad].Icon, a >= 0 ? KeyActions[a].Label : "free", "", false, i == bindsSel));
                    }
                }
            }
            else
            {
                if (count == 0) rows.Add(new(PadBtn.A, "Nothing in this tab", "", false, false, HasIcon: false));
                foreach (var i in Enumerable.Range(0, count).Skip(bindsTop).Take(BindsVisible))
                    rows.Add(new(PadBtn.A, Shown(bindsCmds[i], bindsTabs[bindsTab].Name), "", false, i == bindsSel, HasIcon: false, Value: KeysOf(bindsCmds[i])));
            }
            foreach (var part in Wrap(bindsMsg)) rows.Add(new(PadBtn.A, part, "", false, false, HasIcon: false, Small: true));
            rows.Add(Legend((PadBtn.DpadDown, "select"), (PadBtn.LB, "tab")));
            rows.Add(Legend(quick ? new[] { (PadBtn.A, "choose action"), (PadBtn.X, "keyboard key") } : new[] { (PadBtn.A, "assign a key"), (PadBtn.X, "run now") }));
            rows.Add(Legend((PadBtn.Y, quick ? "free" : "favorite"), (PadBtn.B, "close")));
        }
        else if (bindsStep == BindsStep.Action)
        {
            title = bindsKeyMode ? "ACTION FOR A KEY" : "ACTION FOR " + bindsChoice.ToUpperInvariant();
            rows.Add(new(PadBtn.A, string.Join("   ", ActionTabs.Select((t, i) => i == bindsActTab ? $"[{t}]" : t)), "", false, false, HasIcon: false, Small: true));
            foreach (var i in Enumerable.Range(0, bindsActs.Count).Skip(bindsActTop).Take(BindsVisible))
            {
                var a = bindsActs[i];
                rows.Add(a < 0 ? new(PadBtn.A, "Free (no action)", "", false, i == bindsActSel, HasIcon: false)
                               : new(PadBtn.A, KeyActions[a].Label, "", false, i == bindsActSel, HasIcon: false, Value: CurrentOf(Q, a) is var c && c != "None" ? (ParseKey(c) is { } kk ? KeyLabel(kk) : c.Split(" / ").Last()) : ""));
            }
            rows.Add(Legend((PadBtn.DpadDown, "select"), (PadBtn.LB, "tab"), (PadBtn.A, "choose")));
            rows.Add(Legend((PadBtn.B, "cancel")));
        }
        else if (bindsStep == BindsStep.Listen)
        {
            title = bindsQuick ? "PRESS A KEY" : "PRESS A KEY";
            rows.Add(new(PadBtn.A, bindsQuick ? KeyActions[bindsAction].Label : Label(bindsPicked), "", false, true, HasIcon: false, Value: "press a key..."));
            foreach (var part in Wrap(bindsMsg)) rows.Add(new(PadBtn.A, part, "", false, false, HasIcon: false, Small: true));
            // What the quick menu already uses, as in the quick menu itself (smaller), so the new key does not clash with it.
            var taken = QuickMenuAssignmentRows();
            if (taken.Count > 0) rows.Add(new(PadBtn.A, "Quick menu buttons in use:", "", false, false, HasIcon: false, Small: true));
            rows.AddRange(taken);
            rows.Add(bindsQuick ? new(PadBtn.A, "Esc: cancel", "", false, false, HasIcon: false, Small: true) : Legend((PadBtn.B, "cancel")));
        }
        else
        {
            title = "KEY ALREADY USED";
            rows.Add(new(PadBtn.A, bindsKeyName.ToUpperInvariant() + " already runs", "", false, false, HasIcon: false, Value: bindsOld));
            rows.Add(new(PadBtn.A, "Replace it with " + Label(bindsPicked), "", false, false, HasIcon: true));
            rows.Add(new(PadBtn.X, "Choose another key", "", false, false, HasIcon: true));
            rows.Add(new(PadBtn.B, "Keep " + bindsOld, "", false, false, HasIcon: true));
        }
        var units = 66f + 14f + rows.Sum(r => r.Small ? 40f : 62f);
        overlay.Show(title, rows, CurrentStyle(), Math.Min(bindsStep == BindsStep.Listen ? MenuScale * 0.7f : MenuScale, 62000f / units), MenuOpacity, HankHudMod.Instance?.CurrentLook, 560);
    }

    /// <summary>Opens the choice of the action for a button (or a key): the actions of the quick menu, with "Free" first for a button.</summary>
    static readonly string[] ActionTabs = { "All", "Quick Actions", "Cameras", "HUD & overlays", "Other" };

    /// <summary>The tab an action of the quick menu is listed in (besides "All").</summary>
    static int ActionTabOf(string key) => key switch
    {
        "menu_shortcuts" or "menu_set1" or "menu_set2" or "menu_set3" => 1,
        "menu_camera" or "menu_cam_pov" or "menu_cam_follow" or "menu_cam_drone" or "menu_cam_game" => 2,
        "menu_hud" or "menu_hud_prev" or "menu_hud_next" or "menu_fun" or "menu_overlay" or "menu_padov" or "menu_themes" => 3,
        _ => 4,
    };

    void FillActions()
    {
        bindsActs = new List<int>();
        if (!bindsKeyMode) bindsActs.Add(-1);
        bindsActs.AddRange(AssignableActions().Where(a => bindsActTab == 0 || ActionTabOf(KeyActions[a].Key) == bindsActTab));
        bindsActSel = 0; bindsActTop = 0;
    }

    /// <summary>Opens the choice of the action for a button (or a key): tabs (all, shortcuts, cameras, HUD and overlays, other), with "Free" first for a button.</summary>
    void OpenActionChooser()
    {
        bindsActTab = 0;
        FillActions();
        bindsMsg = "";
        bindsStep = BindsStep.Action;
    }
}
