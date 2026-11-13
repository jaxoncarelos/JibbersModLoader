using Jibbers.Sdk;

namespace HankModPack;

/// <summary>
/// The shortcut sets. Two ways in:
/// - D-pad down + Circle (quick menu), or the button given to one set (Settings > Binds & buttons): D-pad down can be let go at once. First the set is chosen
///   (D-pad up / down, A; a button given to a set goes straight to it), then L1 / R1 change the set and a button runs its command. View / Share, Options or
///   Esc close the window when nothing was chosen. Nothing can be changed there.
/// - Settings > Shortcuts: the editor. A opens a set (D-pad up / down choose a button, A then picks its command in tabs: favorites, commands, chat, camera;
///   Y adds a command to the favorites), X renames it with the keyboard, B goes back / closes.
/// </summary>
public sealed unsafe partial class LookAroundMod
{
    enum SetsStep { Sets, Use, Edit, Pick, Rename }

    bool setsOpen, setsInit, setsMuted;
    SetsStep setsStep;
    int setsIdx, setsSel, setsTop, setsLast;
    bool setsUse, setsDownFree;
    HashSet<string> setsDownPrev = new();
    HashSet<Key> setsKeysPrev = new();
    string setsMsg = "", setsName = "";
    List<(string Name, List<string> Items)> pickTabs = new();
    int pickTab, pickSel, pickTop;
    SetsStep pickReturn = SetsStep.Edit;
    const int SetsVisible = 8;

    /// <summary>Every pad button a command can be given to (D-pad down is left out: it is held to open the quick menu).</summary>
    static readonly (string Name, string Label, PadBtn? Icon)[] SetButtons =
    {
        ("A", "A / Cross", PadBtn.A), ("B", "B / Circle", PadBtn.B), ("X", "X / Square", PadBtn.X), ("Y", "Y / Triangle", PadBtn.Y),
        ("LT", "LT / L2", PadBtn.LT), ("RT", "RT / R2", PadBtn.RT),
        ("L3", "Left stick click", PadBtn.L3), ("R3", "Right stick click", PadBtn.R3),
        ("Left", "D-pad left", null), ("Right", "D-pad right", null), ("Up", "D-pad up", null), ("Down", "D-pad down", null),
    };

    static bool SetDown(string name, Pads p) => name switch
    {
        "A" => (p.Buttons & PadButton.A) != 0, "B" => (p.Buttons & PadButton.B) != 0, "X" => (p.Buttons & PadButton.X) != 0, "Y" => (p.Buttons & PadButton.Y) != 0,
        "LB" => (p.Buttons & PadButton.LeftShoulder) != 0, "RB" => (p.Buttons & PadButton.RightShoulder) != 0,
        "LT" => p.LT > 0.5f, "RT" => p.RT > 0.5f,
        "L3" => (p.Buttons & PadButton.LeftStick) != 0, "R3" => (p.Buttons & PadButton.RightStick) != 0,
        "Start" => (p.Buttons & PadButton.Start) != 0, "Back" => (p.Buttons & PadButton.Back) != 0,
        "Left" => (p.Buttons & PadButton.DpadLeft) != 0, "Right" => (p.Buttons & PadButton.DpadRight) != 0,
        "Up" => (p.Buttons & PadButton.DpadUp) != 0, "Down" => (p.Buttons & PadButton.DpadDown) != 0,
        _ => false,
    };

    static readonly string[] AllPadNames = { "A", "B", "X", "Y", "LB", "RB", "LT", "RT", "L3", "R3", "Start", "Back", "Left", "Right", "Up", "Down" };

    void OpenSets(bool use, int direct = -1)
    {
        Log($"shortcuts: window open ({(use ? "use" : "editor")}{(direct >= 0 ? ", set " + (direct + 1) : "")})");
        menuOpen = false; lingerEnd = 0; pendingAct = null; highlight = null; ReleaseSticky();
        overlay.Hide();
        Stop("shortcuts");
        needRelease = true;
        setsOpen = true; setsInit = false; setsUse = use; setsDownFree = false; setsStep = use && direct >= 0 ? SetsStep.Use : SetsStep.Sets; setsIdx = direct >= 0 ? direct : use ? setsLast : 0; setsSel = setsIdx; setsTop = 0; setsMsg = "";
        try { mute.Mute(); setsMuted = true; } catch (Exception e) { Once("sets-mute", e); }
    }

