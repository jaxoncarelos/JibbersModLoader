using System.Text.Json.Nodes;

namespace Jibbers.Sdk.Features.Atmosphere;

/// <summary>Phone settings and lifecycle adapter for the atmosphere application service.</summary>
public sealed class AtmosphereEffects : ModFeature
{
    public override string Id => "atmosphere";
    public override string Name => "Atmosphere";
    public override string Author => "Jaxon";
    public override string Description => "Fog, snowfall, valley clouds and colour grading.";

    readonly AtmosphereConfigurationStore configuration;
    AtmosphereRuntime? runtime;
    public AtmosphereEffects() => configuration = new(() => Path.Combine(Bootstrap.GameDir.Length > 0 ? Bootstrap.GameDir : AppContext.BaseDirectory, "jibbers_atmosphere.json"), Log);
    public override IEnumerable<Setting> Settings => configuration.Schema;

    internal AtmosphereRenderSettings CaptureSettings() => configuration.Capture();
    public override void OnLoad()
    {
        runtime = new AtmosphereRuntime(configuration, AtmosphereGameServices.Create(Log), () => Enabled, Log);
        runtime.Start();
        Input.OnSetting(Owner, "toggleKey", () =>
        {
            if (GetBool("hotkeys"))
                ApplyChange(configuration.ApplyCommand(new JsonObject { ["toggle"] = true }));
        });
        Input.OnSetting(Owner, "presetKey", () =>
        {
            if (GetBool("hotkeys"))
                ApplyChange(configuration.ApplyCommand(new JsonObject { ["nextPreset"] = true }));
        });
        MainThread.Later(() => configuration.SynchronizeMenu(SetSetting));
    }

    public override void OnSettingChanged(string key, JsonNode value) => ApplyChange(configuration.ApplyMenuSetting(key, value));
    void ApplyChange(AtmosphereConfigurationChange change)
    {
        if (!change.Changed)
            return;
        runtime?.ConfigurationChanged(change.Respawn);
        if (runtime is not null && !configuration.Capture().Enabled)
            MainThread.Run(() => runtime?.Disable());
        if (runtime is not null && change.SynchronizeMenu)
            MainThread.Later(() => configuration.SynchronizeMenu(SetSetting));
    }

    public override JsonNode? OnCommand(string command, JsonObject request)
    {
        if (command == "atmosphere.set")
        {
            ApplyChange(configuration.ApplyCommand(request));
            return new JsonObject
            {
                ["settings"] = configuration.Capture().Values.DeepClone()
            };
        }

        if (command != "atmosphere.status")
            return null;
        var status = runtime?.Status ?? default;
        return new JsonObject
        {
            ["preset"] = configuration.Capture().Values["preset"]?.DeepClone(),
            ["spawned"] = status.Spawned,
            ["floor"] = status.TerrainFloor,
            ["cameraKmh"] = status.CameraSpeedKmh,
            ["error"] = status.Error,
        };
    }

    public override void OnUnload()
    {
        if (runtime is null)
            return;
        MainThread.Run(() => runtime?.Dispose());
        runtime = null;
    }
}
