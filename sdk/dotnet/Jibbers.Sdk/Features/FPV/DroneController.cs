using System.Text.Json.Nodes;

namespace Jibbers.Sdk.Features.Fpv;

/// <summary>Mod metadata and lifecycle; flight behavior lives in a testable DroneRuntime.</summary>
public sealed class DroneController : ModFeature
{
    public override string Id => "fpv";
    public override string Name => "FPV Drone";
    public override string Version => "0.2.0";
    public override string Author => "Jibbers mods";
    public override string Description => "Fly the drone camera in Acro, Angle or Cine mode. F9 or L3+R3 to take off.";
    public override IEnumerable<Setting> Settings => DroneModSettings.Schema;

    DroneRuntime? runtime;
    public override void OnLoad()
    {
        // F10 now belongs to Atmosphere. Migrate the former flight-mode binding.
        if (GetKey("mode_key") == (int)Key.F10)
            SetSetting("mode_key", JsonValue.Create((int)Key.F11)!);
        var game = new UnityDroneGameLoop(Log);
        runtime = new DroneRuntime(game, game, () => DroneModSettings.Read(this), Log);
        runtime.Start();
        Log("ready; F9 or L3+R3 to take off");
    }

    public override void OnSettingChanged(string key, JsonNode value)
    {
        runtime?.RefreshSettings();
        if (key == "enabled" && !Enabled)
            MainThread.Run(() => runtime?.Land());
    }

    public override JsonNode? OnCommand(string command, JsonObject request)
    {
        if (runtime is null)
            return null;
        if (command == "fpv.status")
            return new JsonObject
            {
                ["hooked"] = runtime.Running,
                ["status"] = runtime.Status
            };
        if (command != "fpv.toggle")
            return null;
        runtime.RequestToggle();
        return new JsonObject
        {
            ["requested"] = true
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
