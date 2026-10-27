using Jibbers.Sdk;
using Jibbers.Sdk.Game.Env;
using Jibbers.Sdk.Game.Physics;
using Jibbers.Sdk.UI;
using System.Diagnostics;
using System.Text.Json.Nodes;

namespace SlowMo;

/// <summary>Mod entry point: reads the left stick click and keyboard key each frame and slows the physics step.</summary>
public sealed class SlowMoMod : JibbersMod
{
    public override string Id => "slowmo";
    public override string Name => "Slow Motion";
    public override string Author => "Jaxon";
    public override string Description => "Click the left stick (or press V) to slow the game down; click again to return to normal speed.";

    [Choice("Activation", "Toggle", "Toggle", "Hold", Tooltip = "Toggle: click once to turn on, again to turn off. Hold: slow only while held.")]
    public string Activation { get; private set; } = "Toggle";

    [Toggle("Left stick click", Key = "controller", Tooltip = "Use the left stick click (L3) on Xbox-compatible controllers. It counts when released, and not when both sticks are clicked (the FPV drone's toggle).")]
    public bool UseController { get; private set; }

    [KeyBinding("Keyboard key", Key.V, Key = "key", Tooltip = "Keyboard key that does the same as the left stick click.")]
    public Key KeyboardKey { get; private set; }

    [Slider("Speed", 0.25, SlowMotionOptions.MinSpeed, 0.9, 0.05, Tooltip = "Game speed while slowed (0.25 = a quarter of normal speed).")]
    public double Speed { get; private set; }

    [Slider("Ease time", 0.3, 0, 1, 0.05, Key = "ease", Tooltip = "Seconds to slide into and out of slow motion (0 = instant).")]
    public double EaseSeconds { get; private set; }

    [Toggle("Indicator", Tooltip = "Show a label while slow motion is on.")]
    public bool Indicator { get; private set; }

    readonly SlowMotion slowMotion = new();
    readonly StepLimiter limiter = new();
    int stepsOffered, stepsRun;
    double engagedSeconds;
    bool reported;
    HudPanel? hud;
    HudLabel? label;
    long previousFrame;
    bool active, indicatorFailed;

    SlowMotionOptions Options => new(Activation == "Hold" ? SlowMotionMode.Hold : SlowMotionMode.Toggle, Speed, EaseSeconds);

    /// <summary>The button, and whether the right stick is clicked too (the FPV drone's both-sticks toggle).</summary>
    (bool Pressed, bool Chord) ReadInput()
    {
        if (!Input.GameFocused || Input.Typing)
            return (false, false);
        var pad = UseController ? Gamepad.Read() : default;
        return ((UseController && pad.Held(PadButton.LeftStick)) || Input.IsDown(KeyboardKey), pad.Held(PadButton.RightStick));
    }

    public override void OnLoad()
    {
        active = true;
        hud = Hud.Register("Slow Motion", new HudOptions { SortingOrder = 940 });
        label = hud.AddText("Label", new HudTextOptions
        {
            Anchor = HudAnchor.TopCenter,
            Offset = new(0, -90),
            Size = new(600, 80),
            FontSize = 30,
        });
        Manager.UpdateMethod.Before((_, _) =>
        {
            if (active)
                Frame();
        });
        // Slow the physics by skipping whole MuJoCo FixedUpdate calls, as the game's own pause does.
        // (The SDK's Simulation.SlowWhile skips StepScene instead, which this build's FixedUpdate may
        // have compiled inline, so skipping it changed nothing.)
        Scene.FixedUpdateMethod.Around((_, original) =>
        {
            var slowing = active && slowMotion.Slowing && Input.GameFocused && !Input.Typing;
            var step = limiter.ShouldStep(slowing, slowMotion.Speed);
            if (slowing)
            {
                stepsOffered++;
                if (step)
                    stepsRun++;
            }
            if (step)
                original();
        });
    }

    void Frame()
    {
        var now = Stopwatch.GetTimestamp();
        var elapsed = previousFrame == 0 ? 0 : Math.Min(Stopwatch.GetElapsedTime(previousFrame, now).TotalSeconds, 0.25);
        previousFrame = now;
        var paused = Scene.IsPaused;
        var (pressed, chord) = ReadInput();
        slowMotion.Update(pressed, elapsed, Options, paused, chord);
        ShowIndicator(Indicator && !indicatorFailed && slowMotion.Engaged && !paused);
        ReportSteps(elapsed);
    }

    /// <summary>Two seconds into each slow motion, logs how many physics steps actually ran.</summary>
    void ReportSteps(double elapsed)
    {
        if (!slowMotion.Engaged)
        {
            (engagedSeconds, stepsOffered, stepsRun, reported) = (0, 0, 0, false);
            return;
        }
        engagedSeconds += elapsed;
        if (reported || engagedSeconds < 2)
            return;
        reported = true;
        Log(stepsOffered == 0
            ? "Slow Motion: no physics steps reached the limiter; the game may not be stepping physics through MjScene.FixedUpdate."
            : $"Slow Motion: ran {stepsRun} of {stepsOffered} physics steps ({(double)stepsRun / stepsOffered:P0}, target {slowMotion.Speed:P0}).");
    }

    void ShowIndicator(bool visible)
    {
        try
        {
            if (!visible)
            {
                hud?.Hide();
                return;
            }
            label!.Text = $"SLOW MO  {Options.TargetSpeed:0.##}x";
            hud!.Show();
        }
        catch (Exception exception)
        {
            if (!indicatorFailed)
                Log("indicator unavailable: " + exception.Message);
            indicatorFailed = true;
        }
    }

    public override void OnSettingChanged(string key, JsonNode value)
    {
        if (key == "enabled" && !value.GetValue<bool>())
            MainThread.Later(() =>
            {
                slowMotion.Reset();
                ShowIndicator(false);
            });
    }

    public override JsonNode? OnCommand(string command, JsonObject request) => command != "slowmo.info" ? null : new JsonObject
    {
        ["engaged"] = slowMotion.Engaged,
        ["speed"] = slowMotion.Speed,
        ["controller"] = Gamepad.Read().Connected,
    };

    public override void OnUnload()
    {
        active = false;
        MainThread.Run(() =>
        {
            slowMotion.Reset();
            hud?.Dispose();
            hud = null;
        });
    }
}
