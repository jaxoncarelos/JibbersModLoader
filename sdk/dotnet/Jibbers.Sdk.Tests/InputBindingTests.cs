using System.Text.Json.Nodes;
using Jibbers.Sdk;
using Jibbers.Sdk.Features.Atmosphere;

static class InputBindingTests
{
    sealed class TestMod : JibbersMod
    {
        public override string Id => "input_binding_test";
        public override string Name => "Input binding test";
    }

    static Input.Binding Binding(JibbersMod mod, Action callback) => new()
    {
        Key = () => (int)Key.F3,
        Modifiers = () => true,
        Trigger = Input.Trigger.Pressed,
        Callback = callback,
        Owner = mod.Id,
    };

    static TestMod LoadMod()
    {
        var mod = new TestMod();
        mod.Store = new SettingsStore(new[] { Setting.Toggle("enabled", "Enabled", true) }, mod.Name, null);
        ModContext.Add(mod);
        return mod;
    }

    [Test]
    static void OrdinaryBindingsRespectEnabledState()
    {
        var mod = LoadMod();
        var calls = 0;
        var binding = Binding(mod, () => calls++);
        binding.Dispatch();
        mod.SetSetting("enabled", JsonValue.Create(false)!);
        binding.Dispatch();
        Assert.Equal(1, calls);
        mod.SetSetting("enabled", JsonValue.Create(true)!);
        binding.Dispatch();
        Assert.Equal(2, calls);
    }

    [Test]
    static void AtmosphereToggleCanTurnDisabledEffectsBackOn()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        try
        {
            var mod = LoadMod();
            var configuration = new AtmosphereConfigurationStore(() => path, _ => { });
            var binding = Binding(mod, () =>
            {
                configuration.ApplyCommand(new JsonObject { ["toggle"] = true });
                mod.SetSetting("enabled", JsonValue.Create(configuration.Capture().Enabled)!);
            });
            using var handle = new Input.Handle { B = binding }.AllowWhenDisabled();
            binding.Dispatch();
            Assert.True(!mod.Enabled);
            binding.Dispatch();
            Assert.True(mod.Enabled);
            Assert.True(configuration.Capture().Enabled);
            handle.Dispose();
            binding.Dispatch();
            Assert.True(mod.Enabled, "disposed bindings cannot change settings");
        }
        finally { File.Delete(path); }
    }

    [Test]
    static void AtmospherePresetsCanBeSelectedWhileEffectsAreOff()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        try
        {
            var mod = LoadMod();
            mod.SetSetting("enabled", JsonValue.Create(false)!);
            var configuration = new AtmosphereConfigurationStore(() => path, _ => { });
            configuration.ApplyCommand(new JsonObject { ["enabled"] = false });
            var originalPreset = configuration.Capture().Values["preset"]?.ToJsonString();
            var binding = Binding(mod, () => configuration.ApplyCommand(new JsonObject { ["nextPreset"] = true }));
            using var handle = new Input.Handle { B = binding }.AllowWhenDisabled();
            binding.Dispatch();
            Assert.True(originalPreset != configuration.Capture().Values["preset"]?.ToJsonString());
            Assert.True(!configuration.Capture().Enabled);
            Assert.True(!mod.Enabled);
        }
        finally { File.Delete(path); }
    }

    [Test]
    static void AllowWhenDisabledDoesNotKeepUnloadedBindingsAlive()
    {
        var mod = LoadMod();
        mod.SetSetting("enabled", JsonValue.Create(false)!);
        var calls = 0;
        var binding = Binding(mod, () => calls++);
        using var handle = new Input.Handle { B = binding }.AllowWhenDisabled();
        binding.Dispatch();
        Assert.Equal(1, calls);
        handle.Dispose();
        binding.Dispatch();
        Assert.Equal(1, calls);
    }
}
