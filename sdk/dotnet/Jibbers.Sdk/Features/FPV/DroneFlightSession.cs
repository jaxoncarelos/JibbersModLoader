namespace Jibbers.Sdk.Features.Fpv;

/// <summary>One flight session. Native camera, input, scene queries and rendering are replaceable ports.</summary>
internal sealed class DroneFlightSession(DroneSessionServices services) : IDisposable
{
    DroneOptions? options;
    Drone? drone;
    DroneFlightSettings flightSettings = new();
    FlightMode mode;
    float fieldOfView;
    float flightSeconds;
    bool disposed;
    public Drone? Drone => drone;

    public void Configure(DroneOptions updatedOptions)
    {
        options = updatedOptions;
        flightSettings = updatedOptions.Flight;
        mode = updatedOptions.FlightMode;
        fieldOfView = updatedOptions.FieldOfView;
        drone?.SetMode(mode, flightSettings);
        if (drone is not null)
            services.Presentation.Show(updatedOptions.ShowHud, updatedOptions.SoundEnabled);
    }

    public void Update(DroneFrameContext frame, bool toggleRequested)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var settings = options ?? throw new InvalidOperationException("Configure the drone before updating it.");
        var(controls, actions) = services.Input.Read(frame.UnscaledDeltaTime, frame.Focused, drone is not null, settings.Pilot);
        if (!UpdateFlightState(toggleRequested || actions.Toggle, frame.Focused))
            return;
        ApplyPilotActions(actions);
        var paused = !frame.Focused || services.Camera.InputBlocked || !float.IsFinite(frame.DeltaTime) || frame.DeltaTime < 1e-4f;
        var target = services.Targets.Update(paused ? 0 : frame.DeltaTime);
        if (target is null && drone!.FollowOffset is not null)
            drone.Lock(null);
        var report = paused ? new FlightStepResult() : Simulate(frame.DeltaTime, controls, target);
        services.Camera.Apply(new(drone!.Position, drone.CalculateCameraRotation(flightSettings, settings.Vibration)), fieldOfView);
        services.Presentation.Update(frame.UnscaledDeltaTime, Telemetry(paused), settings.Volume, report.Crashed);
    }

    bool UpdateFlightState(bool toggleRequested, bool focused)
    {
        if (drone is null)
        {
            if (!toggleRequested || !focused || !services.Camera.CanTakeOff)
                return false;
            var pose = services.Camera.Pose;
            drone = new Drone(pose.Position, pose.Rotation, mode);
            services.Camera.BeginFlight();
            flightSeconds = 0;
            services.Presentation.Show(options!.ShowHud, options.SoundEnabled);
        }
        else if (toggleRequested || !services.Camera.FlightModeActive)
        {
            Land(services.Camera.FlightModeActive);
            return false;
        }

        return true;
    }

    void ApplyPilotActions(PilotActions actions)
    {
        if (actions.CycleMode)
        {
            mode = drone!.FlightMode.Next();
            drone.SetMode(mode, flightSettings);
        }

        if (drone!.FlightMode != FlightMode.Cine)
            flightSettings = flightSettings with
            {
                Uptilt = Math.Clamp(flightSettings.Uptilt + actions.Tilt * 5, 0, 60)
            };
        fieldOfView = Math.Clamp(fieldOfView + actions.Fov * 5, 50, 130);
        if (actions.Unlock)
        {
            services.Targets.Release();
            drone.Lock(null);
        }

        if (actions.Lock)
        {
            services.Targets.Cycle(drone.Position, drone.CalculateCameraRotation(flightSettings, 0));
            drone.Lock(services.Targets.Update(0)?.Position);
        }

        if (actions.Recall)
            Recall();
    }

    FlightStepResult Simulate(float deltaTime, in PilotControls controls, FlightTarget? target)
    {
        flightSeconds += MathF.Min(deltaTime, 0.1f);
        var report = drone!.Step(deltaTime, controls, target, services.Collisions, flightSettings);
        var rider = services.Targets.LocalRiderPosition;
        var lost = rider is { } local && ((drone.Position - local).Length > 4000 || (report.Ground is null && drone.Position.Y - local.Y < -150));
        if (report.Invalid || lost)
            Recall();
        return report;
    }

    DroneTelemetry Telemetry(bool paused) => new(drone!.FlightMode, drone.CalculateMotorOutput(flightSettings), drone.Velocity.Length, services.Collisions.Sweep(drone.Position, -DroneVector.Up, 300, 0)?.Distance, flightSeconds, services.Targets.LockedName, drone.RearmDelaySeconds > 0, paused, services.Input.GamepadConnected);
    void Recall()
    {
        if (services.Targets.RecallReference is { } target)
            drone?.Recall(target.Position, target.Forward);
    }

    public void Land(bool restoreCameraMode = true)
    {
        try
        {
            services.Camera.Restore(restoreCameraMode);
        }
        finally
        {
            drone = null;
            Cleanup.Run(services.Targets.Release, services.Presentation.Hide);
        }
    }

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        Cleanup.Run(() => Land(), services.Dispose);
    }
}
