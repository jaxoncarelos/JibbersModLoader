using System.Text.Json.Nodes;

namespace Jibbers.Sdk.Features.Atmosphere;

/// <summary>One mode selection controls both the preset and whether effects are active.</summary>
internal static class AtmosphereModes
{
    public static string Current(JsonObject settings) => !AtmosphereConfiguration.GetBool(settings, "enabled")
        ? "Off" : settings["preset"]?.GetValue<string>() is "Foggy day" ? "Foggy" : settings["preset"]?.GetValue<string>() ?? "Foggy";

    public static string[] Choices(JsonObject settings) => new[] { "Off", "Clear", "Foggy", "Snowstorm" }
        .Concat(AtmosphereConfiguration.PresetOrder(settings["presets"] as JsonObject ?? new())
            .Where(name => name is not ("Foggy day" or "Clear" or "Snowstorm" or "Off" or "Foggy"))).ToArray();

    public static string Next(JsonObject settings)
    {
        var choices = Choices(settings);
        var index = Array.IndexOf(choices, Current(settings));
        return choices[(index + 1) % choices.Length];
    }

    public static JsonObject Select(JsonObject settings, string mode)
    {
        if (mode == "Off")
        {
            var disabled = (JsonObject)settings.DeepClone();
            disabled["enabled"] = false;
            return disabled;
        }
        var selected = AtmosphereConfiguration.WithPreset(settings, mode == "Foggy" ? "Foggy day" : mode);
        selected["enabled"] = true;
        return selected;
    }
}
