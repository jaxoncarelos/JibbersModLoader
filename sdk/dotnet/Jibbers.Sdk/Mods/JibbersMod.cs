using System.Text.Json.Nodes;

namespace Jibbers.Sdk;

/// <summary>
/// Base class of a C# mod. Build a class library against <c>Jibbers.Sdk.dll</c>, subclass this
/// once, and drop the DLL in <c>JibbersMods\cs\</c>. The SDK creates it, gives it a section in the
/// Mods tab with its <see cref="Settings"/>, saves them, and calls the methods below.
/// </summary>
/// <example>
/// <code>
/// public sealed class MyMod : JibbersMod
/// {
///     public override string Id =&gt; "my_mod";
///     public override string Name =&gt; "My Mod";
///     public override IEnumerable&lt;Setting&gt; Settings =&gt; new[] { Setting.Slider("power", "Power", 1, 0, 2, 0.05) };
///     public override void OnLoad() =&gt; Game.Event.TrickPerformed.Subscribe(t =&gt; Log.Info(t.Name));
/// }
/// </code>
/// </example>
public abstract class JibbersMod
{
    /// <summary>Stable id: lowercase letters, digits, underscore. Names the settings file and prefixes your commands.</summary>
    public abstract string Id { get; }
    /// <summary>Heading of the mod's section in the Mods tab.</summary>
    public abstract string Name { get; }
    /// <summary>Version string (default: the assembly version).</summary>
    public virtual string Version => GetType().Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
    /// <summary>Author shown in the Mods tab.</summary>
    public virtual string Author => "";
    /// <summary>Other mod IDs that cannot run alongside this mod. Checked in both directions before OnLoad; catalog packages load first.</summary>
    public virtual IReadOnlyList<string> Conflicts => Array.Empty<string>();
    /// <summary>One-sentence description.</summary>
    public virtual string Description => "";
    /// <summary>Adds an "enabled" toggle first (default true). While it is off the SDK skips your hooks and events.</summary>
    public virtual bool EnabledToggle => true;
    /// <summary>The mod's settings (an "enabled" toggle is added for you).</summary>
    public virtual IEnumerable<Setting> Settings => PropertySettings.Schema(GetType());

    /// <summary>
    /// Runs once at game start, after settings are loaded: install hooks and detours, subscribe to
    /// events (they are applied on the main thread for you). Not on the main thread: wrap direct game
    /// calls in <see cref="MainThread.Later"/>.
    /// </summary>
    public virtual void OnLoad() { }

    /// <summary>A setting changed (Mods tab or <c>plugin.set</c>). Any thread.</summary>
    public virtual void OnSettingChanged(string key, JsonNode value) { }

    /// <summary>A custom request <c>{"cmd": "&lt;id&gt;.something", ...}</c>. Return null if it isn't yours. Any thread.</summary>
    public virtual JsonNode? OnCommand(string cmd, JsonObject request) => null;

    /// <summary>The game is closing or the mod is being unloaded: remove what you created. The SDK removes your hooks, detours, events and watches after this.</summary>
    public virtual void OnUnload() { }

    internal SettingsStore? Store;

    /// <summary>True while the mod's "enabled" toggle is on (always true without the toggle).</summary>
    public bool Enabled => !EnabledToggle || GetBool("enabled");

    /// <summary>A toggle's value.</summary>
    public bool GetBool(string key) => Store?.Get(key) is JsonValue v && v.TryGetValue(out bool b) && b;
    /// <summary>A slider's value.</summary>
    public double GetDouble(string key) => Store?.Get(key) is JsonValue v && double.TryParse(v.ToJsonString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : 0;
    /// <summary>A choice's selected option.</summary>
    public string GetString(string key) => Store?.Get(key) is JsonValue v && v.TryGetValue(out string? s) ? s : "";
    /// <summary>A key setting's virtual-key code.</summary>
    public int GetKey(string key) => (int)GetDouble(key);
    /// <summary>Changes a setting from code (validated, saved, and <see cref="OnSettingChanged"/> is called).</summary>
    public void SetSetting(string key, JsonNode value)
    {
        var stored = (Store ?? throw new InvalidOperationException("mod not loaded")).Set(key, value);
        PropertySettings.Apply(this, key, stored);
        OnSettingChanged(key, stored);
    }

    /// <summary>Logs a line prefixed with the mod id.</summary>
    public void Log(string message) => Sdk.Log.Info(Id, message);
}
