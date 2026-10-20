using System.Text.Json;
using System.Text.Json.Nodes;

namespace Jibbers.Sdk;

/// <summary>How a setting is edited in the Mods tab.</summary>
public enum SettingKind
{
    /// <summary>On/off.</summary>
    Toggle,
    /// <summary>A number in a range.</summary>
    Slider,
    /// <summary>One of a list of options.</summary>
    Choice,
    /// <summary>A Windows virtual-key code (0x58 = X, 0x78 = F9).</summary>
    Key,
}

/// <summary>
/// One mod setting. Declare them in <see cref="JibbersMod.Settings"/>; the Mods tab shows them, the
/// SDK saves them to <c>JibbersMods\&lt;id&gt;.settings.json</c>, and you read them with
/// <see cref="JibbersMod.GetBool"/> and friends.
/// </summary>
public sealed class Setting
{
    /// <summary>Stable key (stored in the settings file).</summary>
    public string Key { get; }
    /// <summary>Label in the Mods tab.</summary>
    public string Label { get; }
    /// <summary>Collapsible heading in the Mods tab (default "General").</summary>
    public string Section { get; private set; } = "General";
    /// <summary>Hover text.</summary>
    public string Tooltip { get; private set; } = "";
    /// <summary>How it is edited.</summary>
    public SettingKind Kind { get; }
    /// <summary>Default value.</summary>
    public JsonNode Default { get; }
    /// <summary>Slider minimum.</summary>
    public double Min { get; private init; }
    /// <summary>Slider maximum.</summary>
    public double Max { get; private init; }
    /// <summary>Slider step.</summary>
    public double Step { get; private init; }
    /// <summary>Choice options.</summary>
    public IReadOnlyList<string> Options { get; private init; } = Array.Empty<string>();

    Setting(string key, string label, SettingKind kind, JsonNode def)
    {
        Key = key;
        Label = label;
        Kind = kind;
        Default = def;
    }

    /// <summary>An on/off setting.</summary>
    public static Setting Toggle(string key, string label, bool @default) => new(key, label, SettingKind.Toggle, JsonValue.Create(@default));

    /// <summary>A number from <paramref name="min"/> to <paramref name="max"/> in steps of <paramref name="step"/>.</summary>
    public static Setting Slider(string key, string label, double @default, double min, double max, double step) =>
        new(key, label, SettingKind.Slider, JsonValue.Create(@default)) { Min = min, Max = max, Step = step };

    /// <summary>One of <paramref name="options"/>.</summary>
    public static Setting Choice(string key, string label, IEnumerable<string> options, string @default) =>
        new(key, label, SettingKind.Choice, JsonValue.Create(@default)) { Options = options.ToArray() };

    /// <summary>A key binding (Windows virtual-key code, 0-255).</summary>
    public static Setting KeyBinding(string key, string label, int virtualKey) => new(key, label, SettingKind.Key, JsonValue.Create(virtualKey));

    /// <summary>Puts the setting under a heading.</summary>
    public Setting InSection(string section)
    {
        Section = section;
        return this;
    }

    /// <summary>Adds hover text.</summary>
    public Setting WithTooltip(string tooltip)
    {
        Tooltip = tooltip;
        return this;
    }

    /// <summary>The <c>plugin.info</c> schema entry the Mods tab reads.</summary>
    public JsonObject ToJson()
    {
        var o = new JsonObject
        {
            ["key"] = Key,
            ["label"] = Label,
            ["default"] = Default.DeepClone(),
            ["section"] = Section,
            ["tooltip"] = Tooltip,
        };
        switch (Kind)
        {
            case SettingKind.Slider:
                o["min"] = Min;
                o["max"] = Max;
                o["step"] = Step;
                break;
            case SettingKind.Choice:
                o["options"] = new JsonArray(Options.Select(s => (JsonNode)JsonValue.Create(s)!).ToArray());
                break;
        }
        o["type"] = Kind switch
        {
            SettingKind.Toggle => "toggle",
            SettingKind.Slider => "slider",
            SettingKind.Choice => "choice",
            _ => "key",
        };
        return o;
    }

    /// <summary>A JSON number as a double, whatever it was built from (parsed text, int, float...).</summary>
    static bool Number(JsonNode? v, out double x)
    {
        x = 0;
        if (v is not JsonValue j)
        {
            return false;
        }
        if (j.TryGetValue(out JsonElement e))
        {
            return e.ValueKind == JsonValueKind.Number && e.TryGetDouble(out x);
        }
        if (j.TryGetValue(out double d)) { x = d; return true; }
        if (j.TryGetValue(out float f)) { x = f; return true; }
        if (j.TryGetValue(out long l)) { x = l; return true; }
        if (j.TryGetValue(out int i)) { x = i; return true; }
        if (j.TryGetValue(out uint u)) { x = u; return true; }
        if (j.TryGetValue(out short sh)) { x = sh; return true; }
        if (j.TryGetValue(out byte b)) { x = b; return true; }
        if (j.TryGetValue(out decimal m)) { x = (double)m; return true; }
        return false;
    }

