using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace RickBackflip;

/// <summary>Un évènement : quand un de ces tricks est posé, on affiche ce GIF et on joue ce son.</summary>
internal sealed class EventRule
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;

    /// <summary>Noms de tricks EXACTS (majuscules, espaces et tirets ignorés) : "Backflip" ne valide pas "Dub Backflip".</summary>
    [JsonPropertyName("tricks")] public List<string> Tricks { get; set; } = new();

    /// <summary>Option avancée : expression régulière testée sur le nom complet du trick.</summary>
    [JsonPropertyName("regex")] public string? Regex { get; set; }

    /// <summary>Si vrai, « Left 360 » valide aussi « Left 360 Hank Japan » (le trick avec un grab).</summary>
    [JsonPropertyName("ignore_grabs")] public bool IgnoreGrabs { get; set; }

    /// <summary>Un GIF (.gif) ou une vidéo (.mp4, .webm, .mov).</summary>
    [JsonPropertyName("gif")] public string? Gif { get; set; }
    [JsonPropertyName("sound")] public string? Sound { get; set; }

    // Réglages propres à l'évènement (sinon ceux de l'onglet Mods).
    [JsonPropertyName("size")] public float? Size { get; set; }
    [JsonPropertyName("position")] public string? Position { get; set; }
    [JsonPropertyName("volume")] public float? Volume { get; set; }

    HashSet<string> exact = new();
    Regex? pattern;

    /// <summary>Calcule les formes comparées. À appeler après le chargement du fichier.</summary>
    public void Prepare(Action<string> log)
    {
        exact = Tricks.Where(t => !string.IsNullOrWhiteSpace(t)).Select(Normalize).ToHashSet();
        pattern = null;
        if (!string.IsNullOrWhiteSpace(Regex))
        {
            try { pattern = new Regex(Regex, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50)); }
            catch (ArgumentException e) { log($"Event \"{Name}\": invalid regex ({e.Message}), ignored."); }
        }
    }

    public bool Matches(string trickName)
    {
        if (!Enabled) return false;
        if (exact.Contains(Normalize(trickName))) return true;
        if (IgnoreGrabs && exact.Contains(Normalize(TrickCatalog.Core(trickName)))) return true;
        try { return pattern?.IsMatch(trickName) == true; }
        catch (RegexMatchTimeoutException) { return false; }
    }

    /// <summary>« Switch Back-Flip » et « switchbackflip » sont le même nom.</summary>
    public static string Normalize(string name) =>
        name.Trim().ToLowerInvariant().Replace(" ", "").Replace("-", "").Replace("_", "");
}

internal sealed class EventConfig
{
    [JsonPropertyName("events")] public List<EventRule> Events { get; set; } = new();

    static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip, // les // du fichier sont permis
        AllowTrailingCommas = true,
    };

    public static EventConfig Parse(string json, Action<string> log)
    {
        var config = JsonSerializer.Deserialize<EventConfig>(json, Options) ?? new EventConfig();
        foreach (var rule in config.Events) rule.Prepare(log);
        return config;
    }

    /// <summary>
    /// Le fichier créé au premier lancement. Les évènements courants se règlent dans Options > Mods ;
    /// celui-ci sert aux cas avancés (plusieurs tricks d'un coup, expressions régulières).
    /// </summary>
    public const string DefaultJson = """
        {
          // Évènements avancés. Les évènements normaux se règlent dans Options > Mods > Rick Backflip.
          // Quand l'un des "tricks" est posé (sans crash), le GIF ou la vidéo s'affiche et le son joue.
          // Les noms doivent être EXACTS, tels que le jeu les écrit (jibbers_hotload.log les liste à chaque atterrissage).
          // Majuscules, espaces et tirets sont ignorés : "Back Flip" = "backflip".
          // Les fichiers sont cherchés dans ce dossier : GIF ou vidéo (mp4, webm) pour l'image, mp3 ou wav pour le son.
          // Ce fichier est relu à chaque trick : pas besoin de relancer le jeu.
          "events": [
            {
              // Exemple désactivé. Mets "enabled": true, ton GIF/son, et le nom du trick.
              // Options facultatives : "size" (hauteur en px), "position" ("Top left", "Center"...), "volume" (0-100),
              // "ignore_grabs" (true : "Left 360" valide aussi "Left 360 Hank Japan"),
              // "regex" (expression régulière sur le nom complet).
              "name": "Exemple",
              "enabled": false,
              "tricks": ["Left 360"],
              "gif": "exemple.gif",
              "sound": "exemple.mp3"
            }
          ]
        }
        """;
}
