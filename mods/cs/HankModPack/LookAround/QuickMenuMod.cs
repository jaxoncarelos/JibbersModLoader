using System.Reflection;
using System.Text.Json.Nodes;
using Jibbers.Sdk;

namespace HankModPack;

/// <summary>
/// The quick menu (hold D-pad down): its own entry in Options > Mods, with the look of the panel and every button. The menu itself runs inside
/// <see cref="LookAroundMod"/>, which owns the D-pad down hold.
/// </summary>
public sealed class QuickMenuMod : PackPart
{
    protected override string PartId => "quick_menu";
    public override string Name => "Hank Mod Pack: Quick Menu";
    public override string Author => "Hank";
    public override string Description => "The panel shown on the left while D-pad down is held: button pictures (PlayStation / Xbox), size, opacity, and which button does what.";

    public static QuickMenuMod? Instance { get; private set; }
    public QuickMenuMod() { Instance = this; }

    public override void OnLoad() { ImportPackSettings(); Instance = this; }

    static readonly FieldInfo? StoreFld = typeof(JibbersMod).GetField("Store", BindingFlags.Instance | BindingFlags.NonPublic);
    // Looked up once per key: this runs for every row, every frame, while the button assignment window is open.
    readonly Dictionary<string, PropertyInfo?> choiceProps = new();
    PropertyInfo? ChoiceProp(string key)
    {
        lock (choiceProps)
        {
            if (!choiceProps.TryGetValue(key, out var pi))
                choiceProps[key] = pi = GetType().GetProperties()
                    .FirstOrDefault(p => p.GetCustomAttributes(typeof(SettingAttribute), true).FirstOrDefault() is SettingAttribute a && a.Key == key);
            return pi;
        }
    }

    /// <summary>The current value of a button option (by its settings key).</summary>
    public string? GetChoice(string key) => ChoiceProp(key)?.GetValue(this) as string;

