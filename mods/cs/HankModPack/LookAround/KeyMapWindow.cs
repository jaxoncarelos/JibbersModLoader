using System.Diagnostics;
using Jibbers.Sdk;

namespace HankModPack;

/// <summary>
/// The quick menu's actions and the buttons they can have. The window that changes them is the \"Buttons\" tab of the binds window (BindsWindow.cs).
/// </summary>
public sealed unsafe partial class LookAroundMod
{
    static readonly (string Label, string Key, int Group)[] KeyActions =
    {
        ("Quick Actions (3 sets)", "menu_shortcuts", 0), ("Detach both skis", "menu_detach", 0), ("Quick Actions set 1", "menu_set1", 0), ("Quick Actions set 2", "menu_set2", 0), ("Quick Actions set 3", "menu_set3", 0), ("Cameras (menu)", "menu_camera", 0), ("Camera: POV (direct)", "menu_cam_pov", 0), ("Camera: Follow cam (direct)", "menu_cam_follow", 0), ("Camera: Drone (direct)", "menu_cam_drone", 0), ("Camera: Game camera (direct)", "menu_cam_game", 0), ("Spawn menu", "menu_spawn", 0),
        ("HUD on / off", "menu_hud", 0), ("HUD theme: previous", "menu_hud_prev", 0), ("HUD theme: next", "menu_hud_next", 0),
        ("Fun on / off", "menu_fun", 0), ("Banners on / off", "menu_overlay", 0), ("Input visualizer on / off", "menu_padov", 0), ("Themes (theme window)", "menu_themes", 0),
        ("Camera list: POV", "cam_pov", 1), ("Camera list: Follow cam", "cam_follow", 1), ("Camera list: Drone", "cam_drone", 1), ("Camera list: Game camera", "cam_default", 1),
    };
    static readonly string[] KeyChoices = { "None", "A / Cross", "B / Circle", "X / Square", "Y / Triangle", "LB / L1", "RB / R1", "LT / L2", "RT / R2", "LSB / L3", "RSB / R3", "View / Share", "Menu / Options" };

    /// <summary>What an action has now: "Key: F6" for a keyboard key, otherwise its pad button.</summary>
    static string CurrentOf(QuickMenuMod? q, int row)
        => q?.GetKeyboard(KeyActions[row].Key) is { } kb ? "Key: " + kb : q?.GetChoice(KeyActions[row].Key) ?? "None";

    void SetKey(int row, string value)
    {
        if (QuickMenuMod.Instance is not { } q) return;
        if (value.StartsWith("Key: ")) { q.SetKeyboard(KeyActions[row].Key, value[5..]); value = "None"; }   // a key replaces the pad button
        else q.SetKeyboard(KeyActions[row].Key, null);                                                       // a pad button replaces the key
        q.SetChoice(KeyActions[row].Key, value);
        Log($"quick menu: '{KeyActions[row].Label}' -> {value}");
    }

    void CycleKey(int row, int dir)
    {
        var cur = CurrentOf(QuickMenuMod.Instance, row);
        var i = Math.Max(0, Array.IndexOf(KeyChoices, cur));
        SetKey(row, KeyChoices[((i + dir) % KeyChoices.Length + KeyChoices.Length) % KeyChoices.Length]);
    }

    static string? NameOf(PadButton b) => b switch
    {
        PadButton.A => "A / Cross", PadButton.B => "B / Circle", PadButton.X => "X / Square", PadButton.Y => "Y / Triangle",
        PadButton.LeftShoulder => "LB / L1", PadButton.RightShoulder => "RB / R1", PadButton.LeftStick => "LSB / L3", PadButton.RightStick => "RSB / R3",
        PadButton.Back => "View / Share", PadButton.Start => "Menu / Options", _ => null,
    };
}