    /// <summary>Checks a value and returns what to store: sliders are clamped, choices must be options, keys 0-255.</summary>
    public JsonNode Normalize(JsonNode? v)
    {
        switch (Kind)
        {
            case SettingKind.Toggle:
                if (v is JsonValue b && b.TryGetValue(out bool on))
                {
                    return JsonValue.Create(on);
                }
                throw new ArgumentException($"{Key} takes true or false");
            case SettingKind.Slider:
                if (Number(v, out var x) && double.IsFinite(x))
                {
                    return JsonValue.Create(Math.Round(Math.Clamp(x, Min, Max) * 1e6) / 1e6);
                }
                throw new ArgumentException($"{Key} takes a number");
            case SettingKind.Choice:
                if (v is JsonValue s && s.TryGetValue(out string? str) && Options.Contains(str))
                {
                    return JsonValue.Create(str)!;
                }
                throw new ArgumentException($"{Key} takes one of {string.Join(", ", Options)}");
            default:
                if (Number(v, out var code) && code >= 0 && code <= 255 && code == Math.Floor(code))
                {
                    return JsonValue.Create((int)code);
                }
                throw new ArgumentException($"{Key} takes a key code (0-255)");
        }
    }

    internal void Check()
    {
        if (string.IsNullOrEmpty(Key))
        {
            throw new ArgumentException("a setting has an empty key");
        }
        if (Kind == SettingKind.Slider && !(double.IsFinite(Min) && double.IsFinite(Max) && double.IsFinite(Step) && Min < Max && Step > 0))
        {
            throw new ArgumentException($"{Key}: slider needs finite min < max and step > 0");
        }
        if (Kind == SettingKind.Choice && Options.Count == 0)
        {
            throw new ArgumentException($"{Key}: choice has no options");
        }
        Normalize(Default);
    }
}

/// <summary>The current values of one mod's settings, saved as JSON.</summary>
public sealed class SettingsStore
{
    readonly List<Setting> settings;
    readonly Dictionary<string, JsonNode> values = new();
    readonly string? path;
    readonly object gate = new();

    /// <summary>
    /// Validates the declaration, adds an <c>enabled</c> toggle first unless there is one (when
    /// <paramref name="enabledLabel"/> is set), and loads saved values (missing or bad entries use defaults).
    /// </summary>
    public SettingsStore(IEnumerable<Setting> declared, string? enabledLabel, string? path)
    {
        settings = declared.ToList();
        if (enabledLabel is not null && settings.All(s => s.Key != "enabled"))
        {
            settings.Insert(0, Setting.Toggle("enabled", enabledLabel, true).WithTooltip("Turns this mod on or off."));
        }
        for (var i = 0; i < settings.Count; i++)
        {
            settings[i].Check();
            if (settings.Take(i).Any(o => o.Key == settings[i].Key))
            {
                throw new ArgumentException($"duplicate setting key {settings[i].Key}");
            }
        }
        this.path = path;
        JsonObject? saved = null;
        try
        {
            if (path is not null && File.Exists(path))
            {
                saved = JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
            }
        }
        catch (Exception)
        {
            // A damaged file means defaults.
        }
        foreach (var s in settings)
        {
            JsonNode v;
            try
            {
                v = saved?[s.Key] is { } sv ? s.Normalize(sv) : s.Default.DeepClone();
            }
            catch (ArgumentException)
            {
                v = s.Default.DeepClone();
            }
            values[s.Key] = v;
        }
    }

    /// <summary>The declared settings, <c>enabled</c> included.</summary>
    public IReadOnlyList<Setting> Settings => settings;

    /// <summary>The <c>plugin.info</c> <c>settings</c> array.</summary>
    public JsonArray Schema() => new(settings.Select(s => (JsonNode)s.ToJson()).ToArray());

    /// <summary>Current value of a key, or null.</summary>
    public JsonNode? Get(string key)
    {
        lock (gate)
        {
            return values.TryGetValue(key, out var v) ? v.DeepClone() : null;
        }
    }

    /// <summary>All values as an object.</summary>
    public JsonObject Values()
    {
        lock (gate)
        {
            var o = new JsonObject();
            foreach (var (k, v) in values)
            {
                o[k] = v.DeepClone();
            }
            return o;
        }
    }

    /// <summary>Validates, stores and saves a value; returns it as stored.</summary>
    public JsonNode Set(string key, JsonNode? value)
    {
        var s = settings.FirstOrDefault(x => x.Key == key) ?? throw new ArgumentException($"unknown setting {key}");
        var v = s.Normalize(value);
        lock (gate)
        {
            values[key] = v;
        }
        Save();
        return v.DeepClone();
    }

    void Save()
    {
        if (path is null)
        {
            return;
        }
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, Values().ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, path, true);
        }
        catch (Exception e)
        {
            Log.Info($"settings: could not save {path}: {e.Message}");
        }
    }
}
