using Jibbers.Sdk;

namespace Fpv;

internal readonly record struct DroneFrameContext(int CameraIdentity, float DeltaTime, float UnscaledDeltaTime, bool Focused);
internal readonly record struct DronePose(DroneVector Position, DroneRotation Rotation);
internal readonly record struct RecallReference(DroneVector Position, DroneVector Forward);
internal sealed record DroneOptions(DroneFlightSettings Flight, PilotSettings Pilot, FlightMode FlightMode, float FieldOfView, float Vibration, bool ShowHud, bool SoundEnabled, float Volume);
internal interface IDroneCamera : IDisposable
{
    bool InputBlocked { get; }

    bool CanTakeOff { get; }

    bool FlightModeActive { get; }

    DronePose Pose { get; }

    void BeginFlight();
    void Restore(bool restoreCameraMode);
    void Apply(DronePose pose, float fieldOfView);
}

internal interface IDroneTargets : IDisposable
{
    string? LockedName { get; }

    DroneVector? LocalRiderPosition { get; }

    RecallReference? RecallReference { get; }

    FlightTarget? Update(float deltaTime);
    void Cycle(DroneVector dronePosition, DroneRotation viewRotation);
    void Release();
}

internal interface IDroneInput
{
    bool GamepadConnected { get; }

    (PilotControls Controls, PilotActions Actions) Read(float elapsedSeconds, bool focused, bool flying, PilotSettings settings);
}

internal interface IDronePresentation : IDisposable
{
    void Show(bool hudEnabled, bool soundEnabled);
    void Hide();
    void Update(float elapsedSeconds, DroneTelemetry telemetry, float volume, bool crashed);
}

internal interface IDroneSceneSource
{
    DroneFrameContext CurrentFrame { get; }

    DroneSessionServices OpenSession();
}

internal interface IDroneGameLoop : IDisposable
{
    void Start(Action frame);
}

/// <summary>Dependencies and ownership for one camera lifetime, supplied by the game adapter.</summary>
internal sealed record DroneSessionServices(IDroneCamera Camera, IDroneTargets Targets, IDroneInput Input, ICollisionWorld Collisions, IDronePresentation Presentation) : IDisposable
{
    public void Dispose() => Cleanup.Run(Camera.Dispose, Targets.Dispose, Presentation.Dispose);
}
