namespace RickBackflip;

/// <summary>
/// Les tricks proposés dans les options : des groupes pratiques, les tricks que le jeu sait nommer
/// (stance, direction, rotation, nombre de tours) et ceux que tu as déjà posés (appris automatiquement).
/// Les noms suivent ceux du jeu : « Switch Left Dub Cork 1080 », « Dub Backflip », « Left 360 »...
/// </summary>
internal static class TrickCatalog
{
    public const string NotSet = "(not set)";
    public const string BackflipBoth = "[Both] Backflip + Switch Backflip";
    public const string Rotation1620 = "[Any] Rotation 1620 or more";
    public const string TripleAny = "[Any] Triple (any trick)";

    /// <summary>Groupes : un nom affiché, des noms exacts et/ou une expression régulière sur le nom du trick.</summary>
    static readonly (string Label, string[] Tricks, string? Regex)[] Groups = BuildGroups();

    static (string Label, string[] Tricks, string? Regex)[] BuildGroups()
    {
        var none = Array.Empty<string>();
        var list = new List<(string, string[], string?)>
        {
            (BackflipBoth, new[] { "Backflip", "Switch Backflip" }, null),
            ("[Any] Backflip (Dub, Trip...)", none, @"back\s?flip"),
            ("[Any] Frontflip (Dub, Trip...)", none, @"front\s?flip"),
            ("[Any] Cork (all counts)", none, @"\bcork\b"),
            ("[Any] Misty (all counts)", none, @"\bmisty\b"),
            ("[Any] Bio (all counts)", none, @"\bbio\b"),
            ("[Any] Lincoln (all counts)", none, @"\blincoln\b"),
            (Rotation1620, none, @"\b(1[6-9]\d\d|[2-9]\d{3})\b"),
            (TripleAny, none, @"\btrip(le)?\b"),
            ("[Any] Switch trick", none, @"^switch\b"),
            ("[Any] landed trick", none, @"."),
        };

        // Familles : ignorent le stance (Switch), la direction (Left/Right), les degres et les grabs.
        // « [Any] Trip Cork » = « Trip Cork 1080 », « Switch Left Trip Cork 1260 », « Right Trip Cork 900 Mute »...
        const string prefix = @"^(switch\s+)?(left\s+|right\s+)?";
        var counts = new (string Label, string Rx)[]
        {
            ("Single", ""), ("Dub", @"dub(le)?\s+"), ("Trip", @"trip(le)?\s+"), ("Quad", @"quad(ruple)?\s+"),
        };
        foreach (var word in new[] { "Cork", "Misty", "Bio", "Lincoln", "Backflip", "Frontflip" })
        {
            var w = word.EndsWith("flip") ? word[..^4].ToLowerInvariant() + @"\s?flip" : word.ToLowerInvariant();
            foreach (var (label, rx) in counts)
                list.Add(($"[Any] {label} {word}", none, $@"{prefix}{rx}{w}\b"));
        }
        foreach (var deg in new[] { 180, 360, 540, 720, 900, 1080, 1260 })
            list.Add(($"[Any] Flat {deg} (Left/Right/Switch)", none, $@"^(switch\s+)?(left|right)\s+{deg}\b"));
        return list.ToArray();
    }

