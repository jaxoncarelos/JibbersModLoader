using System.Globalization;
using System.Text.Json.Nodes;
using Jibbers.Sdk;
using Jibbers.Sdk.Interop;
using Arrow = Jibbers.Sdk.Game.Ui.ArrowSelection;
using MapLoad = Jibbers.Sdk.Game.Map.LoadEvent;

namespace RenderDistance;

/// <summary>
/// Uncaps the game's own Terrain / Object Render Distance options. Both are ArrowSelection rows
/// whose choices stop at 1500 m (defaultOptions "100,...,1500", bound to the game settings
/// <c>cull-terrains-distance</c> / <c>cull-objects-distance</c>). This gives those rows more choices
/// (up to 50 km by default), so you pick farther distances in the normal Options menu. The game's
/// TerrainCuller scales its detail tiers by the terrain distance, so far terrain chunks on big
/// multi-chunk maps stay drawn too.
/// </summary>
public sealed unsafe class RenderDistanceMod : JibbersMod
{
    public override string Id => "render_distance";
    public override string Name => "Render Distance";
    public override string Author => "jaxoncarelos";
    public override string Description => "Uncaps the game's Terrain and Object Render Distance options past 1500 m.";

    [Choice("Highest choice (m)", "50000", "3000", "5000", "10000", "20000", "50000", "100000", Key = "max_m", Section = "Render Distance",
        Tooltip = "The farthest distance the game's render distance options offer. Farther costs more GPU.")]
    public string MaxMeters { get; private set; } = "50000";

    static readonly string[] Globals = { "cull-terrains-distance", "cull-objects-distance" };
    // Last distance picked per setting: re-applied if the game snaps a saved far value back to 1500
    // before the menu rows are uncapped (e.g. while the menu loads before mods attach).
    readonly Dictionary<string, float> remembered = new();

    public override void OnLoad()
    {
        foreach (var (name, v) in State.Load(StatePath)) remembered[name] = v;
        MainThread.Later(() =>
        {
            foreach (var name in Globals) Watch(name);
            Uncap("startup");
        });
        MapLoad.Subscribe(_ => Uncap("map loaded"));
    }

    static string StatePath => Path.Combine(Path.GetDirectoryName(Environment.ProcessPath) ?? ".", "JibbersMods", "render_distance.state.json");

    public override void OnSettingChanged(string key, JsonNode value)
    {
        if (key is "max_m" or "enabled") MainThread.Later(() => Uncap("setting changed"));
    }

    void Watch(string name)
    {
        if (GameVars.GetFloat(name) is not null) GameVars.Watch(name, (float v) => Picked(name, v));
        else if (GameVars.GetInt(name) is not null) GameVars.Watch(name, (int v) => Picked(name, v));
        else Log($"{name}: not one of the game's number settings");
    }

    void Picked(string name, float value)
    {
        if (!Enabled) return;
        if (value < Choices.CapMeters && remembered.TryGetValue(name, out var far) && far > Choices.CapMeters && !userTouched.Contains(name))
        {
            // The game clamped a saved far distance; put it back once.
            userTouched.Add(name);
            MainThread.Later(() => SetGame(name, far));
            return;
        }
        userTouched.Add(name);
        remembered[name] = value;
        try { State.Save(StatePath, remembered); } catch (Exception e) { Log("could not save the distance: " + e.Message); }
    }
    readonly HashSet<string> userTouched = new();

    static void SetGame(string name, float value)
    {
        if (GameVars.GetFloat(name) is not null) GameVars.Set(name, value);
        else if (GameVars.GetInt(name) is not null) GameVars.Set(name, (int)MathF.Round(value));
    }

