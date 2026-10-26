using Fpv;

static class DroneRuntimeTests
{
    static readonly DroneOptions Options = new(new(), new(), FlightMode.Cine, 90, 0, true, true, 0.5f);
    static readonly DroneFrameContext Frame = new(1, 0.016f, 0.016f, true);
    sealed class Camera : IDroneCamera
    {
        public bool InputBlocked { get; set; }
        public bool CanTakeOff { get; set; } = true;
        public bool FlightModeActive { get; set; } = true;
        public DronePose Pose { get; set; } = new(new(0, 10, 0), DroneRotation.Identity);

        public int Begins, Applies, Restores;
        public bool RestoredMode, Disposed, FailApply;
        public void BeginFlight() => Begins++;
        public void Restore(bool restoreCameraMode)
        {
            Restores++;
            RestoredMode = restoreCameraMode;
        }

        public void Apply(DronePose pose, float fieldOfView)
        {
            if (FailApply)
                throw new InvalidOperationException("camera lost");
            Pose = pose;
            Applies++;
        }

        public void Dispose() => Disposed = true;
    }

    sealed class Targets : IDroneTargets
    {
        public string? LockedName => null;
        public DroneVector? LocalRiderPosition => new(0, 0, 0);
        public RecallReference? RecallReference => new(new(0, 0, 0), DroneVector.Forward);

        public bool Disposed;
        public int Releases;
        public FlightTarget? Update(float deltaTime) => null;
        public void Cycle(DroneVector position, DroneRotation rotation)
        {
        }

        public void Release() => Releases++;
        public void Dispose() => Disposed = true;
    }

    sealed class Controls : IDroneInput
    {
        public bool GamepadConnected => false;

        public PilotActions Actions = default;
        public PilotControls Current;
        public (PilotControls, PilotActions) Read(float elapsed, bool focused, bool flying, PilotSettings settings) => (Current, Actions);
    }

    sealed class World : ICollisionWorld
    {
        public CollisionHit? Sweep(DroneVector origin, DroneVector direction, float length, float radius) => null;
    }

    sealed class Presentation : IDronePresentation
    {
        public bool Disposed, FailHide;
        public int Shows;
        public void Show(bool hud, bool sound) => Shows++;
        public void Hide()
        {
            if (FailHide)
                throw new InvalidOperationException("hud");
        }

        public void Update(float elapsed, DroneTelemetry telemetry, float volume, bool crashed)
        {
        }

        public void Dispose() => Disposed = true;
    }

    sealed class Ports
    {
        public readonly Camera Camera = new();
        public readonly Targets Targets = new();
        public readonly Controls Input = new();
        public readonly Presentation Presentation = new();
        public DroneSessionServices Services => new(Camera, Targets, Input, new World(), Presentation);

        public DroneFlightSession Session()
        {
            var session = new DroneFlightSession(Services);
            session.Configure(Options);
            return session;
        }
    }

    sealed class Game : IDroneGameLoop, IDroneSceneSource
    {
        public Action Tick = null !;
        public DroneFrameContext CurrentFrame { get; set; } = Frame;

        public readonly List<Ports> Sessions = new();
        public bool FailStart, Disposed;
        public void Start(Action frame)
        {
            Tick = frame;
            if (FailStart)
                throw new InvalidOperationException("hook");
        }

        public DroneSessionServices OpenSession()
        {
            var ports = new Ports();
            Sessions.Add(ports);
            return ports.Services;
        }

        public void Dispose() => Disposed = true;
    }

    [Test]
    static void TakeoffRequiresFocusAndAnEligibleCamera()
    {
        var ports = new Ports();
        using var session = ports.Session();
        session.Update(Frame with { Focused = false }, true);
        Assert.True(session.Drone is null);
        ports.Camera.CanTakeOff = false;
        session.Update(Frame, true);
        Assert.True(session.Drone is null);
        ports.Camera.CanTakeOff = true;
        session.Update(Frame, true);
        Assert.Equal(1, ports.Camera.Begins);
        Assert.True(session.Drone is not null);
    }