    // Mots d'un trick de saut qui précèdent les grabs : « Left 360 » + « Hank Japan ».
    static readonly HashSet<string> CoreWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "switch", "left", "right", "front", "back", "cork", "misty", "bio", "lincoln",
        "single", "dub", "double", "trip", "triple", "quad", "flip", "frontflip", "backflip",
    };

    /// <summary>
    /// Le trick sans ses grabs : « Left 360 Hank Japan » devient « Left 360 ». Les noms que l'on ne sait pas
    /// découper (rails, « Straight Air »...) restent tels quels.
    /// </summary>
    public static string Core(string name)
    {
        if (name.Contains(" - ")) return name; // trick de rail
        var words = name.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var count = 0;
        var meaningful = false; // un nombre de degrés ou un flip
        while (count < words.Length)
        {
            var w = words[count];
            if (int.TryParse(w, out _)) meaningful = true;
            else if (CoreWords.Contains(w))
            {
                if (w.Contains("flip", StringComparison.OrdinalIgnoreCase)) meaningful = true;
            }
            else break;
            count++;
        }
        return count == 0 || count == words.Length || !meaningful ? name : string.Join(' ', words.Take(count));
    }

    /// <summary>
    /// Nom réduit pour les bannières : sans grabs ni direction (Left/Right), « Dub » devient « Double », « Trip » « Triple ».
    /// « Switch Left Trip Cork 1080 Hank Japan » -> « Switch Triple Cork 1080 ». Les rails gardent leur nom complet.
    /// </summary>
    public static string ShortName(string name)
    {
        if (name.Contains(" - ")) return name;
        var words = Core(name).Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(w => !w.Equals("left", StringComparison.OrdinalIgnoreCase) && !w.Equals("right", StringComparison.OrdinalIgnoreCase))
            .Select(w => w.ToLowerInvariant() switch
            {
                "dub" => "Double",
                "trip" => "Triple",
                "quad" => "Quad",
                _ => w,
            });
        var result = string.Join(' ', words);
        // « Left 360 » garde sa direction : « 360 » tout seul ne dirait rien (même chose pour « Switch Left 360 »).
        if (result.Split(' ').All(w => w.Equals("switch", StringComparison.OrdinalIgnoreCase) || int.TryParse(w, out _))) return Core(name);
        return result.Length > 0 ? result : name;
    }

    /// <summary>Le contenu des listes déroulantes : groupes, tricks connus du jeu, puis ceux déjà posés.</summary>
    public static string[] Options(IEnumerable<string> learned)
    {
        var list = new List<string> { NotSet };
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { EventRule.Normalize(NotSet) };
        foreach (var (label, _, _) in Groups)
        {
            list.Add(label);
            seen.Add(EventRule.Normalize(label));
        }
        foreach (var name in Known().Concat(learned.OrderBy(n => n, StringComparer.OrdinalIgnoreCase)))
        {
            if (list.Count >= 400) break;
            if (seen.Add(EventRule.Normalize(name))) list.Add(name);
        }
        return list.ToArray();
    }

    static IEnumerable<string> Known()
    {
        string[] stances = { "", "Switch " };
        string[] directions = { "Left", "Right" };

        foreach (var stance in stances)
            foreach (var flip in new[] { "Backflip", "Frontflip" })
                foreach (var count in new[] { "", "Dub ", "Trip ", "Quad " })
                    yield return $"{stance}{count}{flip}";

        foreach (var stance in stances)
            foreach (var dir in directions)
                foreach (var degrees in new[] { 180, 360, 540, 720, 900, 1080, 1260 })
                    yield return $"{stance}{dir} {degrees}";

        foreach (var stance in stances)
            foreach (var dir in directions)
                foreach (var axis in new[] { "Cork", "Misty", "Bio", "Lincoln" })
                    foreach (var degrees in new[] { 180, 360, 540, 720, 900, 1080 })
                        yield return $"{stance}{dir} {axis} {degrees}";
    }

    /// <summary>La règle de matching d'une entrée de liste (groupe ou trick exact), ou null si « (not set) ».</summary>
    public static EventRule? RuleFor(string entry, bool ignoreGrabs)
    {
        if (string.IsNullOrWhiteSpace(entry) || entry == NotSet) return null;
        var rule = new EventRule { Name = entry, IgnoreGrabs = ignoreGrabs };
        var group = Groups.FirstOrDefault(g => g.Label == entry);
        if (group.Label is not null)
        {
            rule.Tricks = group.Tricks.ToList();
            rule.Regex = group.Regex;
        }
        else
        {
            rule.Tricks.Add(entry);
        }
        rule.Prepare(_ => { });
        return rule;
    }
}

/// <summary>Les tricks déjà posés, gardés dans known_tricks.txt pour apparaître dans les listes au prochain lancement.</summary>
internal sealed class LearnedTricks
{
    readonly string path;
    readonly object gate = new();
    readonly List<string> names = new();
    readonly HashSet<string> keys = new(StringComparer.OrdinalIgnoreCase);

    public LearnedTricks(string path)
    {
        this.path = path;
        try
        {
            if (File.Exists(path))
            {
                foreach (var line in File.ReadAllLines(path))
                {
                    var name = line.Trim();
                    if (name.Length > 0 && keys.Add(EventRule.Normalize(name))) names.Add(name);
                }
            }
        }
        catch { /* une liste illisible n'est pas grave */ }
    }

    public IReadOnlyList<string> Names
    {
        get { lock (gate) return names.ToArray(); }
    }

    /// <summary>Retient un trick (sans ses grabs). Renvoie vrai s'il était nouveau.</summary>
    public bool Add(string name)
    {
        lock (gate)
        {
            if (names.Count >= 300 || !keys.Add(EventRule.Normalize(name))) return false;
            names.Add(name);
            try { File.AppendAllText(path, name + Environment.NewLine); }
            catch { /* tant pis : il sera réappris */ }
            return true;
        }
    }
}
