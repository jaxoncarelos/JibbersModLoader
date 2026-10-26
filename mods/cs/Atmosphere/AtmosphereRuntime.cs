using Jibbers.Sdk;

namespace Atmosphere;

/// <summary>Coordinates atmosphere application policy without knowing Unity objects or native shader APIs.</summary>
internal sealed class AtmosphereRuntime(IAtmosphereSettings configuration, AtmosphereGameServices services, Func<bool> enabled, Action<string> log) : IDisposable
{
    AtmosphereRenderSettings? previousSettings;
    int framesUntilQualityRefresh;
    string? prefabError;
    AtmosphereFrame lastFrame;
    bool disposed;
    bool started;
    string? frameError;
    bool? previouslyActive;
    public AtmosphereStatus Status => new(services.Prefab.Spawned, lastFrame.TerrainFloor, lastFrame.CameraVelocity.Magnitude * 3.6f, frameError ?? prefabError);

    public void Start()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (started)
            return;
        started = true;
        try
        {
            services.Scene.Start(UpdateFrame);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    void UpdateFrame()
    {
        if (disposed || frameError is not null)
            return;
        try
        {
            ApplyFrame();
        }
        catch (Exception exception)
        {
            frameError = exception.Message;
            log("effects stopped safely: " + exception.Message);
            try
            {
                Disable();
            }
            catch (Exception cleanup)
            {
                log("cleanup: " + cleanup.Message);
            }
        }
    }

    void ApplyFrame()
    {
        var settings = configuration.Capture();
        var active = enabled() && settings.Enabled;
        if (!active)
        {
            if (previouslyActive != false)
                Disable();
            previouslyActive = false;
            return;
        }

        previouslyActive = true;
        UpdateGrading(settings, active);
        services.Surface.Apply(settings.Values, active);
        RefreshQuality(settings);
        EnsurePrefab();
        lastFrame = services.Scene.Capture();
        services.Renderer.Apply(settings, lastFrame);
    }

    void UpdateGrading(AtmosphereRenderSettings settings, bool active)
    {
        var gradingEnabled = active && settings.GradingEnabled;
        if (!ReferenceEquals(previousSettings, settings) || !gradingEnabled)
            services.Grading.Restore();
        if (gradingEnabled)
            services.Grading.Apply(settings.Values, true);
        previousSettings = settings;
    }

    void RefreshQuality(AtmosphereRenderSettings settings)
    {
        if (framesUntilQualityRefresh-- > 0)
            return;
        services.Quality.Apply(settings.Values);
        framesUntilQualityRefresh = 300;
    }

    void EnsurePrefab()
    {
        if (prefabError is not null)
            return;
        try
        {
            services.Prefab.EnsureSpawned();
        }
        catch (Exception exception)
        {
            prefabError = exception.Message;
            log(exception.Message);
        }
    }

    public void ConfigurationChanged(bool respawn)
    {
        services.Surface.Invalidate();
        prefabError = null;
        frameError = null;
        previousSettings = null;
        framesUntilQualityRefresh = 0;
        if (respawn)
            services.Prefab.RequestRespawn();
    }

    public void Disable() => Cleanup.Run(services.Surface.Restore, services.Renderer.Disable, services.Grading.Restore, services.Quality.Restore);
    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        Cleanup.Run(Disable, services.Dispose);
    }
}