    [Test]
    static void FocusLossAndPauseFreezeAnExistingFlight()
    {
        var ports = new Ports();
        using var session = ports.Session();
        ports.Input.Current = new()
        {
            Pitch = 1
        };
        session.Update(Frame, true);
        var position = session.Drone!.Position;
        session.Update(Frame with { Focused = false }, false);
        Assert.Equal(position, session.Drone.Position);
        session.Update(Frame with { DeltaTime = 0 }, false);
        Assert.Equal(position, session.Drone.Position);
    }

    [Test]
    static void LandingRestoresCameraAndReleasesTarget()
    {
        var ports = new Ports();
        using var session = ports.Session();
        session.Update(Frame, true);
        session.Update(Frame, true);
        Assert.True(session.Drone is null);
        Assert.True(ports.Camera.RestoredMode);
        Assert.Equal(1, ports.Targets.Releases);
    }

    [Test]
    static void ExternalCameraChangeKeepsThePlayersNewMode()
    {
        var ports = new Ports();
        using var session = ports.Session();
        session.Update(Frame, true);
        ports.Camera.FlightModeActive = false;
        session.Update(Frame, false);
        Assert.True(session.Drone is null);
        Assert.True(!ports.Camera.RestoredMode);
    }

    [Test]
    static void LostDroneRecallsBehindTheRider()
    {
        var ports = new Ports();
        using var session = ports.Session();
        session.Update(Frame, true);
        session.Drone!.Position = new(5000, 10, 0);
        session.Update(Frame, false);
        Assert.Equal(new DroneVector(0, 2, -4), session.Drone.Position);
    }

    [Test]
    static void LiveSettingsReconfigureFlightAndPresentation()
    {
        var ports = new Ports();
        using var session = ports.Session();
        session.Update(Frame, true);
        session.Configure(Options with { FlightMode = FlightMode.Acro, ShowHud = false });
        Assert.Equal(FlightMode.Acro, session.Drone!.FlightMode);
        Assert.Equal(2, ports.Presentation.Shows);
    }

    [Test]
    static void CleanupFailureStillDisposesEverySessionPort()
    {
        var ports = new Ports();
        var session = ports.Session();
        session.Update(Frame, true);
        ports.Presentation.FailHide = true;
        Assert.Throws<AggregateException>(session.Dispose);
        Assert.True(ports.Camera.Disposed && ports.Targets.Disposed && ports.Presentation.Disposed);
        session.Dispose();
    }

    [Test]
    static void CameraReplacementDisposesThePreviousSession()
    {
        var game = new Game();
        using var runtime = new DroneRuntime(game, game, () => Options, _ =>
        {
        });
        runtime.Start();
        runtime.RequestToggle();
        game.Tick();
        game.CurrentFrame = Frame with
        {
            CameraIdentity = 2
        };
        game.Tick();
        Assert.Equal(2, game.Sessions.Count);
        Assert.True(game.Sessions[0].Camera.Disposed);
        Assert.True(game.Sessions[0].Camera.RestoredMode);
    }

    [Test]
    static void FrameFailureLandsOnceAndStaleCallbacksAreInert()
    {
        var game = new Game();
        var logs = new List<string>();
        var runtime = new DroneRuntime(game, game, () => Options, logs.Add);
        runtime.Start();
        runtime.RequestToggle();
        game.Tick();
        game.Sessions[0].Camera.FailApply = true;
        game.Tick();
        game.Tick();
        Assert.True(!runtime.Running);
        Assert.True(game.Sessions[0].Camera.Disposed);
        Assert.Equal(1, logs.Count);
        runtime.Dispose();
        game.Tick();
        Assert.Equal(1, game.Sessions.Count);
    }

    [Test]
    static void StartFailureReleasesTheGameLoop()
    {
        var game = new Game
        {
            FailStart = true
        };
        var runtime = new DroneRuntime(game, game, () => Options, _ =>
        {
        });
        Assert.Throws<InvalidOperationException>(runtime.Start);
        Assert.True(game.Disposed);
        game.Tick();
        Assert.Equal(0, game.Sessions.Count);
    }
}
