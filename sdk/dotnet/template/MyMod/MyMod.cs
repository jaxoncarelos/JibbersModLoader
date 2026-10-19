// Starter mod. Rename the folder, the .csproj and the class, change Id, and edit.
//
// What it shows:
// - settings (toggle, slider, choice, key) that appear under Options > Mods and are saved for you;
// - a game event (the rider spawning) and a pre-hook on the game camera's per-frame update;
// - a hotkey bound to a key setting, rebindable in the Mods tab;
// - reading and changing one of the game's own settings;
// - a custom command other tools can call.

using System.Text.Json.Nodes;
using Jibbers.Sdk;
using Jibbers.Sdk.Game.Camera;
using Jibbers.Sdk.Game.Event;

namespace MyMod;

public sealed class MyMod : JibbersMod
{
    public override string Id => "my_mod"; // lowercase, digits, underscore. Names JibbersMods\my_mod.settings.json
    public override string Name => "My Mod";
    public override string Author => "you";
    public override string Description => "Starter mod from the Jibbers C# SDK.";

    public override IEnumerable<Setting> Settings => new[]
    {
        Setting.Slider("distance", "Camera distance", 3.5, 1, 10, 0.1).InSection("Camera").WithTooltip("The game's follow-camera distance."),
        Setting.Choice("mode", "Mode", new[] { "Gentle", "Normal", "Wild" }, "Normal").InSection("Tuning"),
        Setting.KeyBinding("hotkey", "Hotkey", (int)Key.F9).InSection("Controls"),
    };

    long frames;

    public override void OnLoad()
    {
        // Game events: typed, no hook slot used. Runs on the main thread.
        PlayerSpawn.Subscribe(e => Log($"rider spawned: {e.Ragdoll.UnityName()}"));
        TrickPerformed.Subscribe(t =>
        {
            if (!t.IsUpdate)
            {
                Log($"trick: {t.Name} {t.Details}");
            }
        });

        // A pre-hook: runs before every call of FollowCam.LateUpdate (once per frame while riding).
        Follow.LateUpdateMethod.Before((cam, _) =>
        {
            frames++;
        });

        // A hotkey from the "hotkey" setting: pressing it applies the camera distance.
        Input.OnSetting(this, "hotkey", () =>
        {
            var n = GameVars.Set("follow-cam-distance", (float)GetDouble("distance"));
            Log($"camera distance {GetDouble("distance"):0.0} ({n} listeners notified, {frames} camera frames so far)");
        });
    }

    public override void OnSettingChanged(string key, JsonNode value) => Log($"{key} = {value.ToJsonString()}");

    public override JsonNode? OnCommand(string cmd, JsonObject request) => cmd switch
    {
        "my_mod.status" => new JsonObject { ["frames"] = frames, ["mode"] = GetString("mode") },
        _ => null,
    };
}
