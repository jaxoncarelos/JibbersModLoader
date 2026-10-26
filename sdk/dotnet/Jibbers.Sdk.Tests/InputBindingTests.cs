using System.Text.Json.Nodes;
using Jibbers.Sdk;

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
    static void VirtualKeysMapToUnityKeyCodes()
    {
        Assert.Equal(120, Input.UnityKeyCode(0x58));
        Assert.Equal(97, Input.UnityKeyCode(0x41));
        Assert.Equal(282, Input.UnityKeyCode(0x70));
        Assert.Equal(null, Input.UnityKeyCode(0));
        Assert.Equal(null, Input.UnityKeyCode(0x10000));
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
