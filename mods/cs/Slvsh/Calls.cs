using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Slvsh;

internal enum TrickType { Jump, Rail }

/// <summary>
/// One trick of a called line, built in the radial menu. Every part is optional: null means
/// "don't care". Grabs are never called (they differ by moveset). Directions are "left", "right",
/// "front", "back" or "none"; rotations are "flat", "cork", "misty", "bio", "lincoln", "frontflip"
/// or "backflip"; <see cref="Count"/> is single (1) to quad (4).
/// </summary>
internal sealed record CalledTrick
{
    public TrickType Type { get; init; }
    /// <summary>"regular" or "switch" (into the jump, or onto the rail).</summary>
    public string? Stance { get; init; }
    /// <summary>Jump: the spin. Rail: the spin on.</summary>
    public string? Spin { get; init; }
    public string? Axis { get; init; }
    public int? Count { get; init; }
    public int? Degrees { get; init; }
    /// <summary>Rail: "slide", "frontslide", "backslide", "5050" or "5-0".</summary>
    public string? Slide { get; init; }
    /// <summary>Rail: the spin off ("none" = straight off).</summary>
    public string? Off { get; init; }
    public string? OffAxis { get; init; }
    public int? OffCount { get; init; }
    public int? OffDegrees { get; init; }

    public const int MaxDegrees = 3600;
    static readonly string[] Stances = { "regular", "switch" };
    static readonly string[] Spins = { "left", "right", "none" };
    static readonly string[] Offs = { "left", "right", "front", "back", "none" };
    public static readonly string[] Axes = { "flat", "cork", "misty", "bio", "lincoln", "frontflip", "backflip" };
    public static readonly string[] Slides = { "slide", "frontslide", "backslide", "5050", "5-0" };

    /// <summary>What to show for it: "Switch Left Dub Cork 1080", "Left 270 On - Slide - Right 270 Out".</summary>
    public string Describe()
    {
        if (Type == TrickType.Jump)
        {
            var words = new List<string>();
            if (Stance == "switch") words.Add("Switch");
            if (Axis is "frontflip" or "backflip")
            {
                words.Add(CountWord(Count) + (Axis == "frontflip" ? "Frontflip" : "Backflip"));
                return string.Join(" ", words);
            }
            if (Spin is "left" or "right") words.Add(Title(Spin));
            else if (Spin is null) words.Add("Any way");
            if (Axis is not null and not "flat") words.Add(CountWord(Count) + Title(Axis));
            else if (Axis is null) words.Add("any rotation");
            if (Spin == "none" && Axis is "flat" && Degrees is null or 0) return string.Join(" ", words.Append("Straight Air"));
            words.Add(Degrees is { } d ? d.ToString() : "any degrees");
            return string.Join(" ", words);
        }
        var on = (Stance == "switch" ? "Switch " : Stance == "regular" ? "Regular " : "")
            + (Spin is "left" or "right" ? $"{Title(Spin)} {Degrees?.ToString() ?? "any degrees"} On" : Spin == "none" ? "On (normal)" : "Any way On");
        var slide = Slide switch { null => "any slide", "5050" => "50-50", "5-0" => "5-0", var s => Title(s) };
        var off = Off switch
        {
            null => "any way off",
            "none" => "straight off",
            _ => $"{Title(Off)} {(OffAxis is not null and not "flat" ? CountWord(OffCount) + Title(OffAxis) + " " : "")}{OffDegrees?.ToString() ?? "any degrees"} Out",
        };
        return $"{on} - {slide} - {off}";
    }

