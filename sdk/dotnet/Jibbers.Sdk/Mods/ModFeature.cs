using System.Text.Json.Nodes;

namespace Jibbers.Sdk;

/// <summary>A reusable gameplay feature. Its host mod supplies settings storage, logging and lifecycle ownership.</summary>
public abstract class ModFeature
{
    JibbersMod? owner;
    internal void Attach(JibbersMod mod) => owner = mod;
    protected JibbersMod Owner => owner ?? throw new InvalidOperationException("The feature must be hosted by a FeatureMod.");
    /// <summary>Default mod identifier.</summary>
    public abstract string Id { get; }
    /// <summary>Default display name.</summary>
    public abstract string Name { get; }
    /// <summary>Feature version.</summary>
    public virtual string Version => Owner.GetType().Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
    /// <summary>Default author.</summary>
    public virtual string Author => "";
    /// <summary>Default description.</summary>
    public virtual string Description => "";
    /// <summary>Whether the host has an enabled toggle.</summary>
    public virtual bool EnabledToggle => true;
    /// <summary>Settings provided by this feature.</summary>
    public virtual IEnumerable<Setting> Settings => Array.Empty<Setting>();
    /// <summary>Whether the host mod is enabled.</summary>
    public bool Enabled => Owner.Enabled;
    /// <summary>A toggle value.</summary>
    public bool GetBool(string key) => Owner.GetBool(key);
    /// <summary>A numeric setting value.</summary>
    public double GetDouble(string key) => Owner.GetDouble(key);
    /// <summary>A choice value.</summary>
    public string GetString(string key) => Owner.GetString(key);
    /// <summary>A key code.</summary>
    public int GetKey(string key) => Owner.GetKey(key);
    /// <summary>Updates a setting through the host's validation and persistence.</summary>
    public void SetSetting(string key, JsonNode value) => Owner.SetSetting(key, value);
    /// <summary>Logs with the host mod's identity.</summary>
    public void Log(string message) => Owner.Log(message);
    /// <summary>Called by the host after settings are loaded.</summary>
    public virtual void OnLoad() { }
    /// <summary>Called after a setting changes.</summary>
    public virtual void OnSettingChanged(string key, JsonNode value) { }
    /// <summary>Handles a feature command.</summary>
    public virtual JsonNode? OnCommand(string command, JsonObject request) => null;
    /// <summary>Restores game state and releases feature-owned objects.</summary>
    public virtual void OnUnload() { }
}

/// <summary>A small mod entry point for a reusable SDK feature. Lifecycle and settings forwarding are automatic.</summary>
public abstract class FeatureMod<TFeature> : JibbersMod where TFeature : ModFeature, new()
{
    /// <summary>The feature implementation hosted by this mod.</summary>
    protected TFeature Feature { get; }
    /// <summary>Creates and attaches the feature without touching game objects.</summary>
    protected FeatureMod() { Feature = new TFeature(); Feature.Attach(this); }
    public override string Id => Feature.Id;
    public override string Name => Feature.Name;
    public override string Version => Feature.Version;
    public override string Author => Feature.Author;
    public override string Description => Feature.Description;
    public override bool EnabledToggle => Feature.EnabledToggle;
    public override IEnumerable<Setting> Settings => Feature.Settings;
    /// <summary>Configure the feature before it starts. Settings are already available.</summary>
    protected virtual void Configure(TFeature feature) { }
    public sealed override void OnLoad() { Configure(Feature); Feature.OnLoad(); }
    public sealed override void OnSettingChanged(string key, JsonNode value) => Feature.OnSettingChanged(key, value);
    public sealed override JsonNode? OnCommand(string command, JsonObject request) => Feature.OnCommand(command, request);
    public sealed override void OnUnload() => Feature.OnUnload();
}
