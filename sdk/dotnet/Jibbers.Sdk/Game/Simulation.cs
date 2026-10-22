namespace Jibbers.Sdk.Game.Physics;

/// <summary>High-level physics-step controls. Native hooks and frame accounting stay in the SDK.</summary>
public static class Simulation
{
    /// <summary>Runs physics at a fraction of normal speed while the condition is true. Call from OnLoad.</summary>
    /// <remarks>Only active while focused and not typing. The host mod's enabled toggle and unload are handled automatically.</remarks>
    public static MethodHook SlowWhile(Func<bool> condition, Func<double> speed)
    {
        ArgumentNullException.ThrowIfNull(condition);
        ArgumentNullException.ThrowIfNull(speed);
        var limiter = new StepLimiter();
        return Scene.StepSceneMethod.Around(original =>
        {
            var active = Input.GameFocused && !Input.Typing && condition();
            if (limiter.ShouldStep(active, active ? speed() : 1)) original();
        });
    }
}

internal sealed class StepLimiter
{
    double accumulatedSteps;
    public bool ShouldStep(bool active, double speed)
    {
        if (!active) { accumulatedSteps = 0; return true; }
        if (!double.IsFinite(speed) || speed < 0 || speed > 1) throw new ArgumentOutOfRangeException(nameof(speed), "Slow-motion speed must be between 0 and 1.");
        accumulatedSteps += speed;
        if (accumulatedSteps < 1 - 1e-9) return false;
        accumulatedSteps = Math.Max(0, accumulatedSteps - 1);
        return true;
    }
}
