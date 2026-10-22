using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Jibbers.Sdk;

/// <summary>Declares a saved, typed mod setting on a property with a public getter and a setter.</summary>
[AttributeUsage(AttributeTargets.Property, Inherited = true)]
public abstract class SettingAttribute(string label) : Attribute
{
    public string Label { get; } = label;
    /// <summary>Optional stable storage key. Defaults to the property name in snake_case.</summary>
    public string? Key { get; set; }
    public string Section { get; set; } = "General";
    public string Tooltip { get; set; } = "";
    internal abstract Setting Declare(string key, Type propertyType);
}

/// <summary>A boolean setting.</summary>
public sealed class ToggleAttribute(string label, bool defaultValue = true) : SettingAttribute(label)
{
    internal override Setting Declare(string key, Type type) => type == typeof(bool)
        ? Setting.Toggle(key, Label, defaultValue) : throw new ArgumentException($"{key}: Toggle requires a bool property.");
}
/// <summary>A numeric slider backed by a float or double property.</summary>
public sealed class SliderAttribute(string label, double defaultValue, double min, double max, double step = 0.1) : SettingAttribute(label)
{
    internal override Setting Declare(string key, Type type) => type == typeof(float) || type == typeof(double)
        ? Setting.Slider(key, Label, defaultValue, min, max, step) : throw new ArgumentException($"{key}: Slider requires a float or double property.");
}
/// <summary>A key binding backed by a Key property.</summary>
public sealed class KeyBindingAttribute(string label, Jibbers.Sdk.Key defaultValue) : SettingAttribute(label)
{
    internal override Setting Declare(string key, Type type) => type == typeof(Jibbers.Sdk.Key)
        ? Setting.KeyBinding(key, Label, (int)defaultValue) : throw new ArgumentException($"{key}: KeyBinding requires a Key property.");
}
/// <summary>A string choice from a fixed list of options.</summary>
public sealed class ChoiceAttribute(string label, string defaultValue, params string[] options) : SettingAttribute(label)
{
    internal override Setting Declare(string key, Type type) => type == typeof(string)
        ? Setting.Choice(key, Label, options, defaultValue) : throw new ArgumentException($"{key}: Choice requires a string property.");
}

internal static class PropertySettings
{
    sealed record Binding(PropertyInfo Property, Setting Definition);
    static readonly ConcurrentDictionary<Type, Binding[]> cache = new();
    static Binding[] Bindings(Type type) => cache.GetOrAdd(type, Build);
    static Binding[] Build(Type type)
    {
        var result = new List<Binding>();
        foreach (var property in type.GetProperties(BindingFlags.Instance | BindingFlags.Public).OrderBy(p => p.MetadataToken))
        {
            if (property.GetCustomAttribute<SettingAttribute>() is not { } attribute) continue;
            if (property.GetMethod?.IsPublic != true || property.GetSetMethod(true) is null || property.GetIndexParameters().Length != 0)
                throw new ArgumentException($"{type.Name}.{property.Name}: setting properties require a getter and setter (the setter may be private).");
            var key = attribute.Key ?? Regex.Replace(Regex.Replace(property.Name, "([A-Z]+)([A-Z][a-z])", "$1_$2"), "([a-z0-9])([A-Z])", "$1_$2").ToLowerInvariant();
            var definition = attribute.Declare(key, property.PropertyType).InSection(attribute.Section).WithTooltip(attribute.Tooltip);
            definition.Check();
            if (result.Any(binding => binding.Definition.Key == key)) throw new ArgumentException($"Duplicate property setting key {key}.");
            result.Add(new(property, definition));
        }
        return result.ToArray();
    }
    public static IEnumerable<Setting> Schema(Type type) => Bindings(type).Select(binding => binding.Definition);
    public static void Load(JibbersMod mod)
    {
        foreach (var binding in Bindings(mod.GetType()))
        {
            var value = mod.Store!.Get(binding.Definition.Key) ?? throw new ArgumentException($"Property setting {binding.Definition.Key} is missing from Settings.");
            Apply(mod, binding, value);
        }
    }
    public static void Apply(JibbersMod mod, string key, JsonNode value)
    {
        var binding = Bindings(mod.GetType()).FirstOrDefault(binding => binding.Definition.Key == key);
        if (binding is not null) Apply(mod, binding, value);
    }
    static void Apply(JibbersMod mod, Binding binding, JsonNode value)
    {
        var type = binding.Property.PropertyType;
        object converted = type == typeof(bool) ? value.GetValue<bool>()
            : type == typeof(string) ? value.GetValue<string>()
            : type == typeof(Key) ? (Key)value.GetValue<int>()
            : type == typeof(float) ? (object)(float)double.Parse(value.ToJsonString(), System.Globalization.CultureInfo.InvariantCulture)
            : double.Parse(value.ToJsonString(), System.Globalization.CultureInfo.InvariantCulture);
        binding.Property.SetValue(mod, converted);
    }
}
