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
        try
        {
            var own = Path.Combine(Bootstrap.ModsDir, Id + ".settings.json");
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
    public override string Description => "Nine mods in one DLL: Quick Menu, Free Look, Play Cameras, HUD, Improved Spawn, Improved Replay Editor, Replay Camera, Overlay and Fun. Each one has its own entry named Hank Mod Pack: ... in Options > Mods.";
}
