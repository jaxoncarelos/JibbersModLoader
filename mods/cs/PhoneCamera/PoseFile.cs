using System.Text.Json;

namespace PhoneCamera;

/// <summary>
/// Poses saved in the game's pose editor (<c>%APPDATA%\Jibbers\Poses\*.jbrpose</c>, JSON): a name and
/// <c>value-0</c>..<c>value-56</c>, 19 joints' bends, then their spreads, then their twists (0..1).
/// </summary>
internal static class PoseFile
{
    public static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Jibbers", "Poses");

    static string Squash(string s) => new string(s.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();

    /// <summary>The values of one pose file's JSON, or null if it isn't named <paramref name="name"/> (ignoring case and spaces).</summary>
    public static float[]? Parse(string json, string name)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (!root.TryGetProperty("name", out var n) || n.ValueKind != JsonValueKind.String || Squash(n.GetString()!) != Squash(name))
            return null;
        var count = root.TryGetProperty("data-count", out var c) && c.TryGetInt32(out var k) ? k : 57;
        var data = root.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.Object ? d : default;
        var values = new float[Math.Clamp(count, 0, 512)];
        for (var i = 0; i < values.Length; i++)
            values[i] = data.ValueKind == JsonValueKind.Object && data.TryGetProperty($"value-{i}", out var v) && v.TryGetDouble(out var x) ? (float)x : 0.5f;
        return values;
    }

    /// <summary>The saved pose named <paramref name="name"/>, from the game's pose folder.</summary>
    public static float[] Read(string name)
    {
        if (!Directory.Exists(Folder))
            throw new FileNotFoundException($"no pose folder at {Folder}");
        foreach (var file in Directory.EnumerateFiles(Folder, "*.jbrpose"))
        {
            try
            {
                if (Parse(File.ReadAllText(file), name) is { } values)
                    return values;
            }
            catch (JsonException)
            {
            }
        }
        throw new FileNotFoundException($"no saved pose called {name} in {Folder}");
    }
}
