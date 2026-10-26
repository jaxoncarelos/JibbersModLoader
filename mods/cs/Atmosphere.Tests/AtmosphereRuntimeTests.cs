using System.Text.Json.Nodes;
using Jibbers.Sdk;
using Atmosphere;

static class AtmosphereRuntimeTests
{
    sealed class Settings : IAtmosphereSettings
    {
        public AtmosphereRenderSettings Current = new(AtmosphereConfiguration.Defaults());
        public AtmosphereRenderSettings Capture() => Current;
    }

    sealed class Scene : IAtmosphereScene
    {
        public Action Tick = null !;
        public int Starts, Captures;
        public bool FailCapture, FailStart, Disposed;
        public void Start(Action frame)
        {
            Tick = frame;
            Starts++;
            if (FailStart)
                throw new InvalidOperationException("hook");
        }

        public AtmosphereFrame Capture()
        {
            Captures++;
            if (FailCapture)
                throw new InvalidOperationException("camera");
            return default;
        }

        public void Dispose() => Disposed = true;
    }

    // One test spy implements independent ports; production adapters each own one responsibility.
    sealed class Effects : IAtmospherePrefab, IAtmosphereSurface, IAtmosphereQuality, IAtmosphereGrading, IAtmosphereRenderer
    {
        public bool Spawned => Spawns > 0;

        public int Spawns, Respawns, Invalidations, Restores, Rendered, Disabled, QualityChanges, GradingChanges, Disposals;
        public bool FailSpawn, FailRestore;
        public void EnsureSpawned()
        {
            Spawns++;
            if (FailSpawn)
                throw new InvalidOperationException("bundle");
        }

        public void RequestRespawn() => Respawns++;
        public void Apply(JsonObject settings, bool enabled)
        {
        }

        void IAtmosphereQuality.Apply(JsonObject settings) => QualityChanges++;
        void IAtmosphereGrading.Apply(JsonObject settings, bool enabled) => GradingChanges++;
        public void Invalidate() => Invalidations++;
        public void Restore()
        {
            Restores++;
            if (FailRestore)
                throw new InvalidOperationException("restore");
        }

        public void Apply(AtmosphereRenderSettings settings, AtmosphereFrame frame) => Rendered++;
        public void Disable() => Disabled++;
        public void Dispose() => Disposals++;
    }

    sealed class Fixture
    {
        public readonly Settings Settings = new();
        public readonly Scene Scene = new();
        public readonly Effects Effects = new();
        public readonly List<string> Logs = new();
        public bool Enabled = true;
        public AtmosphereRuntime Runtime() => new(Settings, new(Scene, Effects, Effects, Effects, Effects, Effects), () => Enabled, Logs.Add);
    }

    [Test]
    static void ActiveFramesRenderAndQualityIsThrottled()
    {
        var fixture = new Fixture();
        using var runtime = fixture.Runtime();
        runtime.Start();
        runtime.Start();
        for (var frame = 0; frame < 301; frame++)
            fixture.Scene.Tick();
        Assert.Equal(301, fixture.Effects.Rendered);
        Assert.Equal(1, fixture.Scene.Starts);
        Assert.Equal(1, fixture.Effects.QualityChanges);
        fixture.Scene.Tick();
        Assert.Equal(2, fixture.Effects.QualityChanges);
    }

    [Test]
    static void DisabledFeatureStopsSceneQueriesAndClearsShaders()
    {
        var fixture = new Fixture();
        using var runtime = fixture.Runtime();
        runtime.Start();
        fixture.Scene.Tick();
        fixture.Enabled = false;
        fixture.Scene.Tick();
        Assert.Equal(1, fixture.Scene.Captures);
        Assert.Equal(1, fixture.Effects.Rendered);
        Assert.Equal(1, fixture.Effects.Disabled);
        Assert.True(fixture.Effects.Restores >= 3, "disabled effects restore surface, grading and quality immediately");
    }

