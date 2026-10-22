namespace Jibbers.Sdk.Features.Rewind;

/// <summary>Coordinates rewind storage, input and feedback through small independently replaceable ports.</summary>
internal sealed class RewindRuntime(IRewindGameLoop gameLoop, IRewindInput input, IRewindIndicator indicator, Func<RewindOptions> readOptions, Action<string> log) : IDisposable
{
    readonly RewindHistory history = new(log);
    readonly RewindPlayback playback = new();
    string? previousInputError;
    bool started;
    bool disposed;
    bool indicatorFailed;
    public RewindStatus Status => new(history.Count, gameLoop.FixedStepSeconds, history.IsRewinding, input.Status);

    public void Start()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (started)
            return;
        try
        {
            gameLoop.Start(AdvancePhysics, UpdateInteraction);
            started = true;
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    void AdvancePhysics(IRewindScene scene)
    {
        if (disposed)
            return;
        var options = readOptions();
        if (options.Enabled)
            history.Advance(scene, gameLoop.FixedStepSeconds, options.PlaybackSpeed);
    }

    void UpdateInteraction(float elapsedSeconds)
    {
        if (disposed)
            return;
        var options = readOptions();
        var controls = input.Read(options);
        if (!options.Enabled)
            controls = controls with
            {
                Held = false
            };
        var inputError = input.Status.Error;
        if (inputError != previousInputError && inputError is not null)
            log("input reservation: " + inputError);
        previousInputError = inputError;
        var feedback = playback.Update(controls, history.Count, gameLoop.FixedStepSeconds, elapsedSeconds, gameLoop.IsPaused, options, inputError);
        history.IsRewinding = feedback.IsRewinding;
        if (!indicatorFailed)
        {
            try
            {
                if (feedback.Message is { } message)
                    indicator.Show(message);
                else
                    indicator.Hide();
            }
            catch (Exception exception)
            {
                indicatorFailed = true;
                log("indicator unavailable: " + exception.Message);
            }
        }

        if (feedback.ResumedSecondsAgo is { } seconds)
            log($"resumed {seconds:0.00}s back");
    }

    public void Reset()
    {
        history.IsRewinding = false;
        playback.Reset();
        Cleanup.Run(input.RestoreBindings, indicator.Hide);
    }

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        // Stop callbacks before releasing any objects they could reach.
        Cleanup.Run(gameLoop.Dispose, Reset, input.Dispose, indicator.Dispose, history.Clear);
    }
}
