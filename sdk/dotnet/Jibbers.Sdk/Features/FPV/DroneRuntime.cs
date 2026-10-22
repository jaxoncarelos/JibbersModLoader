namespace Jibbers.Sdk.Features.Fpv;

/// <summary>Owns session replacement across camera/map changes and contains a failed flight session.</summary>
internal sealed class DroneRuntime(IDroneGameLoop gameLoop, IDroneSceneSource scenes, Func<DroneOptions> readOptions, Action<string> log) : IDisposable
{
    DroneFlightSession? session;
    int cameraIdentity;
    int settingsChanged = 1;
    int toggleRequested;
    bool active;
    bool disposed;
    public string Status { get; private set; } = "waiting for camera";
    public bool Running => active;

    public void Start()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (active)
            return;
        active = true;
        try
        {
            gameLoop.Start(UpdateFrame);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public void RefreshSettings() => Interlocked.Exchange(ref settingsChanged, 1);
    public void RequestToggle() => Interlocked.Exchange(ref toggleRequested, 1);
    public void Land() => session?.Land();
    void UpdateFrame()
    {
        if (!active || disposed)
            return;
        try
        {
            var frame = scenes.CurrentFrame;
            if (session is null || cameraIdentity != frame.CameraIdentity)
            {
                session?.Dispose();
                session = null;
                var services = scenes.OpenSession();
                session = new DroneFlightSession(services);
                cameraIdentity = frame.CameraIdentity;
                RefreshSettings();
            }

            if (Interlocked.Exchange(ref settingsChanged, 0) != 0)
                session.Configure(readOptions());
            session.Update(frame, Interlocked.Exchange(ref toggleRequested, 0) != 0);
            Status = session.Drone is { } drone ? $"flying {drone.FlightMode} {drone.Velocity.Length * 3.6f:F0} km/h" : "landed";
        }
        catch (Exception exception)
        {
            active = false;
            try
            {
                session?.Dispose();
            }
            catch (Exception cleanup)
            {
                log("cleanup: " + cleanup.Message);
            }

            session = null;
            Status = exception.Message;
            log("stopped safely: " + exception.Message);
        }
    }

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        active = false;
        Cleanup.Run(gameLoop.Dispose, () => session?.Dispose());
        session = null;
    }
}