    static string CountWord(int? count) => count switch { 2 => "Dub ", 3 => "Trip ", 4 => "Quad ", _ => "" };
    static string Title(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    /// <summary>Whether the game's name for a landed trick is this trick. Grabs and taps are ignored.</summary>
    public bool Matches(string name)
    {
        var parsed = TrickName.Parse(name);
        if (Type == TrickType.Jump)
        {
            if (parsed.Rail || parsed.Segments.Count != 1) return false;
            var air = parsed.Segments[0];
            return Is(Stance, air.Switch ? "switch" : "regular") && Air(air, Spin, Axis, Count, Degrees);
        }
        if (!parsed.Rail) return false;
        var entry = parsed.Segments[0];
        if (!Is(Stance, entry.Switch ? "switch" : "regular") || !Is(Spin, entry.Dir ?? "none")) return false;
        if (Spin is "left" or "right" && Degrees is { } d && entry.Degrees != d) return false;
        var slide = parsed.Segments.FirstOrDefault(s => s.Kind == SegmentKind.Slide);
        if (Slide is not null && (slide is null || TrickName.SlideKey(slide.Words) != TrickName.SlideKey(Slide))) return false;
        var exit = parsed.Segments.FirstOrDefault(s => s.Kind == SegmentKind.Out);
        if (Off is null) return true;
        if (Off == "none") return exit is null || exit.Dir is null;
        return exit is not null && Air(exit, Off, OffAxis, OffCount, OffDegrees);
    }

    static bool Is(string? wanted, string actual) => wanted is null || wanted == actual;

    static bool Air(TrickName.Segment s, string? spin, string? axis, int? count, int? degrees)
    {
        var actualAxis = s.Axis ?? "flat";
        if (axis is "frontflip" or "backflip")
            return actualAxis == axis && (count is null || s.Count == count);
        if (!Is(axis, actualAxis) || !Is(spin, s.Dir ?? "none")) return false;
        if (count is { } c && actualAxis != "flat" && s.Count != c) return false;
        return degrees is not { } d || s.Degrees == d;
    }

    public JsonObject ToJson()
    {
        var o = new JsonObject { ["type"] = Type == TrickType.Rail ? "rail" : "jump" };
        void Put(string key, string? s) { if (s is not null) o[key] = s; }
        void PutInt(string key, int? i) { if (i is not null) o[key] = i; }
        Put("stance", Stance); Put("spin", Spin); Put("axis", Axis); PutInt("count", Count); PutInt("deg", Degrees);
        Put("slide", Slide); Put("off", Off); Put("offaxis", OffAxis); PutInt("offcount", OffCount); PutInt("offdeg", OffDegrees);
        return o;
    }

    /// <summary>Reads one trick; null for anything malformed (it came over the network).</summary>
    public static CalledTrick? FromJson(JsonNode? n)
    {
        if (n is not JsonObject o) return null;
        string? Pick(string key, string[] allowed) => o[key]?.GetValue<string>() is { } s ? (allowed.Contains(s) ? s : throw new FormatException(key)) : null;
        int? Int(string key, int max) => o[key]?.GetValue<int>() is { } i ? (i >= 0 && i <= max ? i : throw new FormatException(key)) : null;
        var type = o["type"]?.GetValue<string>() switch { "jump" => TrickType.Jump, "rail" => TrickType.Rail, _ => throw new FormatException("type") };
        return new CalledTrick
        {
            Type = type, Stance = Pick("stance", Stances), Spin = Pick("spin", Spins), Axis = Pick("axis", Axes), Count = Int("count", 4), Degrees = Int("deg", MaxDegrees),
            Slide = Pick("slide", Slides), Off = Pick("off", Offs), OffAxis = Pick("offaxis", Axes), OffCount = Int("offcount", 4), OffDegrees = Int("offdeg", MaxDegrees),
        };
    }
}

/// <summary>A called line: the tricks to land in order, in one run.</summary>
internal static class CalledLine
{
    public const int MaxTricks = 8;

    public static string Describe(IReadOnlyList<CalledTrick> line) => string.Join("  >  ", line.Select(t => t.Describe()));

    public static JsonArray ToJson(IReadOnlyList<CalledTrick> line) => new(line.Select(t => (JsonNode)t.ToJson()).ToArray());

