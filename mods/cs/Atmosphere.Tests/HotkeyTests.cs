using System.Text.Json.Nodes;
using Atmosphere;

// The SDK's AllowWhenDisabled bindings are covered by Jibbers.Sdk.Tests; these check the
// configuration commands Atmosphere's hotkeys send while the mod is switched off.
static class HotkeyTests
{
    [Test]
    static void ToggleCanTurnDisabledEffectsBackOn()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        try
        {
            var configuration = new AtmosphereConfigurationStore(() => path, _ => { });
            configuration.ApplyCommand(new JsonObject { ["toggle"] = true });
            Assert.True(!configuration.Capture().Enabled);
            configuration.ApplyCommand(new JsonObject { ["toggle"] = true });
            Assert.True(configuration.Capture().Enabled);
        }
        finally { File.Delete(path); }
    }

    [Test]
    static void CycleSelectsAnActiveModeFromOff()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        try
        {
            var configuration = new AtmosphereConfigurationStore(() => path, _ => { });
            configuration.ApplyCommand(new JsonObject { ["enabled"] = false });
            var originalPreset = configuration.Capture().Values["preset"]?.ToJsonString();
            configuration.ApplyCommand(new JsonObject { ["nextPreset"] = true });
            Assert.True(originalPreset != configuration.Capture().Values["preset"]?.ToJsonString());
            Assert.True(configuration.Capture().Enabled);
        }
        finally { File.Delete(path); }
    }
}
