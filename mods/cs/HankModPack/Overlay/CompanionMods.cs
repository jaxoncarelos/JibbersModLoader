using System.Text.Json.Nodes;
using Jibbers.Sdk;

namespace RickBackflip;

/// <summary>Liens entre les deux mods de la DLL (chacun a son menu repliable dans Options > Mods).</summary>
internal static class Shared
{
    public static RickBackflipMod? Main;
    public static RickBackflipEventsMod? Events;

    /// <summary>Dossier des fichiers : <c>JibbersMods\Hank Mod Pack\Overlay Files\&lt;un dossier par overlay&gt;\(gif ou vidéo + son)</c>.</summary>
    public static string PackDir => Path.Combine(Bootstrap.ModsDir, "Hank Mod Pack");
    public static string AssetsDir => Path.Combine(PackDir, "Overlay Files");

    /// <summary>Dossier des évènements Fun : <c>JibbersMods\Hank Mod Pack\Fun Overlay\1 … 5</c> (un dossier numéroté par évènement).</summary>
    public static string FunDir => Path.Combine(PackDir, "Fun Overlay");
    /// <summary>Sons des bannières du mod Overlay (ancien dossier « builtin »).</summary>
    public const string SoundsName = "Overlay Sounds";
    public static string SoundsDir => Path.Combine(AssetsDir, SoundsName);
    public const int FunSlots = 5;

    const string ReadmeText = """
        # Hank Mod Pack

        Nine features in one DLL (`Hank Mod Pack.dll`), each with its own entry in Options > Mods:

        - **Improved Spawn** is included in the pack. The quick menu opens its player list and spectator controls.
        - **Hank Mod Pack: Improved Replay Editor** - time of day, camera shake and an FX timeline in the replay editor.
        - **Hank Mod Pack: Overlay** - trick banners and sounds after a clean landing.
        - **Hank Mod Pack: Fun** - up to 5 events that show a GIF or video and play a sound when a trick is landed.
        - **Hank Mod Pack: HUD** - replaces the game HUD: spawnpoint, stance and respawn speed (bottom left), speed (bottom right); 11 themes.

        ## Folders

        ```
        Hank Mod Pack\
          README.md            this file
          Overlay Files\       one folder per overlay (GIF/video + sound), known_tricks.txt
            Overlay Sounds\    the banner sounds (created automatically)
          Fun Overlay\
            events.json        advanced events (several tricks, regex), created automatically
            1\ ... 5\          the files of Fun event 1 ... 5 (one .gif/.mp4/.webm/.mov + one .mp3/.wav each)
          Replay Editor Keyframes\  effect keyframes of each replay clip (Improved Replay Editor, one .fx.json per clip)
            Presets\<map>\    saved keyframe presets, one folder per map
        ```

        Choose the trick of each Fun event in Options > Mods > Hank Mod Pack: Fun. An event with an empty folder shows nothing.
        Restart the game after adding files so the lists refresh.
        """;

    static string OldDir => Path.Combine(Bootstrap.ModsDir, "rick_backflip");
    static bool prepared;
    static readonly string[] MediaExt = { ".gif", ".mp4", ".webm", ".mov" };
    static readonly string[] SoundExt = { ".mp3", ".wav" };

