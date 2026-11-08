using System.Diagnostics;
using Jibbers.Sdk;

namespace HankModPack;

/// <summary>
/// The button assignment window: hold D-pad down (quick menu) and press L3. One line per action with the button it uses.
/// D-pad up / down choose, A then press the new button (or left / right to browse the buttons), B closes. The mouse works too: click a line (left half = previous button, right half = next).
/// </summary>
public sealed unsafe partial class LookAroundMod
{
    bool keysOpen, keysInit, keysMuted, keysListening;
    int keysSel;
    PadButton keysPrev;

    static readonly (string Label, string Key, int Group)[] KeyActions =
    {
        ("Look behind", "look_behind", 0), ("Mods window", "menu_mods", 0), ("Camera menu", "menu_camera", 0), ("Camera: POV (direct)", "menu_cam_pov", 0), ("Camera: Follow cam (direct)", "menu_cam_follow", 0), ("Camera: Drone (direct)", "menu_cam_drone", 0), ("Camera: Game camera (direct)", "menu_cam_game", 0), ("Spawn menu", "menu_spawn", 0),
        ("Camera editor", "menu_editor2", 0), ("HUD on / off", "menu_hud", 0), ("HUD theme: previous", "menu_hud_prev", 0), ("HUD theme: next", "menu_hud_next", 0),
        ("Fun on / off", "menu_fun", 0), ("Banners on / off", "menu_overlay", 0),
        ("Camera list: POV", "cam_pov", 1), ("Camera list: Follow cam", "cam_follow", 1), ("Camera list: Drone", "cam_drone", 1), ("Camera list: Game camera", "cam_default", 1),
    };
    static readonly string[] KeyChoices = { "None", "A / Cross", "B / Circle", "X / Square", "Y / Triangle", "LB / L1", "RB / R1", "LT / L2", "RT / R2", "LSB / L3", "RSB / R3", "View / Share", "Menu / Options" };

    void OpenKeys()
    {
        Log("quick menu: key assignment window");
        menuOpen = false; lingerEnd = 0; pendingAct = null; highlight = null;
        overlay.Hide();
        Stop("keys");
        needRelease = true;
        keysOpen = true; keysInit = false; keysSel = 0; keysListening = false;
        try { mute.Mute(); keysMuted = true; } catch (Exception e) { Once("keys-mute", e); }
        Pointer.Show();
    }

    void CloseKeys()
    {
        if (!keysOpen) return;
        keysOpen = false; keysListening = false;
        overlay.Hide();
        Pointer.Restore();
        if (keysMuted) { try { mute.Restore(); } catch { } keysMuted = false; }
        needRelease = true;
        Log("quick menu: key assignment window closed");
    }

    void SetKey(int row, string value)
    {
        if (QuickMenuMod.Instance is not { } q) return;
        q.SetChoice(KeyActions[row].Key, value);
        Log($"quick menu: '{KeyActions[row].Label}' -> {value}");
    }

    void CycleKey(int row, int dir)
    {
        var cur = QuickMenuMod.Instance?.GetChoice(KeyActions[row].Key) ?? "None";
        var i = Math.Max(0, Array.IndexOf(KeyChoices, cur));
        SetKey(row, KeyChoices[((i + dir) % KeyChoices.Length + KeyChoices.Length) % KeyChoices.Length]);
    }

    static string? NameOf(PadButton b) => b switch
    {
        PadButton.A => "A / Cross", PadButton.B => "B / Circle", PadButton.X => "X / Square", PadButton.Y => "Y / Triangle",
        PadButton.LeftShoulder => "LB / L1", PadButton.RightShoulder => "RB / R1", PadButton.LeftStick => "LSB / L3", PadButton.RightStick => "RSB / R3",
        PadButton.Back => "View / Share", PadButton.Start => "Menu / Options", _ => null,
    };

    void KeysTick(Pads pad, long now)
    {
        if (!keysInit) { keysPrev = pad.Buttons; keysInit = true; }
        var edges = pad.Buttons & ~keysPrev;
        keysPrev = pad.Buttons;
        bool Hit(PadButton b) => (edges & b) != 0;
        var count = KeyActions.Length;

        if (keysListening)
        {
            // The next button pressed is the new one (the triggers are read from their analog value).
            string? picked = null;
            foreach (var b in new[] { PadButton.A, PadButton.B, PadButton.X, PadButton.Y, PadButton.LeftShoulder, PadButton.RightShoulder, PadButton.LeftStick, PadButton.RightStick, PadButton.Back, PadButton.Start })
                if (Hit(b)) { picked = NameOf(b); break; }
            if (picked is null && pad.LT > 0.6f) picked = "LT / L2";
            if (picked is null && pad.RT > 0.6f) picked = "RT / R2";
            if (picked is not null) { SetKey(keysSel, picked); keysListening = false; keysTrig = true; }
        }
        else
        {
            if (Hit(PadButton.B) || Hit(PadButton.Start)) { CloseKeys(); return; }
            if (Hit(PadButton.DpadDown)) keysSel = (keysSel + 1) % count;
            if (Hit(PadButton.DpadUp)) keysSel = (keysSel + count - 1) % count;
            if (Hit(PadButton.DpadRight)) CycleKey(keysSel, 1);
            if (Hit(PadButton.DpadLeft)) CycleKey(keysSel, -1);
            if (Hit(PadButton.A)) { keysListening = true; keysTrig = pad.LT > 0.6f || pad.RT > 0.6f; }
            if (Hit(PadButton.Y)) SetKey(keysSel, "None");
        }

        // Mouse: a click on a line picks it, left half = previous button, right half = next.
        if (Pointer.Position(out var mx, out var my) && Pointer.Down(0) && overlay.HitTest(mx, my) is { } hit && hit.Row < count)
        {
            keysSel = hit.Row; keysListening = false;
            CycleKey(keysSel, hit.Fx < 0.5f ? -1 : 1);
        }

        var q = QuickMenuMod.Instance;
        var rows = new List<QuickMenuOverlay.Row>();
        var values = new string[count];
        for (var i = 0; i < count; i++) values[i] = q?.GetChoice(KeyActions[i].Key) ?? "None";
        for (var i = 0; i < count; i++)
        {
            var cur = values[i];
            var dup = cur != "None" && Enumerable.Range(0, count).Any(j => j != i && KeyActions[j].Group == KeyActions[i].Group && values[j] == cur);
            var text = i == keysSel && keysListening ? "press a button..." : cur + (dup ? "  !" : "");
            rows.Add(new(PadBtn.A, KeyActions[i].Label, "", false, i == keysSel, HasIcon: false, Value: text));
        }
        rows.Add(new(PadBtn.A, "", "", false, false, HasIcon: false, Small: true, Legend: new[] { (PadBtn.DpadDown, "choose / browse"), (PadBtn.A, "press a button") }));
        rows.Add(new(PadBtn.A, "", "", false, false, HasIcon: false, Small: true, Legend: new[] { (PadBtn.Y, "none"), (PadBtn.B, "close") }));
        var percent = Math.Min(MenuScale, 82000f / (66 + 14 + 62 * count + 80));
        overlay.Show("QUICK MENU BUTTONS", rows, CurrentStyle(), percent, MenuOpacity, HankHudMod.Instance?.CurrentLook, 560);
    }
    bool keysTrig;
}