    [Test]
    static void ReplacedSettingsRestorePreviousGradingBeforeApplyingNewValues()
    {
        var fixture = new Fixture();
        using var runtime = fixture.Runtime();
        runtime.Start();
        fixture.Scene.Tick();
        var restores = fixture.Effects.Restores;
        fixture.Scene.Tick();
        Assert.Equal(restores, fixture.Effects.Restores);
        fixture.Settings.Current = new(AtmosphereConfiguration.Defaults());
        fixture.Scene.Tick();
        Assert.Equal(restores + 1, fixture.Effects.Restores);
    }

    [Test]
    static void MissingPrefabRetriesOnlyAfterConfigurationChange()
    {
        var fixture = new Fixture();
        fixture.Effects.FailSpawn = true;
        using var runtime = fixture.Runtime();
        runtime.Start();
        fixture.Scene.Tick();
        fixture.Scene.Tick();
        Assert.Equal(1, fixture.Effects.Spawns);
        Assert.Equal(1, fixture.Logs.Count);
        runtime.ConfigurationChanged(true);
        fixture.Effects.FailSpawn = false;
        fixture.Scene.Tick();
        Assert.Equal(2, fixture.Effects.Spawns);
        Assert.Equal(1, fixture.Effects.Respawns);
        Assert.Equal<string?>(null, runtime.Status.Error);
    }

    [Test]
    static void MeasurementFailureClearsEffectsAndCanRecoverAfterSettingsChange()
    {
        var fixture = new Fixture();
        using var runtime = fixture.Runtime();
        runtime.Start();
        fixture.Scene.FailCapture = true;
        fixture.Scene.Tick();
        fixture.Scene.Tick();
        Assert.Equal(1, fixture.Scene.Captures);
        Assert.Equal(1, fixture.Logs.Count);
        Assert.Equal(1, fixture.Effects.Disabled);
        Assert.True(fixture.Effects.Restores >= 3);
        fixture.Scene.FailCapture = false;
        runtime.ConfigurationChanged(false);
        fixture.Scene.Tick();
        Assert.Equal(1, fixture.Effects.Rendered);
    }

    [Test]
    static void UnloadAttemptsAllRestorationAndDisposalEvenAfterFailure()
    {
        var fixture = new Fixture();
        var runtime = fixture.Runtime();
        runtime.Start();
        fixture.Effects.FailRestore = true;
        Assert.Throws<AggregateException>(runtime.Dispose);
        Assert.True(fixture.Scene.Disposed);
        Assert.Equal(5, fixture.Effects.Disposals);
        fixture.Scene.Tick();
        Assert.Equal(0, fixture.Scene.Captures);
        runtime.Dispose();
    }

    [Test]
    static void HookFailureReleasesEveryPort()
    {
        var fixture = new Fixture();
        fixture.Scene.FailStart = true;
        var runtime = fixture.Runtime();
        Assert.Throws<InvalidOperationException>(runtime.Start);
        Assert.True(fixture.Scene.Disposed);
        Assert.Equal(5, fixture.Effects.Disposals);
    }

    [Test]
    static void StoreRoundTripsSettingsWithoutMutatingPreviousSnapshot()
    {
        var path = Path.Combine(Path.GetTempPath(), "atmosphere-" + Guid.NewGuid() + ".json");
        try
        {
            var store = new AtmosphereConfigurationStore(() => path, _ =>
            {
            });
            _ = store.Schema;
            var original = store.Capture();
            var originalSnow = original.Snow.Amount;
            store.ApplyMenuSetting("snow", JsonValue.Create(0.25)!);
            Assert.Equal(originalSnow, original.Snow.Amount);
            Assert.Equal(0.25f, store.Capture().Snow.Amount);
            var loaded = new AtmosphereConfigurationStore(() => path, _ =>
            {
            });
            _ = loaded.Schema;
            Assert.Equal(0.25f, loaded.Capture().Snow.Amount);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    static void MenuSynchronizationDoesNotWriteBackOrInvalidateItsSnapshot()
    {
        var store = new AtmosphereConfigurationStore(() => throw new InvalidOperationException("no writes expected"), _ =>
        {
        });
        var snapshot = store.Capture();
        var calls = 0;
        store.SynchronizeMenu((key, value) =>
        {
            calls++;
            Assert.True(!store.ApplyMenuSetting(key, value).Changed);
        });
        Assert.True(calls > 10);
        Assert.True(ReferenceEquals(snapshot, store.Capture()));
    }
}
