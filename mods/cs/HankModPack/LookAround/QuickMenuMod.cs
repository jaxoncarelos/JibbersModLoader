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
    // The buttons are changed in Settings > Buttons (binds window): only the menu look is listed in Options > Mods.
    protected override bool HiddenInModsTab(Setting s) => s.Section == "Buttons";
    protected override string PartId => "quick_menu";
    public override string Name => "Hank Mod Pack: Quick Menu";
    public override string Author => "Hank";
    public override string Description => "The panel shown on the left while D-pad down is held: button pictures (PlayStation / Xbox), size, opacity, and which button does what.";

    public static QuickMenuMod? Instance { get; private set; }
    public QuickMenuMod() { Instance = this; }

    public override void OnLoad() { ImportPackSettings(); LoadKeyboard(); LoadSets(); LoadFavorites(); Instance = this; MigrateButtons(); }

    /// <summary>
    /// Once: the buttons move to the new layout (D-pad down + A = shortcuts, + R1 = POV camera, + L1 = detach both skis; the themes are in Settings now),
    /// whatever the old settings had. After that the buttons are yours to change.
    /// </summary>
    void MigrateButtons()
    {
        try
        {
            // Layout 2: D-pad down + R1 = POV camera, + L1 = detach both skis, themes in Settings. Layout 3: + Circle = shortcuts, + Cross = Follow cam.
            void Move(string marker, params (string Key, string Value)[] buttons)
            {
                var file = Path.Combine(Bootstrap.ModsDir, marker);
                if (File.Exists(file)) return;
                foreach (var (key, value) in buttons) { SetKeyboard(key, null); SetChoice(key, value); }
                File.WriteAllText(file, "The quick menu buttons were moved to this layout.");
                Log($"quick menu: buttons moved to the new layout ({marker})");
            }
            Move("quick_menu_layout2.done", ("menu_cam_pov", "RB / R1"), ("menu_detach", "LB / L1"), ("menu_hud_prev", "None"), ("menu_hud_next", "None"));
            Move("quick_menu_layout3.done", ("menu_shortcuts", "B / Circle"), ("menu_cam_follow", "A / Cross"));
            Move("quick_menu_layout4.done", ("menu_cam_drone", "RT / R2"));
            // Layout 6: Triangle = Cameras, L1 = Input visualizer, L2 = Themes (HUD on / off has no button by default; Detach is on L3 from layout 7).
            Move("quick_menu_layout6.done", ("menu_detach", "None"), ("menu_hud", "None"), ("menu_padov", "LB / L1"), ("menu_themes", "LT / L2"), ("menu_camera", "Y / Triangle"));
            Move("quick_menu_layout7.done", ("menu_detach", "LSB / L3"));   // Detach both skis on L3
            // Share (Mods), Options (Settings) and R3 (look behind) are fixed: no other action may sit on them.
            var layout5 = Path.Combine(Bootstrap.ModsDir, "quick_menu_layout5.done");
            if (!File.Exists(layout5))
            {
                var fixedButtons = new[] { "View / Share", "Menu / Options", "RSB / R3" };
                foreach (var key in new[] { "menu_camera", "menu_spawn", "menu_fun", "menu_overlay", "menu_hud", "menu_hud_prev", "menu_hud_next", "menu_cam_pov", "menu_cam_follow", "menu_cam_drone", "menu_cam_game", "menu_shortcuts", "menu_detach" })
                    if (GetChoice(key) is { } c && fixedButtons.Contains(c)) SetChoice(key, "None");
                File.WriteAllText(layout5, "ok");
            }
        }
        catch (Exception e) { Log("quick menu: could not move the buttons: " + e.Message); }
    }

    // Keyboard keys given to actions (action key -> name of the key, "F6"). The pad choices cannot hold them, so they live in their own file.
    readonly Dictionary<string, string> keyboard = new();
    string KeyboardFile => Path.Combine(Bootstrap.ModsDir, "quick_menu_keyboard.json");

    void LoadKeyboard()
    {
        try
        {
            keyboard.Clear();
            if (File.Exists(KeyboardFile) && JsonNode.Parse(File.ReadAllText(KeyboardFile)) is JsonObject o)
                foreach (var (k, v) in o) if (v?.GetValue<string>() is { Length: > 0 } name) keyboard[k] = name;
        }
        catch (Exception e) { Log("quick menu: could not read the keyboard keys: " + e.Message); }
    }

    /// <summary>The keyboard key given to an action (by its settings key), or null.</summary>
    public string? GetKeyboard(string key) { lock (keyboard) return keyboard.TryGetValue(key, out var n) ? n : null; }

    /// <summary>Gives an action a keyboard key (null removes it) and saves.</summary>
    public void SetKeyboard(string key, string? name)
    {
        try
        {
            lock (keyboard)
            {
                if (name is null) keyboard.Remove(key); else keyboard[key] = name;
                var o = new JsonObject();
                foreach (var (k, v) in keyboard) o[k] = v;
                File.WriteAllText(KeyboardFile, o.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            }
        }
        catch (Exception e) { Log("quick menu: could not save the keyboard key: " + e.Message); }
    }

    // The favorite commands (the "Favorites" tab of the command choice).
    readonly List<string> favorites = new();
    string FavoritesFile => Path.Combine(Bootstrap.ModsDir, "quick_menu_favorites.json");

    void LoadFavorites()
    {
        try
        {
            favorites.Clear();
            if (File.Exists(FavoritesFile))
            {
                if (JsonNode.Parse(File.ReadAllText(FavoritesFile)) is JsonArray arr)
                    foreach (var n in arr) if (n?.GetValue<string>() is { Length: > 0 } c) favorites.Add(c);
            }
            else { ResetFavorites(); return; }
            // Once: the starting favorites changed (Add Ghost, Ghost Menu, Detach both skis, Builder Mode); nothing had been chosen by hand before.
            var marker = Path.Combine(Bootstrap.ModsDir, "quick_menu_favorites2.done");
            if (!File.Exists(marker)) { ResetFavorites(); File.WriteAllText(marker, "The favorites were reset to the new starting list."); }
        }
        catch (Exception e) { Log("quick menu: could not read the favorites: " + e.Message); }
    }

    void ResetFavorites()
    {
        favorites.Clear();
        favorites.AddRange(new[] { "add_ghost", "ghost_menu", "detach left ; detach right", "/login bob" });
        SaveFavorites();
        try { File.WriteAllText(Path.Combine(Bootstrap.ModsDir, "quick_menu_favorites2.done"), "ok"); } catch { }
    }

    void SaveFavorites()
    {
        try
        {
            var arr = new JsonArray();
            foreach (var c in favorites) arr.Add(c);
            File.WriteAllText(FavoritesFile, arr.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception e) { Log("quick menu: could not save the favorites: " + e.Message); }
    }

    public List<string> Favorites => favorites.ToList();
    public bool IsFavorite(string command) => favorites.Contains(command, StringComparer.OrdinalIgnoreCase);

    /// <summary>Adds the command to the favorites, or removes it when it is there. Returns true when it is a favorite now.</summary>
    public bool ToggleFavorite(string command)
    {
        var at = favorites.FindIndex(c => c.Equals(command, StringComparison.OrdinalIgnoreCase));
        if (at >= 0) favorites.RemoveAt(at); else favorites.Add(command);
        SaveFavorites();
        return at < 0;
    }

    // The three shortcut sets: a name and, for each pad button, the command it runs.
    public const int SetCount = 3;
    readonly string[] setNames = { "Set 1", "Set 2", "Set 3" };
    readonly Dictionary<string, string>[] setBinds = { new(), new(), new() };
    string SetsFile => Path.Combine(Bootstrap.ModsDir, "quick_menu_sets.json");

    void LoadSets()
    {
        try
        {
            if (File.Exists(SetsFile) && JsonNode.Parse(File.ReadAllText(SetsFile)) is JsonObject o && o["sets"] is JsonArray arr)
                for (var i = 0; i < Math.Min(SetCount, arr.Count); i++)
                    if (arr[i] is JsonObject set)
                    {
                        if (set["name"]?.GetValue<string>() is { Length: > 0 } n) setNames[i] = n;
                        setBinds[i].Clear();
                        if (set["binds"] is JsonObject b) foreach (var (k, v) in b) if (v?.GetValue<string>() is { Length: > 0 } c) setBinds[i][k] = c;
                    }
        }
        catch (Exception e) { Log("quick menu: could not read the shortcut sets: " + e.Message); }
    }

    void SaveSets()
    {
        try
        {
            var arr = new JsonArray();
            for (var i = 0; i < SetCount; i++)
            {
                var b = new JsonObject();
                foreach (var (k, v) in setBinds[i]) b[k] = v;
                arr.Add(new JsonObject { ["name"] = setNames[i], ["binds"] = b });
            }
            File.WriteAllText(SetsFile, new JsonObject { ["sets"] = arr }.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception e) { Log("quick menu: could not save the shortcut sets: " + e.Message); }
    }

    public string GetSetName(int set) => setNames[set];
    public void SetSetName(int set, string name) { if (name.Trim().Length > 0) { setNames[set] = name.Trim(); SaveSets(); } }
    public string? GetSetBind(int set, string button) => setBinds[set].TryGetValue(button, out var c) ? c : null;
    public int SetBindCount(int set) => setBinds[set].Count;
    public void SetSetBind(int set, string button, string? command)
    {
        if (command is null) setBinds[set].Remove(button); else setBinds[set][button] = command;
        SaveSets();
    }

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
            WithAllSettings(() =>
            {
                if (StoreFld?.GetValue(this) is SettingsStore store)
                {
                    store.Set(key, JsonValue.Create(value)!);
                    store.GetType().GetMethod("Save", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.Invoke(store, null);
                }
            });
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

    [Choice("Quick Actions (3 sets)", "B / Circle", "None", "A / Cross", "B / Circle", "X / Square", "Y / Triangle", "LB / L1", "RB / R1", "LT / L2", "RT / R2", "LSB / L3", "RSB / R3", "View / Share", "Menu / Options", Key = "menu_shortcuts", Section = "Buttons",
        Tooltip = "With D-pad down held: opens the three Quick Actions sets (they are also in Settings). Each set gives a command to every button of the controller.")]
    public string ShortcutsButtonName { get; private set; } = "B / Circle";

    [Choice("Detach both skis", "LSB / L3", "None", "A / Cross", "B / Circle", "X / Square", "Y / Triangle", "LB / L1", "RB / R1", "LT / L2", "RT / R2", "LSB / L3", "RSB / R3", "View / Share", "Menu / Options", Key = "menu_detach", Section = "Buttons",
        Tooltip = "With D-pad down held: detaches both skis (the console commands detach left and detach right).")]
    public string DetachButtonName { get; private set; } = "LSB / L3";

    [Choice("Quick Actions set 1", "None", "None", "A / Cross", "B / Circle", "X / Square", "Y / Triangle", "LB / L1", "RB / R1", "LT / L2", "RT / R2", "LSB / L3", "RSB / R3", "View / Share", "Menu / Options", Key = "menu_set1", Section = "Buttons",
        Tooltip = "With D-pad down held: opens the first Quick Actions set straight away.")]
    public string Set1ButtonName { get; private set; } = "None";
    [Choice("Quick Actions set 2", "None", "None", "A / Cross", "B / Circle", "X / Square", "Y / Triangle", "LB / L1", "RB / R1", "LT / L2", "RT / R2", "LSB / L3", "RSB / R3", "View / Share", "Menu / Options", Key = "menu_set2", Section = "Buttons",
        Tooltip = "With D-pad down held: opens the second Quick Actions set straight away.")]
    public string Set2ButtonName { get; private set; } = "None";
    [Choice("Quick Actions set 3", "None", "None", "A / Cross", "B / Circle", "X / Square", "Y / Triangle", "LB / L1", "RB / R1", "LT / L2", "RT / R2", "LSB / L3", "RSB / R3", "View / Share", "Menu / Options", Key = "menu_set3", Section = "Buttons",
        Tooltip = "With D-pad down held: opens the third Quick Actions set straight away.")]
    public string Set3ButtonName { get; private set; } = "None";

    [Choice("Look behind (while D-pad down is held)", "RSB / R3", "None", "A / Cross", "B / Circle", "X / Square", "Y / Triangle", "LB / L1", "RB / R1", "LT / L2", "RT / R2", "LSB / L3", "RSB / R3", "View / Share", "Menu / Options", Key = "look_behind", Section = "Buttons",
        Tooltip = "Hold it with D-pad down to look straight behind the rider.")]
    public string RearButtonName { get; private set; } = "RSB / R3";
    [Choice("Mods window", "View / Share", "None", "A / Cross", "B / Circle", "X / Square", "Y / Triangle", "LB / L1", "RB / R1", "LT / L2", "RT / R2", "LSB / L3", "RSB / R3", "View / Share", "Menu / Options", Key = "menu_mods", Section = "Buttons",
        Tooltip = "Hold it with D-pad down to open the window that switches the game's mods on and off.")]
    public string ModsButtonName { get; private set; } = "View / Share";
    [Choice("Cameras (menu)", "Y / Triangle", "None", "A / Cross", "B / Circle", "X / Square", "Y / Triangle", "LB / L1", "RB / R1", "LT / L2", "RT / R2", "LSB / L3", "RSB / R3", "View / Share", "Menu / Options", Key = "menu_camera", Section = "Buttons",
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
    [Choice("Input visualizer on / off", "LB / L1", "None", "A / Cross", "B / Circle", "X / Square", "Y / Triangle", "LB / L1", "RB / R1", "LT / L2", "RT / R2", "LSB / L3", "RSB / R3", "View / Share", "Menu / Options", Key = "menu_padov", Section = "Buttons",
        Tooltip = "Shows or hides the controller / keyboard overlay (it replaces the game's input display).")]
    public string PadOvButtonName { get; private set; } = "LB / L1";
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

    [Choice("HUD theme: previous", "None", "None", "A / Cross", "B / Circle", "X / Square", "Y / Triangle", "LB / L1", "RB / R1", "LT / L2", "RT / R2", "LSB / L3", "RSB / R3", "View / Share", "Menu / Options", Key = "menu_hud_prev", Section = "Buttons",
        Tooltip = "Switches the Hank Mod Pack: HUD to the previous theme (the themes are also in Settings > Theme, with L1 / R1). While the quick menu is up this button no longer zooms the free look.")]
    public string HudPrevName { get; private set; } = "None";
    [Choice("HUD theme: next", "None", "None", "A / Cross", "B / Circle", "X / Square", "Y / Triangle", "LB / L1", "RB / R1", "LT / L2", "RT / R2", "LSB / L3", "RSB / R3", "View / Share", "Menu / Options", Key = "menu_hud_next", Section = "Buttons",
        Tooltip = "Switches the Hank Mod Pack: HUD to the next theme. While the quick menu is up this button no longer zooms the free look.")]
    public string HudNextName { get; private set; } = "None";

    [Choice("Themes (opens the theme window)", "LT / L2", "None", "A / Cross", "B / Circle", "X / Square", "Y / Triangle", "LB / L1", "RB / R1", "LT / L2", "RT / R2", "LSB / L3", "RSB / R3", "View / Share", "Menu / Options", Key = "menu_themes", Section = "Buttons",
        Tooltip = "Opens the HUD theme window directly.")]
    public string ThemesButtonName { get; private set; } = "LT / L2";
    [Choice("HUD on / off (hides it completely)", "None", "None", "A / Cross", "B / Circle", "X / Square", "Y / Triangle", "LB / L1", "RB / R1", "LT / L2", "RT / R2", "LSB / L3", "RSB / R3", "View / Share", "Menu / Options", Key = "menu_hud", Section = "Buttons",
        Tooltip = "Hides or shows the whole HUD (the mod's windows and the game's own). Menus and windows still show.")]
    public string HudButtonName { get; private set; } = "None";

    [Choice("Settings (camera settings, binds)", "Menu / Options", "None", "A / Cross", "B / Circle", "X / Square", "Y / Triangle", "LB / L1", "RB / R1", "LT / L2", "RT / R2", "LSB / L3", "RSB / R3", "View / Share", "Menu / Options", Key = "menu_editor2", Section = "Buttons",
        Tooltip = "With D-pad down held: opens Settings, where you pick Camera settings (the window that edits the play cameras: L1 / R1 = camera tab, D-pad up / down = option, left / right = change, A = switch on / off, Y = reset, B = close) or Binds (assign a key to a command of the game).")]
    public string EditorButtonName { get; private set; } = "Menu / Options";

    [Choice("Direct camera: POV", "RB / R1", "None", "A / Cross", "B / Circle", "X / Square", "Y / Triangle", "LB / L1", "RB / R1", "LT / L2", "RT / R2", "LSB / L3", "RSB / R3", "View / Share", "Menu / Options", Key = "menu_cam_pov", Section = "Buttons",
        Tooltip = "With D-pad down held: switches straight to the POV camera (no camera list).")]
    public string DirectPovName { get; private set; } = "RB / R1";
    [Choice("Direct camera: Follow cam", "A / Cross", "None", "A / Cross", "B / Circle", "X / Square", "Y / Triangle", "LB / L1", "RB / R1", "LT / L2", "RT / R2", "LSB / L3", "RSB / R3", "View / Share", "Menu / Options", Key = "menu_cam_follow", Section = "Buttons",
        Tooltip = "With D-pad down held: switches straight to the Follow cam.")]
    public string DirectFollowName { get; private set; } = "A / Cross";
    [Choice("Direct camera: Drone", "RT / R2", "None", "A / Cross", "B / Circle", "X / Square", "Y / Triangle", "LB / L1", "RB / R1", "LT / L2", "RT / R2", "LSB / L3", "RSB / R3", "View / Share", "Menu / Options", Key = "menu_cam_drone", Section = "Buttons",
        Tooltip = "With D-pad down held: switches straight to the Drone camera.")]
    public string DirectDroneName { get; private set; } = "RT / R2";
    [Choice("Direct camera: Game camera", "None", "None", "A / Cross", "B / Circle", "X / Square", "Y / Triangle", "LB / L1", "RB / R1", "LT / L2", "RT / R2", "LSB / L3", "RSB / R3", "View / Share", "Menu / Options", Key = "menu_cam_game", Section = "Buttons",
        Tooltip = "With D-pad down held: back to the game's own camera (holding D-pad up does it too).")]
    public string DirectGameName { get; private set; } = "None";
}