    /// <summary>Changes a button option and saves it. Written straight into the settings store (SetSetting would wait for the main thread, which is the caller).</summary>
    public void SetChoice(string key, string value)
    {
        try
        {
            ChoiceProp(key)?.SetValue(this, value);
            if (StoreFld?.GetValue(this) is SettingsStore store)
            {
                store.Set(key, JsonValue.Create(value)!);
                store.GetType().GetMethod("Save", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.Invoke(store, null);
            }
        }
        catch (Exception e) { Log("quick menu: could not save the button: " + e.Message); }
    }


    [Toggle("Show the quick menu while D-pad down is held", true, Key = "menu", Section = "Quick menu")]
    public bool ShowMenu { get; private set; } = true;

    [Choice("Button pictures", "Auto", "Auto", "PlayStation", "Xbox", Key = "icons", Section = "Quick menu",
        Tooltip = "Auto looks at the name of the connected controller. If Steam Input or another program makes a PlayStation pad look like an Xbox one, pick the pictures here.")]
    public string IconStyle { get; private set; } = "Auto";

    [Slider("Menu size %", 50, 30, 160, 5, Key = "menu_scale", Section = "Quick menu")]
    public float MenuScale { get; private set; } = 50;

    [Toggle("Stop the game while the camera editor is open", false, Key = "editor_freeze", Section = "Quick menu",
        Tooltip = "Off (default): the game keeps running and you keep steering the rider while you tune the camera with the D-pad, L1 / R1 and Share. On: the rider stands still and the controls are off while the window is open.")]
    public bool EditorFreeze { get; private set; }

    [Slider("Menu opacity %", 100, 40, 100, 5, Key = "menu_opacity", Section = "Quick menu")]
    public float MenuOpacity { get; private set; } = 100;

    [Slider("Stays on screen after a choice (seconds)", 1.5, 0.5, 4, 0.1, Key = "menu_linger", Section = "Quick menu")]
    public float MenuLinger { get; private set; } = 1.5f;

    [Choice("Look behind (while D-pad down is held)", "RSB / R3", "None", "A / Cross", "B / Circle", "X / Square", "Y / Triangle", "LB / L1", "RB / R1", "LT / L2", "RT / R2", "LSB / L3", "RSB / R3", "View / Share", "Menu / Options", Key = "look_behind", Section = "Buttons",
        Tooltip = "Hold it with D-pad down to look straight behind the rider.")]
    public string RearButtonName { get; private set; } = "RSB / R3";
    [Choice("Mods window", "View / Share", "None", "A / Cross", "B / Circle", "X / Square", "Y / Triangle", "LB / L1", "RB / R1", "LT / L2", "RT / R2", "LSB / L3", "RSB / R3", "View / Share", "Menu / Options", Key = "menu_mods", Section = "Buttons",
        Tooltip = "Hold it with D-pad down to open the window that switches the game's mods on and off.")]
    public string ModsButtonName { get; private set; } = "View / Share";
    [Choice("Camera menu", "Y / Triangle", "None", "A / Cross", "B / Circle", "X / Square", "Y / Triangle", "LB / L1", "RB / R1", "LT / L2", "RT / R2", "LSB / L3", "RSB / R3", "View / Share", "Menu / Options", Key = "menu_camera", Section = "Buttons",
        Tooltip = "Opens the camera list (POV, Follow cam, Drone, Game camera).")]
    public string CameraButtonName { get; private set; } = "Y / Triangle";
    [Choice("Spawn menu", "X / Square", "None", "A / Cross", "B / Circle", "X / Square", "Y / Triangle", "LB / L1", "RB / R1", "LT / L2", "RT / R2", "LSB / L3", "RSB / R3", "View / Share", "Menu / Options", Key = "menu_spawn", Section = "Buttons",
        Tooltip = "Opens the Improved Spawn panel.")]
    public string SpawnButtonName { get; private set; } = "X / Square";
    [Choice("Fun overlays on / off", "None", "None", "A / Cross", "B / Circle", "X / Square", "Y / Triangle", "LB / L1", "RB / R1", "LT / L2", "RT / R2", "LSB / L3", "RSB / R3", "View / Share", "Menu / Options", Key = "menu_fun", Section = "Buttons",
        Tooltip = "Turns the Hank Mod Pack: Fun mod on or off.")]
    public string FunButtonName { get; private set; } = "None";
    [Choice("Banner overlays on / off", "None", "None", "A / Cross", "B / Circle", "X / Square", "Y / Triangle", "LB / L1", "RB / R1", "LT / L2", "RT / R2", "LSB / L3", "RSB / R3", "View / Share", "Menu / Options", Key = "menu_overlay", Section = "Buttons",
        Tooltip = "Turns the Hank Mod Pack: Overlay mod on or off.")]
    public string OverlayButtonName { get; private set; } = "None";
    [Choice("Camera list: POV", "A / Cross", "None", "A / Cross", "B / Circle", "X / Square", "Y / Triangle", "LB / L1", "RB / R1", "LT / L2", "RT / R2", "LSB / L3", "RSB / R3", "View / Share", "Menu / Options", Key = "cam_pov", Section = "Buttons",
        Tooltip = "In the camera list, picks the POV camera.")]
    public string PovButtonName { get; private set; } = "A / Cross";
    [Choice("Camera list: Follow cam", "X / Square", "None", "A / Cross", "B / Circle", "X / Square", "Y / Triangle", "LB / L1", "RB / R1", "LT / L2", "RT / R2", "LSB / L3", "RSB / R3", "View / Share", "Menu / Options", Key = "cam_follow", Section = "Buttons",
        Tooltip = "In the camera list, picks the Follow cam.")]
    public string FollowButtonName { get; private set; } = "X / Square";
    [Choice("Camera list: Drone", "Y / Triangle", "None", "A / Cross", "B / Circle", "X / Square", "Y / Triangle", "LB / L1", "RB / R1", "LT / L2", "RT / R2", "LSB / L3", "RSB / R3", "View / Share", "Menu / Options", Key = "cam_drone", Section = "Buttons",
        Tooltip = "In the camera list, picks the Drone camera.")]
    public string DroneButtonName { get; private set; } = "Y / Triangle";
    [Choice("Camera list: Game camera", "B / Circle", "None", "A / Cross", "B / Circle", "X / Square", "Y / Triangle", "LB / L1", "RB / R1", "LT / L2", "RT / R2", "LSB / L3", "RSB / R3", "View / Share", "Menu / Options", Key = "cam_default", Section = "Buttons",
        Tooltip = "In the camera list, goes back to the game's own camera.")]
    public string DefaultButtonName { get; private set; } = "B / Circle";

    [Choice("HUD theme: previous", "LB / L1", "None", "A / Cross", "B / Circle", "X / Square", "Y / Triangle", "LB / L1", "RB / R1", "LT / L2", "RT / R2", "LSB / L3", "RSB / R3", "View / Share", "Menu / Options", Key = "menu_hud_prev", Section = "Buttons",
        Tooltip = "Switches the Hank Mod Pack: HUD to the previous theme. While the quick menu is up this button no longer zooms the free look.")]
    public string HudPrevName { get; private set; } = "LB / L1";
    [Choice("HUD theme: next", "RB / R1", "None", "A / Cross", "B / Circle", "X / Square", "Y / Triangle", "LB / L1", "RB / R1", "LT / L2", "RT / R2", "LSB / L3", "RSB / R3", "View / Share", "Menu / Options", Key = "menu_hud_next", Section = "Buttons",
        Tooltip = "Switches the Hank Mod Pack: HUD to the next theme. While the quick menu is up this button no longer zooms the free look.")]
    public string HudNextName { get; private set; } = "RB / R1";

    [Choice("HUD on / off (hides it completely)", "LT / L2", "None", "A / Cross", "B / Circle", "X / Square", "Y / Triangle", "LB / L1", "RB / R1", "LT / L2", "RT / R2", "LSB / L3", "RSB / R3", "View / Share", "Menu / Options", Key = "menu_hud", Section = "Buttons",
        Tooltip = "Hides or shows the whole HUD (the mod's windows and the game's own). Menus and windows still show.")]
    public string HudButtonName { get; private set; } = "LT / L2";

    [Choice("Camera editor", "Menu / Options", "None", "A / Cross", "B / Circle", "X / Square", "Y / Triangle", "LB / L1", "RB / R1", "LT / L2", "RT / R2", "LSB / L3", "RSB / R3", "View / Share", "Menu / Options", Key = "menu_editor2", Section = "Buttons",
        Tooltip = "With D-pad down held: opens the window that edits the play cameras (L1 / R1 = camera tab, D-pad up / down = option, left / right = change, A = switch on / off, Y = reset, B = close).")]
    public string EditorButtonName { get; private set; } = "Menu / Options";

    [Choice("Direct camera: POV", "A / Cross", "None", "A / Cross", "B / Circle", "X / Square", "Y / Triangle", "LB / L1", "RB / R1", "LT / L2", "RT / R2", "LSB / L3", "RSB / R3", "View / Share", "Menu / Options", Key = "menu_cam_pov", Section = "Buttons",
        Tooltip = "With D-pad down held: switches straight to the POV camera (no camera list).")]
    public string DirectPovName { get; private set; } = "A / Cross";
    [Choice("Direct camera: Follow cam", "B / Circle", "None", "A / Cross", "B / Circle", "X / Square", "Y / Triangle", "LB / L1", "RB / R1", "LT / L2", "RT / R2", "LSB / L3", "RSB / R3", "View / Share", "Menu / Options", Key = "menu_cam_follow", Section = "Buttons",
        Tooltip = "With D-pad down held: switches straight to the Follow cam.")]
    public string DirectFollowName { get; private set; } = "B / Circle";
    [Choice("Direct camera: Drone", "None", "None", "A / Cross", "B / Circle", "X / Square", "Y / Triangle", "LB / L1", "RB / R1", "LT / L2", "RT / R2", "LSB / L3", "RSB / R3", "View / Share", "Menu / Options", Key = "menu_cam_drone", Section = "Buttons",
        Tooltip = "With D-pad down held: switches straight to the Drone camera.")]
    public string DirectDroneName { get; private set; } = "None";
    [Choice("Direct camera: Game camera", "None", "None", "A / Cross", "B / Circle", "X / Square", "Y / Triangle", "LB / L1", "RB / R1", "LT / L2", "RT / R2", "LSB / L3", "RSB / R3", "View / Share", "Menu / Options", Key = "menu_cam_game", Section = "Buttons",
        Tooltip = "With D-pad down held: back to the game's own camera (holding D-pad up does it too).")]
    public string DirectGameName { get; private set; } = "None";
}
