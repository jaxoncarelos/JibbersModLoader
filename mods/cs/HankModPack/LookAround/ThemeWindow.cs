using Jibbers.Sdk;

namespace HankModPack;

/// <summary>
/// The theme window (Quick Menu > Settings > Theme): L1 / R1 change the HUD theme and the selected one is shown. It stays open when D-pad down is let go;
/// B, Options or Esc closes it. The game's controls are off while it is open.
/// </summary>
public sealed unsafe partial class LookAroundMod
{
    bool themeOpen, themeInit, themeMuted;
    PadButton themePrev;
    HashSet<Key> themeKeysPrev = new();

    void OpenTheme()
    {
        Log("theme window: open");
        menuOpen = false; lingerEnd = 0; pendingAct = null; highlight = null; ReleaseSticky();
        overlay.Hide();
        Stop("theme");
        needRelease = true;
        themeOpen = true; themeInit = false;
        try { mute.Mute(); themeMuted = true; } catch (Exception e) { Once("theme-mute", e); }
    }

    void CloseTheme()
    {
        if (!themeOpen) return;
        themeOpen = false;
        overlay.Hide();
        if (themeMuted) { try { mute.Restore(); } catch { } themeMuted = false; }
        needRelease = true;
        Log("theme window: closed");
    }

    void ThemeTick(Pads pad, long now)
    {
        var keys = KeysDown();
        if (!themeInit) { themePrev = pad.Buttons; themeKeysPrev = keys; themeInit = true; }
        var edges = pad.Buttons & ~themePrev;
        var fresh = keys.Where(k => !themeKeysPrev.Contains(k)).ToList();
        themePrev = pad.Buttons; themeKeysPrev = keys;
        bool Hit(PadButton b) => (edges & b) != 0;
        var hud = HankHudMod.Instance;
        if (hud is null || Hit(PadButton.B) || Hit(PadButton.Start) || Hit(PadButton.Back) || fresh.Contains(Key.Escape)) { CloseTheme(); return; }
        var step = (Hit(PadButton.RightShoulder) || fresh.Contains(Key.Right) ? 1 : 0) - (Hit(PadButton.LeftShoulder) || fresh.Contains(Key.Left) ? 1 : 0);
        if (step != 0) { hud.CycleTheme(step); Log($"theme window: theme -> {hud.DisplayTheme}"); }

        var rows = new List<QuickMenuOverlay.Row>
        {
            new(PadBtn.LB, "Previous: " + hud.NeighbourTheme(-1), "", false, false),
            new(PadBtn.RB, "Next: " + hud.NeighbourTheme(1), "", false, false),
            new(PadBtn.A, "Selected: " + hud.DisplayTheme, "", false, true, HasIcon: false),
            new(PadBtn.A, "", "", false, false, HasIcon: false, Small: true, Legend: new[] { (PadBtn.B, "close") }),
        };
        overlay.Show("THEME", rows, CurrentStyle(), MenuScale, MenuOpacity, HankHudMod.Instance?.CurrentLook);
    }
}
