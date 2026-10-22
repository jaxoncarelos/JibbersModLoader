namespace Jibbers.Sdk.Features.Rewind;

internal readonly record struct RewindOptions(bool Enabled, int KeyboardKey, bool UseController, float HoldDurationSeconds, float PlaybackSpeed, bool ShowIndicator);
internal readonly record struct RewindInputState(bool Held, bool Typing);
internal readonly record struct RewindInputStatus(int MaskedBindings, int ControllerCount, string? Error);
internal readonly record struct RewindStatus(int FrameCount, float FixedStepSeconds, bool IsRewinding, RewindInputStatus Input);
/// <summary>A snapshot is opaque to the history buffer; only its scene adapter interprets it.</summary>
internal interface IRewindSnapshot
{
}

internal interface IRewindScene
{
    object Identity { get; }

    IRewindSnapshot Capture(IRewindSnapshot? reusableSnapshot);
    void Restore(IRewindSnapshot snapshot);
}

internal interface IRewindGameLoop : IDisposable
{
    float FixedStepSeconds { get; }

    bool IsPaused { get; }

    void Start(Action<IRewindScene> physicsStep, Action<float> frame);
}

internal interface IRewindInput : IDisposable
{
    RewindInputStatus Status { get; }

    RewindInputState Read(RewindOptions options);
    void RestoreBindings();
}

internal interface IRewindIndicator : IDisposable
{
    void Show(string message);
    void Hide();
}