    void CloseSets()
    {
        if (!setsOpen) return;
        setsOpen = false;
        overlay.Hide();
        if (setsMuted) { try { mute.Restore(); } catch { } setsMuted = false; }
        needRelease = true;
        Log("shortcuts: window closed");
    }

    static char? TypedChar(Key k)
        => k >= Key.A && k <= Key.Z ? (char)('a' + (k - Key.A)) : k >= Key.D0 && k <= Key.D9 ? (char)('0' + (k - Key.D0)) : k == Key.Space ? ' ' : k == Key.Minus ? '-' : null;

    string SetLabel(int set, string button)
        => Q.GetSetBind(set, button) is { } c ? Label(c) : "-";

    void SetsTick(Pads pad, long now)
    {
        var downNow = AllPadNames.Where(n => SetDown(n, pad)).ToHashSet();
        var keys = KeysDown();
        if (!setsInit) { setsDownPrev = downNow; setsKeysPrev = keys; setsInit = true; }
        var fresh = downNow.Where(n => !setsDownPrev.Contains(n)).ToHashSet();
        var freshKeys = keys.Where(k => !setsKeysPrev.Contains(k)).ToList();
        setsDownPrev = downNow; setsKeysPrev = keys;
        bool Hit(string n) => fresh.Contains(n);
        var q = Q;

        var step = (Hit("Down") || freshKeys.Contains(Key.Down) ? 1 : 0) - (Hit("Up") || freshKeys.Contains(Key.Up) ? 1 : 0);
        var enter = freshKeys.Contains(Key.Enter);
        var esc = freshKeys.Contains(Key.Escape);

        switch (setsStep)
        {
            case SetsStep.Sets:
                if (Hit("B") || Hit("Start") || Hit("Back") || esc) { CloseSets(); return; }
                setsSel = (setsSel + step + QuickMenuMod.SetCount) % QuickMenuMod.SetCount;
                if (Hit("A") || enter)
                {
                    setsIdx = setsSel;
                    if (setsUse) { setsLast = setsIdx; setsStep = SetsStep.Use; setsMsg = ""; }
                    else { setsStep = SetsStep.Edit; setsSel = 0; setsTop = 0; setsMsg = ""; }
                }
                else if (Hit("Y")) { setsIdx = setsSel; setsStep = SetsStep.Edit; setsSel = 0; setsTop = 0; setsMsg = ""; }   // change the buttons of the set
                else if (Hit("X")) { setsIdx = setsSel; setsName = q.GetSetName(setsIdx); setsStep = SetsStep.Rename; setsMsg = ""; }
                break;

            case SetsStep.Use:
            {
                // D-pad down can be let go; pressing it again (or Esc) closes the window. L1 / R1 change the set.
                if (esc || Hit("Back") || Hit("Start")) { CloseSets(); return; }
                var move = (Hit("RB") ? 1 : 0) - (Hit("LB") ? 1 : 0);
                if (move != 0) { setsIdx = (setsIdx + move + QuickMenuMod.SetCount) % QuickMenuMod.SetCount; setsLast = setsIdx; setsMsg = ""; }
                foreach (var b in SetButtons)
                {
                    if (!Hit(b.Name)) continue;
                    var command = q.GetSetBind(setsIdx, b.Name);
                    if (command is null) continue;
                    CloseSets();
                    var reply = RunAny(command);
                    Log($"shortcuts: set {q.GetSetName(setsIdx)}, {b.Label} -> {command}{(reply.Length > 0 ? " (" + reply + ")" : "")}");
                    return;
                }
                break;
            }

            case SetsStep.Edit:
                if (Hit("B") || Hit("Back") || esc) { setsStep = SetsStep.Sets; setsSel = setsIdx; setsMsg = ""; break; }
                setsSel = (setsSel + step + SetButtons.Length) % SetButtons.Length;
                if (Hit("A") || enter)
                {
                    pickTabs = PickerTabs(true); pickTab = 0; pickSel = 0; pickTop = 0; pickReturn = SetsStep.Edit;
                    setsStep = SetsStep.Pick; setsMsg = "";
                }
                else if (Hit("X")) { q.SetSetBind(setsIdx, SetButtons[setsSel].Name, null); setsMsg = $"{SetButtons[setsSel].Label} cleared"; }
                if (setsSel < setsTop) setsTop = setsSel;
                if (setsSel >= setsTop + SetsVisible) setsTop = setsSel - SetsVisible + 1;
                break;

            case SetsStep.Pick:
            {
                var items = pickTabs[pickTab].Items;
                if (Hit("B") || Hit("Back") || esc) { setsStep = pickReturn; setsMsg = ""; break; }
                var tabMove = (Hit("RB") || Hit("Right") || freshKeys.Contains(Key.Right) ? 1 : 0) - (Hit("LB") || Hit("Left") || freshKeys.Contains(Key.Left) ? 1 : 0);
                if (tabMove != 0) { pickTab = (pickTab + tabMove + pickTabs.Count) % pickTabs.Count; pickSel = 0; pickTop = 0; break; }
                if (items.Count > 0)
                {
                    pickSel = (pickSel + step + items.Count) % items.Count;
                    if (Hit("Y"))
                    {
                        setsMsg = ToggleFavorite(pickTabs, items[pickSel]);
                        if (pickSel >= pickTabs[pickTab].Items.Count) pickSel = Math.Max(0, pickTabs[pickTab].Items.Count - 1);
                        break;
                    }
                    if (Hit("A") || enter)
                    {
                        q.SetSetBind(setsIdx, SetButtons[setsSel].Name, items[pickSel]);
                        setsMsg = $"{SetButtons[setsSel].Label} runs {Label(items[pickSel])}";
                        setsStep = pickReturn;
                        break;
                    }
                    if (pickSel < pickTop) pickTop = pickSel;
                    if (pickSel >= pickTop + SetsVisible) pickTop = pickSel - SetsVisible + 1;
                }
                break;
            }

            case SetsStep.Rename:
                if (esc) { setsStep = SetsStep.Sets; setsSel = setsIdx; break; }
                if (enter) { q.SetSetName(setsIdx, setsName); setsStep = SetsStep.Sets; setsSel = setsIdx; setsMsg = "Renamed"; break; }
                foreach (var k in freshKeys)
                {
                    if (k == Key.Backspace && setsName.Length > 0) setsName = setsName[..^1];
                    else if (TypedChar(k) is { } ch && setsName.Length < 20) setsName += setsName.Length == 0 ? char.ToUpperInvariant(ch) : ch;
                }
                if (Hit("B")) { setsStep = SetsStep.Sets; setsSel = setsIdx; }
                break;
        }

        // ------------------------------------------------------------ Drawing
        var rows = new List<QuickMenuOverlay.Row>();
        string title;
        QuickMenuOverlay.Row Legend(params (PadBtn, string)[] items) => new(PadBtn.A, "", "", false, false, HasIcon: false, Small: true, Legend: items);
        switch (setsStep)
        {
            case SetsStep.Sets:
                title = "QUICK ACTIONS";
                for (var i = 0; i < QuickMenuMod.SetCount; i++)
                    rows.Add(new(PadBtn.A, q.GetSetName(i), "", false, i == setsSel, HasIcon: false, Value: $"{q.SetBindCount(i)} button(s)"));
                foreach (var part in Wrap(setsMsg)) rows.Add(new(PadBtn.A, part, "", false, false, HasIcon: false, Small: true));
                if (setsUse) { rows.Add(Legend((PadBtn.DpadDown, "select"), (PadBtn.A, "open the set"))); rows.Add(Legend((PadBtn.Y, "edit buttons"), (PadBtn.X, "rename"))); rows.Add(Legend((PadBtn.Back, "close"), (PadBtn.Start, "close"))); rows.Add(new(PadBtn.A, "Esc: close", "", false, false, HasIcon: false, Small: true)); }
                else
                {
                    rows.Add(Legend((PadBtn.DpadDown, "select"), (PadBtn.A, "assign buttons")));
                    rows.Add(Legend((PadBtn.X, "rename"), (PadBtn.B, "close")));
                }
                break;
            case SetsStep.Use:
            {
                title = "< " + q.GetSetName(setsIdx).ToUpperInvariant() + " >";
                var bound = 0;
                foreach (var b in SetButtons)
                {
                    if (q.GetSetBind(setsIdx, b.Name) is not { } c) continue;
                    bound++;
                    rows.Add(b.Icon is { } ic ? new(ic, Label(c), "", false, false) : new(PadBtn.A, b.Label + ":  " + Label(c), "", false, false, HasIcon: false));
                }
                if (bound == 0) rows.Add(new(PadBtn.A, "Nothing here yet: close this, open Quick Actions and press Y to edit the set", "", false, false, HasIcon: false, Small: true));
                rows.Add(Legend((PadBtn.A, "press a button to run it")));
                rows.Add(Legend((PadBtn.LB, "other set"), (PadBtn.Back, "close")));
                rows.Add(Legend((PadBtn.Start, "close")));
                rows.Add(new(PadBtn.A, "Esc: close", "", false, false, HasIcon: false, Small: true));
                break;
            }
            case SetsStep.Edit:
                title = "ASSIGN  " + q.GetSetName(setsIdx).ToUpperInvariant() + (SetButtons.Length > SetsVisible ? $"   {setsSel + 1}/{SetButtons.Length}" : "");
                foreach (var i in Enumerable.Range(0, SetButtons.Length).Skip(setsTop).Take(SetsVisible))
                {
                    var b = SetButtons[i];
                    rows.Add(b.Icon is { } ic ? new(ic, SetLabel(setsIdx, b.Name), "", false, i == setsSel) : new(PadBtn.A, b.Label + ":  " + SetLabel(setsIdx, b.Name), "", false, i == setsSel, HasIcon: false));
                }
                foreach (var part in Wrap(setsMsg)) rows.Add(new(PadBtn.A, part, "", false, false, HasIcon: false, Small: true));
                rows.Add(Legend((PadBtn.DpadDown, "select"), (PadBtn.A, "choose command")));
                rows.Add(Legend((PadBtn.X, "clear"), (PadBtn.B, "back")));
                break;
            case SetsStep.Pick:
            {
                var items = pickTabs[pickTab].Items;
                title = "COMMAND FOR " + SetButtons[setsSel].Label.ToUpperInvariant() + (items.Count > SetsVisible ? $"   {pickSel + 1}/{items.Count}" : "");
                rows.Add(new(PadBtn.A, TabsText(pickTabs, pickTab), "", false, false, HasIcon: false, Small: true));
                if (items.Count == 0) rows.Add(new(PadBtn.A, "Nothing in this tab", "", false, false, HasIcon: false));
                foreach (var i in Enumerable.Range(0, items.Count).Skip(pickTop).Take(SetsVisible))
                    rows.Add(new(PadBtn.A, Shown(items[i], pickTabs[pickTab].Name), "", false, i == pickSel, HasIcon: false));
                foreach (var part in Wrap(setsMsg)) rows.Add(new(PadBtn.A, part, "", false, false, HasIcon: false, Small: true));
                rows.Add(Legend((PadBtn.DpadDown, "select"), (PadBtn.LB, "tab")));
                rows.Add(Legend((PadBtn.A, "choose"), (PadBtn.Y, "favorite")));
                rows.Add(Legend((PadBtn.B, "cancel")));
                break;
            }
            default:
                title = "RENAME THE SET";
                rows.Add(new(PadBtn.A, setsName + "_", "", false, true, HasIcon: false));
                rows.Add(new(PadBtn.A, "Type with the keyboard, Enter to save, Esc to cancel", "", false, false, HasIcon: false, Small: true));
                break;
        }
        var units = 66f + 14f + rows.Sum(r => r.Small ? 40f : 62f);
        overlay.Show(title, rows, CurrentStyle(), Math.Min(MenuScale * 0.8f, 62000f / units), MenuOpacity, HankHudMod.Instance?.CurrentLook, 560);
    }
}