    /// <summary>
    /// Crée « Overlay Files » et, la première fois, y copie l'ancien dossier « rick_backflip » : chaque GIF/vidéo et son de même nom
    /// vont ensemble dans un dossier (rick.gif + rick.mp3 -> « rick »), events.json et known_tricks.txt suivent. Rien n'est supprimé.
    /// </summary>
    public static void Prepare()
    {
        if (prepared) return;
        prepared = true;
        try
        {
            Directory.CreateDirectory(PackDir);
            var readme = Path.Combine(PackDir, "README.md");
            if (!File.Exists(readme)) try { File.WriteAllText(readme, ReadmeText); } catch { }
            foreach (var name in new[] { "Overlay Files", "Fun Overlay" }) // anciens emplacements, directement dans JibbersMods
            {
                var from = Path.Combine(Bootstrap.ModsDir, name);
                var to = Path.Combine(PackDir, name);
                if (Directory.Exists(from) && !Directory.Exists(to))
                    try { Directory.Move(from, to); } catch { /* sinon les dossiers sont recréés vides */ }
            }
            var fresh = !Directory.Exists(AssetsDir);
            Directory.CreateDirectory(AssetsDir);
            var oldSounds = Path.Combine(AssetsDir, "builtin");
            if (Directory.Exists(oldSounds) && !Directory.Exists(SoundsDir))
                try { Directory.Move(oldSounds, SoundsDir); } catch { /* sinon Sfx recrée les fichiers */ }
            var funFresh = !Directory.Exists(FunDir);
            Directory.CreateDirectory(FunDir);
            var oldEvents = Path.Combine(AssetsDir, "events.json");           // events.json now lives in Fun Overlay
            var newEvents = Path.Combine(FunDir, "events.json");
            if (File.Exists(oldEvents) && !File.Exists(newEvents))
                try { File.Move(oldEvents, newEvents); } catch { }
            for (var n = 1; n <= FunSlots; n++) Directory.CreateDirectory(Path.Combine(FunDir, n.ToString()));
            // Events 1-3 ship with a default GIF + sound (only when their folder has no media / sound yet).
            try
            {
                var asm = typeof(Shared).Assembly;
                foreach (var (slot, res, name) in new[] { (1, "rick", "Never Gonna Give You Up"), (2, "heli", "Helicopter"), (3, "triple", "Triple") })
                {
                    var dir = Path.Combine(FunDir, slot.ToString());
                    var (media, sound) = ResolveIn(dir, dir);
                    foreach (var (ext, have) in new[] { (".gif", media), (".mp3", sound) })
                    {
                        if (have is not null) continue;
                        using var src = asm.GetManifestResourceStream("Default." + res + ext);
                        if (src is null) continue;
                        using var dst = File.Create(Path.Combine(dir, name + ext));
                        src.CopyTo(dst);
                    }
                }
            }
            catch { }
            if (funFresh) // l'ancien évènement 1 utilisait l'overlay « rick » : on le recopie dans le dossier 1
            {
                var rick = Path.Combine(AssetsDir, "rick");
                if (Directory.Exists(rick))
                    foreach (var f in Directory.EnumerateFiles(rick))
                        try { File.Copy(f, Path.Combine(FunDir, "1", Path.GetFileName(f)), false); } catch { }
            }
            if (!fresh || !Directory.Exists(OldDir)) return;
            foreach (var file in Directory.EnumerateFiles(OldDir))
            {
                var name = Path.GetFileName(file);
                var ext = Path.GetExtension(file).ToLowerInvariant();
                var target = MediaExt.Contains(ext) || SoundExt.Contains(ext)
                    ? Path.Combine(AssetsDir, Path.GetFileNameWithoutExtension(file), name)
                    : name is "events.json" or "known_tricks.txt" ? Path.Combine(AssetsDir, name) : null;
                if (target is null) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target, overwrite: false);
            }
        }
        catch { /* la copie est un confort : sans elle, il suffit de ranger les fichiers à la main */ }
    }

    /// <summary>Les overlays : un dossier par overlay dans « Overlay Files » (« Overlay Sounds » = sons des bannières, ignoré).</summary>
    public static IEnumerable<string> OverlayFolders()
    {
        Prepare();
        try
        {
            return Directory.EnumerateDirectories(AssetsDir).Select(Path.GetFileName).OfType<string>()
                .Where(n => !n.Equals(SoundsName, StringComparison.OrdinalIgnoreCase) && !n.StartsWith('_'))
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch { return Array.Empty<string>(); }
    }

    /// <summary>Le GIF/vidéo et le son d'un overlay (chemins relatifs à « Overlay Files »), pris dans son dossier.</summary>
    public static (string? Media, string? Sound) Resolve(string overlay) => ResolveIn(Path.Combine(AssetsDir, overlay), overlay);

    /// <summary>Le GIF/vidéo et le son de l'évènement Fun n (chemins complets), pris dans <c>Fun Overlay\n</c>.</summary>
    public static (string? Media, string? Sound) ResolveFun(int n)
    {
        Prepare();
        return ResolveIn(Path.Combine(FunDir, n.ToString()), Path.Combine(FunDir, n.ToString()));
    }

    static (string? Media, string? Sound) ResolveIn(string dir, string prefix)
    {
        try
        {
            if (!Directory.Exists(dir)) return (null, null);
            var files = Directory.EnumerateFiles(dir).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
            string? Find(string[] exts) => files.FirstOrDefault(f => exts.Contains(Path.GetExtension(f).ToLowerInvariant())) is { } f ? Path.Combine(prefix, Path.GetFileName(f)) : null;
            return (Find(MediaExt), Find(SoundExt));
        }
        catch { return (null, null); }
    }

    static LearnedTricks? learned;
    /// <summary>Les tricks déjà posés (known_tricks.txt), partagés entre le menu et la détection.</summary>
    public static LearnedTricks Learned
    {
        get
        {
            Prepare();
            return learned ??= new LearnedTricks(Path.Combine(AssetsDir, "known_tricks.txt"));
        }
    }
}

