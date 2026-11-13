using System.Diagnostics;
using System.Reflection;
using System.Text.Json.Nodes;
using Jibbers.Sdk;

namespace HankModPack;

/// <summary>
/// The mods window: D-pad down held + the button chosen in the options (Menu / Start by default). One line per mod loaded in the game with an ON / OFF
/// badge, in the colours of the HUD theme. D-pad up / down choose, A switches the mod, B or the same button closes. The game's controls are off while it is open.
/// </summary>
public sealed unsafe partial class LookAroundMod
{
    bool modsOpen, modsInit, modsMuted;
    int modsSel, modsTop;
    PadButton modsPrev;
    long modsNextRepeat;
    List<JibbersMod> modsList = new();
    const int ModsWindow = 9;
    static readonly HashSet<string> ModsLocked = new() { "quick_menu", "free_look" };   // switching these off would close the window for good

    static List<JibbersMod> LoadedMods()
    {
        try
        {
            var ctx = typeof(JibbersMod).Assembly.GetType("Jibbers.Sdk.ModContext");
            var all = ctx?.GetMethod("All", BindingFlags.Public | BindingFlags.Static)?.Invoke(null, null) as IEnumerable<JibbersMod>;
            return all?.Where(m => !string.IsNullOrEmpty(m.Name)).ToList() ?? new();
        }
        catch { return new(); }
    }

    static string ShortName(JibbersMod m) => m.Name.StartsWith("Hank Mod Pack: ") ? "Hank · " + m.Name["Hank Mod Pack: ".Length..] : m.Name;

    void OpenMods()
    {
        Log("mods window: open");
        menuOpen = false; lingerEnd = 0; ReleaseSticky();
        overlay.Hide();
        Stop("mods");
        needRelease = true;
        modsOpen = true; modsInit = false;
        modsList = LoadedMods();
        modsSel = 0; modsTop = 0;
        Log($"mods window: {modsList.Count} mod(s) found");
        try { mute.Mute(); modsMuted = true; } catch (Exception e) { Once("mods-mute", e); }
    }

    void CloseMods()
    {
        if (!modsOpen) return;
        modsOpen = false;
        overlay.Hide();
        if (modsMuted) { try { mute.Restore(); } catch { } modsMuted = false; }
        needRelease = true;
        Log("mods window: closed");
    }

    void ModsTick(Pads pad, long now)
    {
        if (PlayCamerasMod.UiOpen) { CloseMods(); return; }
        if (!modsInit) { modsPrev = pad.Buttons; modsInit = true; }
        var edges = pad.Buttons & ~modsPrev;
        modsPrev = pad.Buttons;
        bool Hit(PadButton b) => (edges & b) != 0;

        var count = modsList.Count;
        if (Hit(PadButton.B) || Hit(PadButton.Back) || Hit(PadButton.Start)) { CloseMods(); return; }
        if (count > 0)
        {
            if (Hit(PadButton.DpadDown)) modsSel = (modsSel + 1) % count;
            if (Hit(PadButton.DpadUp)) modsSel = (modsSel + count - 1) % count;
            if (modsSel < modsTop) modsTop = modsSel;
            if (modsSel >= modsTop + ModsWindow) modsTop = modsSel - ModsWindow + 1;
            if (Hit(PadButton.A) || Hit(PadButton.DpadLeft) || Hit(PadButton.DpadRight)) ToggleMod(modsList[modsSel]);
        }

        var rows = new List<QuickMenuOverlay.Row>();
        if (count == 0) rows.Add(new(PadBtn.A, "No mod found", "", false, false, HasIcon: false));
        foreach (var i in Enumerable.Range(0, count).Skip(modsTop).Take(ModsWindow))
        {
            var m = modsList[i];
            var on = false;
            try { on = m.Enabled; } catch { }
            rows.Add(new(PadBtn.A, ShortName(m), on ? "ON" : "OFF", on, i == modsSel, HasIcon: false));
        }
        rows.Add(new(PadBtn.A, "", "", false, false, HasIcon: false, Small: true,
            Legend: new[] { (PadBtn.DpadDown, "select"), (PadBtn.A, "on / off"), (PadBtn.B, "close") }));
        var more = count > ModsWindow ? $"   {modsSel + 1}/{count}" : "";
        overlay.Show("MODS" + more, rows, CurrentStyle(), MenuScale, MenuOpacity, HankHudMod.Instance?.CurrentLook, 560);
    }

    void ToggleMod(JibbersMod m)
    {
        if (ModsLocked.Contains(m.Id)) { Log($"mods window: '{m.Name}' stays on (this window belongs to it)"); return; }
        var want = true;
        try { want = !m.Enabled; } catch { }
        Log($"mods window: '{m.Name}' -> {(want ? "ON" : "OFF")}");
        // SetSetting waits for the game's main thread, which is this one: it is called from another thread so it can finish.
        Task.Run(() => { try { m.SetSetting("enabled", JsonValue.Create(want)!); } catch (Exception e) { Log($"mods window: could not switch '{m.Name}': {e.Message}"); } });
    }
}
