using System.Reflection;
using System.Text.Json.Nodes;
using Jibbers.Sdk;

namespace HankModPack;

/// <summary>
/// A mod of the Hank Mod Pack. Each one has its own entry in Options > Mods, named "Hank Mod Pack: ..." so they sit together.
/// </summary>
public abstract class PackPart : JibbersMod
{
    protected abstract string PartId { get; }
    public sealed override string Id => PartId;
    public override string Author => "Hank";

    public sealed override void OnSettingChanged(string key, JsonNode value)
    {
        try { OnPackSettingChanged(key, value); }
        finally
        {
            if (key == "enabled" && !value.GetValue<bool>())
                MainThread.Later(() => { if (!Enabled) OnDeactivated(); });
        }
    }

    public sealed override void OnUnload()
    {
        CameraCallbacks.Follow.Remove(this);
        CameraCallbacks.Recorder.Remove(this);
        MainThread.Run(() =>
        {
            try { OnDeactivated(); }
            finally { OnPackUnload(); }
        });
    }

    protected virtual void OnPackSettingChanged(string key, JsonNode value) { }
    protected void Cleanup(params Action[] actions) => CleanupActions.Run(error => Log("cleanup: " + error.Message), actions);

    internal void CameraFailed(Exception error)
    {
        Cleanup(() => Log("camera feature stopped after an error: " + error.Message), OnDeactivated);
    }



    // ---- The settings files live in JibbersMods\Hank Mod Pack\Settings (the loader would put them next to the DLLs).
    static readonly FieldInfo? PathField = typeof(SettingsStore).GetField("path", BindingFlags.Instance | BindingFlags.NonPublic);
    string SettingsDir => Path.Combine(Bootstrap.ModsDir, "Hank Mod Pack", "Settings");
    /// <summary>Named after the mod ("Quick Menu.json", "HUD.json"...).</summary>
    string SettingsFilePath => Path.Combine(SettingsDir, string.Concat(Name.Replace("Hank Mod Pack: ", "").Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)) + ".json");

    /// <summary>The first version of the Settings folder named the files after the mod id: they take the mod's name.</summary>
    void RenameMovedFile()
    {
        try
        {
            var byId = Path.Combine(SettingsDir, Id + ".settings.json");
            if (File.Exists(byId) && !File.Exists(SettingsFilePath)) File.Move(byId, SettingsFilePath);
        }
        catch (Exception e) { Log("could not rename the settings file: " + e.Message); }
    }


    static int settled;
    static int PartCount => typeof(PackPart).Assembly.GetTypes().Count(t => !t.IsAbstract && t.IsSubclassOf(typeof(PackPart)));

    /// <summary>
    /// The settings used to be next to the DLLs (JibbersMods\&lt;id&gt;.settings.json, also under the ids of earlier versions): once a mod has its file in
    /// Hank Mod Pack\Settings the old ones are deleted, so nothing is read twice. The file shared by version 1.1 goes when every mod of the pack has been through here.
    /// </summary>
    void RemoveOldFiles()
    {
        try
        {
            if (!File.Exists(SettingsFilePath)) return;
            foreach (var id in new[] { Id, LegacyId })
            {
                if (id is null) continue;
                var old = Path.Combine(Bootstrap.ModsDir, id + ".settings.json");
                if (File.Exists(old)) { File.Delete(old); Log("old settings file removed: " + old); }
            }
            if (Interlocked.Increment(ref settled) == PartCount)
                foreach (var name in new[] { "hank_mod_pack.settings.json", "hank_mod_pack_info.settings.json" })
                {
                    var shared = Path.Combine(Bootstrap.ModsDir, name);
                    if (File.Exists(shared)) { File.Delete(shared); Log("old settings file removed: " + shared); }
                }
        }
        catch (Exception e) { Log("could not remove the old settings files: " + e.Message); }
    }

    void RelocateSettings()
    {
        try
        {
            if (StoreField?.GetValue(this) is not SettingsStore store || PathField is null) return;
            var target = SettingsFilePath;
            var current = PathField.GetValue(store) as string;
            if (string.Equals(current, target, StringComparison.OrdinalIgnoreCase)) return;
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            PathField.SetValue(store, target);
            if (File.Exists(target))
            {
                // Already moved before: its values win over the ones the loader just read from the old place.
                if (JsonNode.Parse(File.ReadAllText(target)) is JsonObject saved)
                    foreach (var (key, value) in saved)
                        if (value is not null) { try { store.Set(key, value.DeepClone()); } catch (ArgumentException) { } }
                PropertySettings?.GetMethod("Load", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)?.Invoke(null, new object[] { this });
            }
            else store.GetType().GetMethod("Save", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.Invoke(store, null);
            if (File.Exists(target) && current is not null && File.Exists(current))
            {
                try { File.Delete(current); Log("settings moved to " + target); } catch (Exception e) { Log("could not remove the old settings file: " + e.Message); }
            }
        }
        catch (Exception e) { Log("could not move the settings file: " + e.Message); }
    }

    // ---- Options > Mods: only the settings that have no other place are listed, in a sensible order.
    /// <summary>True for a setting that is changed somewhere else (quick menu, a window of the mod): it is left out of Options > Mods but still saved and used.</summary>
    protected virtual bool HiddenInModsTab(Setting s) => false;
    /// <summary>Order of the sections in Options > Mods (the others follow).</summary>
    protected virtual string[] SectionOrder => Array.Empty<string>();

    static readonly FieldInfo? ListField = typeof(SettingsStore).GetField("settings", BindingFlags.Instance | BindingFlags.NonPublic);
    readonly object tidyGate = new();
    List<Setting>? fullList, shownList;

    void TidyModsTab()
    {
        try
        {
            if (StoreField?.GetValue(this) is not SettingsStore store || ListField is null) return;
            lock (tidyGate)
            {
                var all = (List<Setting>)ListField.GetValue(store)!;
                var order = SectionOrder;
                int Rank(Setting s) { var i = Array.IndexOf(order, s.Section); return s.Key == "enabled" ? -1 : i < 0 ? order.Length : i; }
                fullList = all;
                shownList = all.Where(s => s.Key == "enabled" || !HiddenInModsTab(s)).Select((s, i) => (s, i)).OrderBy(t => Rank(t.s)).ThenBy(t => t.i).Select(t => t.s).ToList();
                ListField.SetValue(store, shownList);
            }
        }
        catch (Exception e) { Log("mods tab: could not tidy the settings: " + e.Message); }
    }

    /// <summary>Runs <paramref name="action"/> with every setting known to the store (the hidden ones too), e.g. to save a value.</summary>
    internal void WithAllSettings(Action action)
    {
        lock (tidyGate)
        {
            var store = StoreField?.GetValue(this) as SettingsStore;
            var swap = store is not null && ListField is not null && fullList is not null;
            if (swap) ListField!.SetValue(store, fullList);
            try { action(); }
            finally { if (swap) ListField!.SetValue(store, shownList); }
        }
    }

    /// <summary>Same as the SDK's SetSetting, also for settings that are hidden from Options > Mods.</summary>
    public new void SetSetting(string key, JsonNode value) => WithAllSettings(() => base.SetSetting(key, value));

    protected virtual void OnDeactivated() { }
    protected virtual void OnPackUnload() { }

    static readonly Type? PropertySettings = typeof(JibbersMod).Assembly.GetType("Jibbers.Sdk.PropertySettings");
    static readonly FieldInfo? StoreField = typeof(JibbersMod).GetField("Store", BindingFlags.Instance | BindingFlags.NonPublic);

    /// <summary>
    /// Version 1.1 kept every setting in hank_mod_pack.settings.json. The first time a mod loads without its own file,
    /// it takes its values from there (the old file is left untouched).
    /// </summary>
    /// <summary>Previous id of a renamed part: its settings file is taken over the first time.</summary>
    protected virtual string? LegacyId => null;

    protected void ImportPackSettings()
    {
        try { RenameMovedFile(); ImportCore(); RelocateSettings(); RemoveOldFiles(); } finally { TidyModsTab(); }
    }

    void ImportCore()
    {
        try
        {
            var own = Path.Combine(Bootstrap.ModsDir, Id + ".settings.json");
            if (File.Exists(SettingsFilePath)) return;
            var old = Path.Combine(Bootstrap.ModsDir, "hank_mod_pack.settings.json");
            if (LegacyId is { } lid && Path.Combine(Bootstrap.ModsDir, lid + ".settings.json") is { } renamed && File.Exists(renamed)) old = renamed;
            if (File.Exists(own) || !File.Exists(old) || StoreField?.GetValue(this) is not SettingsStore store) return;
            if (JsonNode.Parse(File.ReadAllText(old)) is not JsonObject saved) return;
            var keys = Settings.Select(s => s.Key).ToHashSet();
            foreach (var (key, value) in saved)
                if (key != "enabled" && value is not null && keys.Contains(key))
                    store.Set(key, value.DeepClone());
            PropertySettings?.GetMethod("Load", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)?.Invoke(null, new object[] { this });
        }
        catch (Exception e) { Log("could not import the settings of the pack: " + e.Message); }
    }
}

