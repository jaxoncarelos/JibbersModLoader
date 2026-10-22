namespace Jibbers.Sdk.Features.Rewind;

internal readonly record struct RewindFeedback(bool IsRewinding, string? Message, float? ResumedSecondsAgo = null);
/// <summary>Pure hold-to-rewind interaction. Its only inputs are button state, time and available history.</summary>
internal sealed class RewindPlayback
{
    bool previouslyHeld;
    bool ignoreUntilRelease;
    bool rewinding;
    float heldSeconds;
    int startingFrameCount;
    public RewindFeedback Update(RewindInputState input, int frameCount, float fixedStepSeconds, float elapsedSeconds, bool paused, RewindOptions options, string? inputError)
    {
        if (!input.Held)
        {
            var resumed = rewinding ? Math.Max(0, startingFrameCount - frameCount) * fixedStepSeconds : (float? )null;
            Reset();
            return new(false, null, resumed);
        }

        if (!previouslyHeld)
        {
            previouslyHeld = true;
            heldSeconds = 0;
            ignoreUntilRelease = input.Typing;
        }

        if (ignoreUntilRelease)
            return new(false, null);
        var historyReady = !paused && (rewinding ? frameCount > 0 : frameCount * fixedStepSeconds > 0.3f);
        if (!historyReady)
        {
            heldSeconds = 0;
            var resumed = rewinding ? Math.Max(0, startingFrameCount - frameCount) * fixedStepSeconds : (float? )null;
            rewinding = false;
            return new(false, options.ShowIndicator ? "Rewind waiting for active physics/history" : null, resumed);
        }

        heldSeconds += Math.Clamp(elapsedSeconds, 0, 0.25f);
        if (!rewinding && heldSeconds < options.HoldDurationSeconds)
        {
            var progressPercent = (int)Math.Min(heldSeconds / options.HoldDurationSeconds * 100, 99);
            var conflict = inputError is null ? "" : " - input conflict (see log)";
            return new(false, options.ShowIndicator ? $"Hold to rewind {progressPercent}%{conflict}" : null);
        }

        if (!rewinding)
        {
            rewinding = true;
            startingFrameCount = frameCount;
        }

        var elapsedHistory = Math.Max(0, startingFrameCount - frameCount) * fixedStepSeconds;
        var totalHistory = Math.Max(0, startingFrameCount - 1) * fixedStepSeconds;
        var releaseHint = frameCount <= 1 ? "start of the rewind - release to resume" : "release to resume";
        return new(true, options.ShowIndicator ? $"Rewinding -{elapsedHistory:0.0}s / -{totalHistory:0.0}s - {releaseHint}" : null);
    }

    public void Reset()
    {
        previouslyHeld = false;
        ignoreUntilRelease = false;
        rewinding = false;
        heldSeconds = 0;
        startingFrameCount = 0;
    }
}
