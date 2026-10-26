using System.Text.Json.Nodes;
using Jibbers.Sdk;
using Jibbers.Sdk.Game.Physics;

static class AuthoringTests
{
    sealed class TypedMod : JibbersMod
    {
        public override string Id => "typed_test";
        public override string Name => "Typed test";
        [Slider("Camera distance", 3.5, 1, 10)]
        public float CameraDistance { get; private set; }
        [Toggle("Trails")]
        public bool Trails { get; private set; }
        [Choice("Mode", "Normal", "Gentle", "Normal")]
        public string Mode { get; private set; } = "";
        [KeyBinding("Apply", Key.F9, Key = "hotkey")]
        public Key ApplyKey { get; private set; }
        public float DistanceDuringCallback;
        public override void OnSettingChanged(string key, JsonNode value) => DistanceDuringCallback = CameraDistance;
    }
    static TypedMod Loaded()
    {
        var mod = new TypedMod();
        mod.Store = new SettingsStore(mod.Settings, mod.Name, null);
        PropertySettings.Load(mod);
        return mod;
    }
    [Test]
    static void PropertiesReceiveDefaultsAndStableKeys()
    {
        var mod = Loaded();
        Assert.Equal(3.5f, mod.CameraDistance);
        Assert.True(mod.Trails);
        Assert.Equal("Normal", mod.Mode);
        Assert.Equal(Key.F9, mod.ApplyKey);
        Assert.True(mod.Settings.Select(setting => setting.Key).SequenceEqual(new[] { "camera_distance", "trails", "mode", "hotkey" }));
    }
    [Test]
    static void PropertyChangesAreNormalizedBeforeCallbacks()
    {
        var mod = Loaded();
        mod.SetSetting("camera_distance", JsonValue.Create(20)!);
        Assert.Equal(10f, mod.CameraDistance);
        Assert.Equal(10f, mod.DistanceDuringCallback);
        mod.SetSetting("hotkey", JsonValue.Create((int)Key.V)!);
        Assert.Equal(Key.V, mod.ApplyKey);
        mod.SetSetting("trails", JsonValue.Create(false)!);
        Assert.True(!mod.Trails);
        Assert.Throws<ArgumentException>(() => mod.SetSetting("mode", JsonValue.Create("Unknown")!));
    }
    [Test]
    static void PhoneProtocolUpdatesTypedProperties()
    {
        var mod = Loaded();
        var plugin = new ModPlugin(mod);
        plugin.Handle(new JsonObject { ["cmd"] = "plugin.set", ["key"] = "camera_distance", ["value"] = 6.5 });
        Assert.Equal(6.5f, mod.CameraDistance);
        Assert.Equal(6.5f, mod.DistanceDuringCallback);
    }
    [Test]
    static void SavedValuesAreLoadedIntoProperties()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        try
        {
            var first = new TypedMod();
            first.Store = new SettingsStore(first.Settings, first.Name, path);
            first.SetSetting("camera_distance", JsonValue.Create(7.5)!);
            var second = new TypedMod();
            second.Store = new SettingsStore(second.Settings, second.Name, path);
            PropertySettings.Load(second);
            Assert.Equal(7.5f, second.CameraDistance);
        }
        finally { File.Delete(path); }
    }
    sealed class BadType : JibbersMod
    {
        public override string Id => "bad";
        public override string Name => "Bad";
        [Toggle("Wrong type")] public int Wrong { get; set; }
    }
    sealed class DuplicateKeys : JibbersMod
    {
        public override string Id => "bad";
        public override string Name => "Bad";
        [Toggle("One", Key = "same")] public bool One { get; set; }
        [Toggle("Two", Key = "same")] public bool Two { get; set; }
    }
    [Test]
    static void InvalidPropertyDeclarationsFailEarly()
    {
        Assert.Throws<ArgumentException>(() => new BadType().Settings.ToArray());
        Assert.Throws<ArgumentException>(() => new DuplicateKeys().Settings.ToArray());
    }
    [Test]
    static void FractionalStepsHaveNoDriftAndResetWhenReleased()
    {
        var limiter = new StepLimiter();
        Assert.Equal(100, Enumerable.Range(0, 1000).Count(_ => limiter.ShouldStep(true, 0.1)));
        Assert.True(!limiter.ShouldStep(true, 0.25));
        Assert.True(limiter.ShouldStep(false, 1));
        Assert.True(!limiter.ShouldStep(true, 0.75));
        Assert.Throws<ArgumentOutOfRangeException>(() => limiter.ShouldStep(true, double.NaN));
    }
}