/// <summary>
/// Sous-menu « Fun » (GIF, vidéos et sons) : 5 évènements (dossiers « Fun Overlay\1 » à « 5 »), chacun avec un trick, un GIF ou une vidéo, et un son.
/// Le menu des mods n'affiche pas les titres de section : chaque ligne porte donc le numéro de son évènement.
/// </summary>
public sealed class RickBackflipEventsMod : HankModPack.PackPart
{
    internal const string None = "(none)";
    const int SlotCount = Shared.FunSlots;
    protected override string PartId => "rick_backflip_events";
    public override string Name => "Hank Mod Pack: Fun";
    public override string Description => "Up to 5 events: each shows the GIF or video (and plays the sound) found in its numbered folder inside \"Fun Overlay\". The folders are created automatically in JibbersMods.";

    public RickBackflipEventsMod() { Shared.Events = this; }

    protected override void OnDeactivated() => Shared.Main?.StopFun();

    public override void OnLoad()
    {
        ImportPackSettings();
        // Settings saved by an older version still hold the old defaults of events 2 and 3 (Frontflip, Cork): move them to the new ones.
        // A trick the player chose on purpose is left alone.
        try
        {
            if (GetString("e2_trick") == "[Any] Frontflip (Dub, Trip...)") SetSetting("e2_trick", System.Text.Json.Nodes.JsonValue.Create(TrickCatalog.Rotation1620)!);
            if (GetString("e3_trick") == "[Any] Cork (all counts)") SetSetting("e3_trick", System.Text.Json.Nodes.JsonValue.Create(TrickCatalog.TripleAny)!);
        }
        catch (Exception e) { Log("Could not update the default tricks: " + e.Message); }
    }

    IReadOnlyList<Setting>? schema;
    [Slider("GIF cooldown (s)", 3, 0, 30, 1, Key = "cooldown", Section = "GIF", Tooltip = "Minimum time between two GIF / video overlays.")]
    public float CooldownSeconds { get; private set; }

    [Slider("GIF size", 320, 120, 900, 10, Key = "size", Section = "GIF", Tooltip = "Height of the GIF or video, in pixels at 1080p.")]
    public float SizePx { get; private set; }

    [Choice("GIF position", "All 4 corners", "All 4 corners", "Random corner", "Bottom right", "Bottom left", "Top right", "Top left", "Center", Key = "position", Section = "GIF",
        Tooltip = "All 4 corners: the GIF shows in the 4 corners of the screen at once. Random corner: one corner, a different one each time. Videos use one corner.")]
    public string Position { get; private set; } = "All 4 corners";

    [Slider("GIF max duration (s)", 12, 3, 60, 1, Key = "max_seconds", Section = "GIF", Tooltip = "The overlay ends with the sound, or after this time.")]
    public float MaxSeconds { get; private set; }

    public override IEnumerable<Setting> Settings => schema ??= base.Settings.Concat(Build()).ToList();

    IEnumerable<Setting> Build()
    {
        var tricks = TrickCatalog.Options(Shared.Learned.Names);

        // Évènements préremplis : 1 backflip, 2 frontflip, 3 cork, 4 misty, 5 bio (tous activés ; sans fichier dans le dossier, rien ne s'affiche).
        string[] defaults = { TrickCatalog.BackflipBoth, TrickCatalog.Rotation1620, TrickCatalog.TripleAny, "[Any] Misty (all counts)", "[Any] Bio (all counts)" };
        for (var n = 1; n <= SlotCount; n++)
        {
            var first = true;
            var section = "Event " + n;
            yield return Setting.Toggle($"e{n}_on", $"Event {n}: enabled", first).InSection(section);
            yield return Setting.Choice($"e{n}_trick", $"Event {n}: trick", tricks, defaults[n - 1])
                .InSection(section).WithTooltip("Which trick triggers this event. Tricks you land are added to the list at the next launch.");
        }
    }

    /// <summary>Les règles des évènements activés, relues depuis les réglages à chaque trick.</summary>
    internal List<EventRule> SlotRules(bool ignoreGrabs)
    {
        var list = new List<EventRule>();
        if (!Enabled) return list;
        for (var n = 1; n <= SlotCount; n++)
        {
            if (!GetBool($"e{n}_on")) continue;
            var rule = TrickCatalog.RuleFor(GetString($"e{n}_trick"), ignoreGrabs);
            if (rule is null) continue;
            rule.Name = $"Event {n} ({rule.Name})";
            (rule.Gif, rule.Sound) = Shared.ResolveFun(n);
            list.Add(rule);
        }
        return list;
    }
}