    /// <summary>Reads a line; empty for anything malformed or too long.</summary>
    public static IReadOnlyList<CalledTrick> FromJson(JsonNode? n)
    {
        try
        {
            if (n is not JsonArray a || a.Count > MaxTricks) return Array.Empty<CalledTrick>();
            var line = a.Select(CalledTrick.FromJson).ToList();
            return line.All(t => t is not null) ? line! : Array.Empty<CalledTrick>();
        }
        catch (Exception e) when (e is InvalidOperationException or FormatException)
        {
            return Array.Empty<CalledTrick>();
        }
    }
}

internal enum SegmentKind { Air, On, Slide, Out, To }

/// <summary>
/// Reads the game's trick names. A jump is one part ("Switch Left Dub Cork 1080 Safety Nosetap");
/// a rail trick is parts joined by " - ": on, slide, and off ("Left 270 On - Slide - Back Misty 720
/// Cuban Out", "... - To Regular"). Words it doesn't know (grabs, taps) are kept but never matched.
/// </summary>
internal static class TrickName
{
    internal sealed record Segment(SegmentKind Kind, bool Switch, string? Dir, string? Axis, int Count, int Degrees, IReadOnlyList<string> Words);
    internal sealed record Parsed(bool Rail, IReadOnlyList<Segment> Segments);

    static readonly Dictionary<string, int> Counts = new() { ["dub"] = 2, ["double"] = 2, ["trip"] = 3, ["triple"] = 3, ["quad"] = 4 };
    static readonly HashSet<string> AxisWords = new() { "cork", "misty", "bio", "lincoln" };

    public static Parsed Parse(string name)
    {
        var parts = name.Split(" - ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var segments = parts.Select(ParseSegment).ToList();
        return new Parsed(segments.Count > 0 && segments[0].Kind == SegmentKind.On, segments);
    }

    static Segment ParseSegment(string text)
    {
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(w => w.ToLowerInvariant()).ToList();
        var kind = words.Count > 0 && words[^1] == "on" ? SegmentKind.On
            : words.Count > 0 && words[^1] == "out" ? SegmentKind.Out
            : words.Count > 0 && words[0] == "to" ? SegmentKind.To
            : SegmentKind.Air;
        bool isSwitch = false;
        string? dir = null, axis = null;
        int count = 1, degrees = 0;
        var rest = new List<string>();
        foreach (var w in words)
        {
            if (w == "switch") isSwitch = true;
            else if (w is "left" or "right" or "front" or "back") dir = w;
            else if (Counts.TryGetValue(w, out var c)) count = c;
            else if (AxisWords.Contains(w)) axis = w;
            else if (w is "frontflip" or "backflip") axis = w;
            else if (w == "flip") axis = dir == "back" ? "backflip" : "frontflip";
            else if (int.TryParse(w, out var d) && d is >= 0 and <= CalledTrick.MaxDegrees) degrees = d;
            else if (w is not ("on" or "out" or "regular" or "lip")) rest.Add(w);
        }
        // "Front Flip" / "Back Flip": the direction is the flip's, not a spin.
        if (axis is "frontflip" or "backflip" && dir is "front" or "back") dir = null;
        if (kind == SegmentKind.Air && rest.Any(IsSlideWord)) kind = SegmentKind.Slide;
        return new Segment(kind, isSwitch, dir, axis, count, degrees, rest);
    }

    /// <summary>
    /// A trick that starts your run: any rail trick, or a jump with a rotation or at least 180 of spin.
    /// Straight airs, zero spins, butters and small spins on the way to the feature don't.
    /// </summary>
    public static bool IsReal(string name)
    {
        var parsed = Parse(name);
        if (parsed.Rail) return true;
        if (parsed.Segments.Count != 1 || parsed.Segments[0].Kind != SegmentKind.Air) return parsed.Segments.Count > 1;
        var s = parsed.Segments[0];
        return s.Axis is not null || s.Degrees >= 180;
    }

    static bool IsSlideWord(string w) => w.Contains("slide") || w is "5050" or "5-0" or "50-50" or "boardslide";

    /// <summary>A slide's kind, comparable however it's written ("50-50" = "5050").</summary>
    public static string SlideKey(IEnumerable<string> words) => SlideKey(string.Join("", words.Where(IsSlideWord)));
    public static string SlideKey(string text) => Regex.Replace(text.ToLowerInvariant(), "[^a-z0-9]", "") switch { "505" or "50" => "5-0", var s => s };
}
