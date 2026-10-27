// The game-free part of Slow Motion: button presses become a physics speed, eased over real time.
// Unit tested.
namespace SlowMo;

internal enum SlowMotionMode { Toggle, Hold }

internal readonly record struct SlowMotionOptions(SlowMotionMode Mode, double Speed, double EaseSeconds)
{
    public const double MinSpeed = 0.05;
    public double TargetSpeed => Math.Clamp(double.IsFinite(Speed) ? Speed : 1, MinSpeed, 1);
}

/// <summary>Toggle/hold state and the eased physics speed (1 = normal).</summary>
internal sealed class SlowMotion
{
    bool pressing, spoiled;
    /// <summary>Whether the player has slow motion turned on (the speed may still be easing).</summary>
    public bool Engaged { get; private set; }
    /// <summary>The current physics speed, 1 when fully off.</summary>
    public double Speed { get; private set; } = 1;
    /// <summary>Whether physics should run slowed this step, including while easing back out.</summary>
    public bool Slowing => Speed < 1 - 1e-6;

    /// <summary>
    /// Advances one rendered frame. A toggle counts when the button is released, and not if
    /// <paramref name="chord"/> was held at any point of the press (clicking both sticks is the FPV
    /// drone's toggle) or the press began or continued while paused. While paused the speed holds.
    /// </summary>
    public void Update(bool pressed, double elapsedSeconds, SlowMotionOptions options, bool paused = false, bool chord = false)
    {
        if (pressed && !pressing)
            spoiled = false;
        if (pressed && (chord || paused))
            spoiled = true;
        var released = pressing && !pressed;
        pressing = pressed;
        if (paused)
            return;
        if (options.Mode == SlowMotionMode.Hold)
            Engaged = pressing && !spoiled;
        else if (released && !spoiled)
            Engaged = !Engaged;
        var target = Engaged ? options.TargetSpeed : 1;
        if (options.EaseSeconds <= 0 || !double.IsFinite(options.EaseSeconds))
        {
            Speed = target;
            return;
        }
        // A full transition takes EaseSeconds whatever the chosen speed.
        var rate = Math.Max(1 - options.TargetSpeed, SlowMotionOptions.MinSpeed) / options.EaseSeconds;
        var step = rate * Math.Max(elapsedSeconds, 0);
        Speed = Speed < target ? Math.Min(Speed + step, target) : Math.Max(Speed - step, target);
    }

    /// <summary>Back to normal speed at once. A button still held must be released before it toggles again.</summary>
    public void Reset()
    {
        Engaged = false;
        Speed = 1;
        spoiled = true;
    }
}