/// <summary>
/// Entry of the pack itself: the one mod that derives directly from JibbersMod (the others go through PackPart).
/// It has no setting; it only shows that the pack is loaded and which version.
/// </summary>
public sealed class HankModPackInfoMod : JibbersMod
{
    public override string Id => "hank_mod_pack_info";
    public override bool EnabledToggle => false;
    public override void OnLoad()
    {
        Jibbers.Sdk.Game.Event.EnterReplay.Subscribe(_ => CameraOwnership.InReplay = true);
        Jibbers.Sdk.Game.Event.ExitReplay.Subscribe(_ => CameraOwnership.InReplay = false);
        Jibbers.Sdk.Game.Event.ChangeScene.Subscribe(_ => CameraOwnership.InReplay = false);
        Jibbers.Sdk.Game.Camera.Follow.LateUpdateMethod.Around(CameraCallbacks.Follow.Invoke);
        Jibbers.Interop.Game.Recorder.Update_Method.Around(CameraCallbacks.Recorder.Invoke);
    }
    public override string Name => "Hank Mod Pack";
    public override string Author => "Hank";
    public override string Description => "Eleven mods in one DLL: Quick Menu, Free Look, Play Cameras, HUD, Improved Spawn, Improved Replay Editor, Replay Camera, Overlay, Fun and Input Visualizer. Each one has its own entry named Hank Mod Pack: ... in Options > Mods.";
}
