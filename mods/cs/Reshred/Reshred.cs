using Jibbers.Sdk;
using System.Text.Json.Nodes;

namespace Reshred;

/// <summary>Mod entry point: connects the saved options to the rewind application service.</summary>
public sealed class ReshredMod : JibbersMod
{
    public override string Id => "reshred";
    public override string Name => "Rewind";
    public override string Author => "Jaxon";
    public override string Description => "Hold X to rewind through the last 6 seconds, then let go to continue from that moment. Rewound time is cut from replay clips.";
    public override IEnumerable<Setting> Settings => new[]
    {
        Setting.KeyBinding("key", "Keyboard key", (int)Key.X).WithTooltip("Key to hold (X by default)."),
        Setting.Toggle("controller", "Controller X", true).WithTooltip("Also rewind when holding X (Xbox) / Square (PlayStation)."),
        Setting.Slider("holdTime", "Hold time", 0.2, 0.1, 2, 0.1).WithTooltip("Seconds to hold the button before the rewind starts."),
        Setting.Slider("speed", "Rewind speed", 2, 0.5, 6, 0.5).WithTooltip("How fast time runs backwards while held (1 = real time)."),
        Setting.Toggle("indicator", "Indicator", true).WithTooltip("Show how far back you are while rewinding."),
    };

    RewindRuntime? runtime;
    RewindOptions ReadOptions() => new(Enabled, GetKey("key"), GetBool("controller"), (float)GetDouble("holdTime"), (float)GetDouble("speed"), GetBool("indicator"));
    public override void OnLoad()
    {
        runtime = new RewindRuntime(new RewindGameLoop(), new RewindInputReader(), new RewindIndicator(Log), ReadOptions, Log);
        runtime.Start();
    }

    public override void OnSettingChanged(string key, JsonNode value)
    {
        if (key == "enabled" && !value.GetValue<bool>())
            MainThread.Later(() => runtime?.Reset());
    }

    public override JsonNode? OnCommand(string command, JsonObject request)
    {
        if (command != "reshred.info" || runtime is null)
            return null;
        var status = runtime.Status;
        return new JsonObject
        {
            ["frames"] = status.FrameCount,
            ["span"] = status.FrameCount * status.FixedStepSeconds,
            ["dt"] = status.FixedStepSeconds,
            ["scrubbing"] = status.IsRewinding,
            ["maskedBindings"] = status.Input.MaskedBindings,
            ["rawControllers"] = status.Input.ControllerCount,
            ["inputError"] = status.Input.Error,
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