    void Uncap(string why)
    {
        try
        {
            var max = Enabled && float.TryParse(MaxMeters, NumberStyles.Float, CultureInfo.InvariantCulture, out var m) ? m : Choices.CapMeters;
            int rows = 0;
            foreach (var row in AllArrows())
            {
                string name;
                try { name = row.GlobalName; } catch { continue; }
                if (!Globals.Contains(name)) continue;
                var current = GameVars.GetFloat(name) ?? GameVars.GetInt(name) ?? 1500f;
                var options = Choices.Build(row.Raw.defaultOptions, max);
                if (options is null) continue;
                SetChoices(row, options, current);
                rows++;
            }
            foreach (var name in Globals)
            {
                if (Enabled && remembered.TryGetValue(name, out var far) && far <= max) SetGame(name, far);
            }
            Log($"{why}: {rows} render distance option(s) offer up to {max:0} m");
        }
        catch (Exception e) { Log($"{why}: could not uncap render distance: {e.Message}"); }
    }

    /// <summary>Every ArrowSelection, including the hidden Options window and menu prefabs.</summary>
    static IEnumerable<Arrow> AllArrows()
    {
        var resources = Il2Cpp.RequireClass(Unity.Core, "UnityEngine.Resources");
        var all = Unity.CallStatic(resources, "FindObjectsOfTypeAll", Arrow.Class.Class.SystemType);
        return Il2Cpp.ArrayObjects(all).Where(p => p != 0).Select(p => new Arrow(p));
    }

    /// <summary>Gives a row new choices, labels and values together, keeping the current distance selected.</summary>
    static void SetChoices(Arrow row, string[] options, float current)
    {
        var raw = row.Raw;
        raw.defaultOptions = string.Join(",", options);
        var values = options.Select(o => float.Parse(o, CultureInfo.InvariantCulture)).ToArray();
        var index = Choices.Nearest(values, current);
        // Labels through the game's own setter when the row is built, values alongside it so the
        // two arrays can never disagree in length.
        var floats = Il2Cpp.NewArray(Il2Cpp.RequireClass("mscorlib.dll", "System.Single"), values.Length);
        for (var i = 0; i < values.Length; i++) ((float*)Il2Cpp.ArrayData(floats))[i] = values[i];
        raw.wut = floats;
        if (raw.wus != 0) row.SetOptions(Il2Cpp.StringArray(options), index, false);
        raw.wut = floats; // SetOptions may rebuild the values; keep ours either way
    }
}

/// <summary>The last distance picked per setting, kept beside the mod's settings.</summary>
public static class State
{
    public static Dictionary<string, float> Load(string path)
    {
        var r = new Dictionary<string, float>();
        try
        {
            if (File.Exists(path) && JsonNode.Parse(File.ReadAllText(path)) is JsonObject o)
                foreach (var (k, v) in o) if (v is JsonValue jv && jv.TryGetValue<float>(out var f)) r[k] = f;
        }
        catch { }
        return r;
    }

    public static void Save(string path, IReadOnlyDictionary<string, float> values)
    {
        var o = new JsonObject();
        foreach (var (k, v) in values) o[k] = v;
        File.WriteAllText(path, o.ToJsonString());
    }
}

/// <summary>Pure choice-list logic (tested without the game).</summary>
public static class Choices
{
    public const float CapMeters = 1500;
    static readonly float[] Extra = { 2000, 2500, 3000, 4000, 5000, 7500, 10000, 15000, 20000, 30000, 50000, 75000, 100000 };

    /// <summary>The game's choices plus farther ones up to <paramref name="max"/>; null if the list isn't numbers.</summary>
    public static string[]? Build(string? gameOptions, float max)
    {
        if (string.IsNullOrWhiteSpace(gameOptions)) return null;
        var values = new List<float>();
        foreach (var part in gameOptions.Split(','))
        {
            if (!float.TryParse(part.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) return null;
            if (v <= CapMeters) values.Add(v); // drop extras we added earlier, re-add for the current max
        }
        if (values.Count == 0) return null;
        foreach (var v in Extra)
        {
            if (v <= max && v > values[^1]) values.Add(v);
        }
        return values.Select(v => v.ToString("0", CultureInfo.InvariantCulture)).ToArray();
    }

    public static int Nearest(float[] values, float current)
    {
        var best = 0;
        for (var i = 1; i < values.Length; i++)
        {
            if (MathF.Abs(values[i] - current) < MathF.Abs(values[best] - current)) best = i;
        }
        return best;
    }
}
