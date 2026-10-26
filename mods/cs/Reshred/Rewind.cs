using Jibbers.Sdk;

// The game-free parts of Reshred: scrub arithmetic, the Rewired key-code table and the category
// and button-name policies. Unit tested (ported from the Rust tests).
namespace Reshred;

internal static class Rewind
{
    /// <summary>Seconds of history kept: how far back the rewind can go.</summary>
    public const float History = 6.0f;
    /// <summary>How many history frames to step back this physics step at <paramref name = "speed"/> (carrying the remainder).</summary>
    public static int ScrubSteps(ref float fractionalSteps, float speed)
    {
        fractionalSteps += MathF.Max(speed, 0);
        var wholeSteps = MathF.Floor(fractionalSteps);
        fractionalSteps -= wholeSteps;
        return (int)wholeSteps;
    }

    /// <summary>Index of the frame to show after stepping back <paramref name = "stepsBack"/> frames from a history of <paramref name = "frameCount"/>: never before the oldest.</summary>
    public static int? ScrubTarget(int frameCount, int stepsBack) => frameCount == 0 ? null : Math.Max(0, frameCount - 1 - stepsBack);
    /// <summary>Frames kept for <see cref = "History"/> seconds at physics step <paramref name = "fixedStepSeconds"/>.</summary>
    public static int Capacity(float fixedStepSeconds) => (int)(History / fixedStepSeconds) + 1;
}
