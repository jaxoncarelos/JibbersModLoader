using Jibbers.Sdk;
using System.Diagnostics;
using Jibbers.Sdk.Game.Env;
using Jibbers.Sdk.Game.Physics;
using Jibbers.Sdk.Interop;

namespace Reshred;

/// <summary>Adapts SDK-owned game hooks to typed rewind scenes and elapsed frame time.</summary>
internal sealed class RewindGameLoop : IRewindGameLoop
{
    readonly MujocoStateAccess physics = new();
    readonly RiderStateCapture riderState = new();
    bool active;
    long previousFrameTimestamp;
    public float FixedStepSeconds { get; private set; } = 0.003f;
    public bool IsPaused => Scene.IsPaused;

    public void Start(Action<IRewindScene> physicsStep, Action<float> frame)
    {
        active = true;
        Scene.FixedUpdateMethod.Before((scene, _) =>
        {
            if (!active || IsPaused || physics.OpenScene(scene)is not { } context)
                return;
            physicsStep(new NativeRewindScene(physics, context, riderState, riderState.FindLiveObjects()));
        });
        Manager.UpdateMethod.Before((_, _) =>
        {
            if (!active)
                return;
            var timestamp = Stopwatch.GetTimestamp();
            var elapsed = previousFrameTimestamp == 0 ? 0 : (float)Stopwatch.GetElapsedTime(previousFrameTimestamp, timestamp).TotalSeconds;
            previousFrameTimestamp = timestamp;
            frame(Math.Clamp(elapsed, 0, 0.25f));
        });
        MainThread.Later(() =>
        {
            if (!active)
                return;
            var step = Il2Cpp.Unbox<float>(Unity.CallStatic(Unity.Class("UnityEngine.Time"), "get_fixedDeltaTime"));
            FixedStepSeconds = step is >= 0.0005f and < 0.05f ? step : 0.003f;
        });
    }

    // Hooks themselves are owned by the SDK/mod lifecycle. Stale callbacks must be inert immediately.
    public void Dispose() => active = false;
}
