using Jibbers.Sdk.Interop;
using System.Collections.Concurrent;
using System.Diagnostics;
using Jibbers.Sdk;
using Jibbers.Sdk.Game.Env;
using Jibbers.Sdk.Game.Event;

namespace RickBackflip;

/// <summary>
/// Quand un trick de la liste est posé sans crash, affiche un GIF au-dessus du jeu et joue un son.
/// Les évènements (quels tricks, quel GIF, quel son) sont dans JibbersMods\Hank Mod Pack\Fun Overlay\events.json.
/// Par défaut : Backflip et Switch Backflip, rien d'autre. Aucun média n'est livré avec le mod.
/// </summary>
public sealed class RickBackflipMod : HankModPack.PackPart
{
    protected override string[] SectionOrder => new[] { "Banners", "Triggers", "Sound", "Advanced" };
    public RickBackflipMod() { Shared.Main = this; }

    protected override string PartId => "rick_backflip";
    public override string Name => "Hank Mod Pack: Overlay";
    public override string Author => "";
    public override string Description => "Trick banners (name + INSANE!, SICK!...) after a clean landing, plus GIFs, videos and sounds set in the Fun menu.";

    // ---------------------------------------------------------------- Réglages (valeurs par défaut de tous les évènements)

    [Slider("Wait after landing (s)", 0.6, 0, 3, 0.1, Key = "settle", Section = "Triggers",
        Tooltip = "The trick only counts if you don't crash or reset during this delay after landing.")]
    public float SettleSeconds { get; private set; }

    // Réglages des GIF / vidéos : dans le menu « Fun » (CompanionMods.cs).
    float CooldownSeconds => Shared.Events?.CooldownSeconds ?? 3;
    float SizePx => Shared.Events?.SizePx ?? 320;
    string Position => Shared.Events?.Position ?? "All 4 corners";
    float MaxSeconds => Shared.Events?.MaxSeconds ?? 12;

    [Slider("Volume", 70, 0, 100, 1, Key = "volume", Section = "Sound")]
    public float Volume { get; private set; }

    [Toggle("Trick banners", true, Key = "banner_on", Section = "Banners",
        Tooltip = "Shows the name of every trick you land, with a sound, in an arcade style.")]
    public bool BannerOn { get; private set; }

    [Toggle("Banner sound", true, Key = "banner_sound", Section = "Banners", Tooltip = "Three sounds, from simple to huge tricks.")]
    public bool BannerSound { get; private set; }

    [Choice("Trick name", "Short", "Short", "Full", Key = "banner_names", Section = "Banners",
        Tooltip = "Short: 'Switch Triple Cork 1080' (no Left/Right, no grabs). Full: exactly as the game writes it.")]
    public string BannerNames { get; private set; } = "Short";

    [Slider("Banner size (%)", 80, 40, 200, 10, Key = "banner_size", Section = "Banners",
        Tooltip = "Shrinks automatically when there is no free spot beside the rider.")]
    public float BannerSizePct { get; private set; }

    [Choice("Banner position", "Random (avoids rider)", "Random (avoids rider)", "Top center", "Center", "Bottom center", "Top left",
        Key = "banner_pos", Section = "Banners", Tooltip = "Random: a different spot each time, never over the rider.")]
    public string BannerPosition { get; private set; } = "Random (avoids rider)";

    [Toggle("Hide the game's trick display", true, Key = "hide_game_tricks", Section = "Banners",
        Tooltip = "Hides the game's own trick list so that only the banners are shown.")]
    public bool HideGameTricks { get; private set; }

    [Toggle("Ignore grabs", true, Key = "ignore_grabs", Section = "Triggers",
        Tooltip = "On: 'Left 360' also counts when you add a grab ('Left 360 Hank Japan').")]
    public bool IgnoreGrabs { get; private set; }

    [Toggle("Also use events.json", true, Key = "use_file_events", Section = "Advanced",
        Tooltip = "Advanced events (several tricks, regex) from JibbersMods\\Hank Mod Pack\\Fun Overlay\\events.json.")]
    public bool UseFileEvents { get; private set; }

    [Toggle("Log trick names", true, Key = "log_tricks", Section = "Advanced",
        Tooltip = "Writes every landed trick name in jibbers_hotload.log, to help you fill events.json.")]
    public bool LogTricks { get; private set; }

    [KeyBinding("Open media folder", (Key)0, Key = "folder_key", Section = "Controls",
        Tooltip = "Opens the Overlay Files folder: one folder per overlay, holding its GIF or video and its sound. Restart the game to refresh the lists.")]
    public Key FolderKey { get; private set; }

    [KeyBinding("Test overlay", (Key)0, Key = "test_key", Section = "Controls", Tooltip = "Plays the first enabled event right now, to check your files.")]
    public Key TestKey { get; private set; }

    // ---------------------------------------------------------------- Évènements GIF / sons : sous-menu « Rick Backflip - GIF & sounds »

    List<EventRule> SlotRules() => Shared.Events?.SlotRules(IgnoreGrabs) ?? new List<EventRule>();

    IEnumerable<EventRule> AllRules() => UseFileEvents ? SlotRules().Concat(rules) : SlotRules();

    /// <summary>Un overlay vient d'être choisi dans les options : on charge son GIF et son son à l'avance.</summary>
    internal void Preload(string? media, string? sound)
    {
        try
        {
            if (media is not null && !IsVideo(media)) GetGif(media);
            if (sound is not null && FindFile(sound) is { } p) audio.Preload(p);
        }
        catch (Exception e) { Log("Setting: " + e.Message); }
    }

    static bool IsVideo(string? name) =>
        name is not null && Path.GetExtension(name).ToLowerInvariant() is ".mp4" or ".webm" or ".mov";

    // ---------------------------------------------------------------- État

    readonly GifOverlay overlay = new();
    readonly GifOverlay[] extraOverlays = { new(), new(), new() }; // les 3 autres coins quand le GIF est affiché aux 4 coins
    bool extrasShown;
    readonly VideoOverlay video = new();
    readonly GifOverlay bannerOverlay = new();
    readonly ConcurrentDictionary<string, Task<GifAnimation?>> banners = new(StringComparer.OrdinalIgnoreCase);
    (string Name, int Seq, long Due, bool EventMatched)? bannerPending;
    int lastBannerSeq = int.MinValue;
    GifAnimation? shownBanner;
    readonly TextBanner textBanner = new();
    bool shownText;
    long textStart;
    nint textFont;
    bool textBannerBroken; // la police du jeu n'a pas pu servir : on dessine nous-mêmes
    long bannerStart;
    readonly AudioPlayer audio = new();

    // Évènements (events.json), rechargés quand le fichier change.
    volatile EventRule[] rules = Array.Empty<EventRule>();
    DateTime rulesStamp;

    // GIF décodés en arrière-plan, un par fichier.
    sealed class GifEntry
    {
        public volatile GifAnimation? Gif;
        public DateTime Stamp;
        public volatile bool Loading;
    }
    readonly ConcurrentDictionary<string, GifEntry> gifs = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> warned = new();

    // Trick posé, en attente de la fin du délai « sans crash ».
    (EventRule Rule, string Trick, long Due)? pending;
    int handledSequence = int.MinValue;

    // Overlay en cours.
    GifAnimation? shownGif;
    bool shownVideo;
    bool shownHasSound;
    long startedAt;
    double durationSeconds;
    long lastPlayed;

    // ---------------------------------------------------------------- Cycle de vie

    string AssetsDir => Shared.AssetsDir;
    string ConfigPath => Path.Combine(Shared.FunDir, "events.json");

    public override void OnLoad()
    {
        ImportPackSettings();
        try { Shared.Prepare(); }
        catch (Exception e) { Log("Cannot create " + AssetsDir + ": " + e.Message); }

        ReloadRulesIfChanged();
        try { Sfx.EnsureFiles(Shared.SoundsDir); }
        catch (Exception e) { Log("Cannot create banner sounds: " + e.Message); }
        foreach (var r in SlotRules()) { if (!IsVideo(r.Gif)) GetGif(r.Gif); if (FindFile(r.Sound) is { } sp) audio.Preload(sp); }

        TrickPerformed.Subscribe(OnTrick);
        // Un reset ou un respawn juste après l'atterrissage = c'était un crash : on annule.
        Reset.Subscribe(_ => { pending = null; bannerPending = null; });
        Respawn.Subscribe(_ => { pending = null; bannerPending = null; });
        ChangeScene.Subscribe(_ => { OnDeactivated(); textFont = 0; });
        Input.OnPressed(() => TestKey, () =>
        {
            ReloadRulesIfChanged();
            if (AllRules().FirstOrDefault(r => r.Enabled) is { } first) Play(first, "test key", force: true);
            else Log("No enabled event: enable one in Options > Mods.");
        });
        Input.OnPressed(() => FolderKey, () =>
        {
            try { Process.Start(new ProcessStartInfo("explorer.exe", "\"" + AssetsDir + "\"") { UseShellExecute = true }); }
            catch (Exception e) { Log("Cannot open " + AssetsDir + ": " + e.Message); }
        });
        Manager.UpdateMethod.Before((_, _) => Frame());

        Log($"Ready: {AllRules().Count(r => r.Enabled)} event(s). Media folder: {AssetsDir}");
    }

    readonly HankModPack.UiVisibility originalTricks = new();
    readonly HankModPack.ObjectRoots trickRoots = new();

    protected override void OnDeactivated()
    {
        pending = null; bannerPending = null;
        Cleanup(StopOverlay, StopBanner, originalTricks.Dispose, trickRoots.Dispose);
        gameTrickLists = Array.Empty<nint>();
        hideCountdown = 0;
    }

    protected override void OnPackSettingChanged(string key, System.Text.Json.Nodes.JsonNode value)
    {
        if (key is "hide_game_tricks" or "banner_on")
            MainThread.Later(() => { if (!HideGameTricks || !BannerOn) originalTricks.Dispose(); });
    }

    protected override void OnPackUnload()
    {
        try
        {
            audio.Stop();
            if (MainThread.IsCurrent) { overlay.Dispose(); video.Dispose(); bannerOverlay.Dispose(); textBanner.Dispose(); foreach (var x in extraOverlays) x.Dispose(); }
            else MainThread.Run(() => { overlay.Dispose(); video.Dispose(); bannerOverlay.Dispose(); textBanner.Dispose(); foreach (var x in extraOverlays) x.Dispose(); });
        }
        catch (Exception e)
        {
            Log("Unload: " + e.Message);
        }
    }

    // ---------------------------------------------------------------- Évènements (events.json)

    /// <summary>Crée le fichier s'il manque, le relit s'il a changé. En cas d'erreur de syntaxe, garde l'ancienne liste.</summary>
    void ReloadRulesIfChanged()
    {
        try
        {
            if (!File.Exists(ConfigPath))
            {
                File.WriteAllText(ConfigPath, EventConfig.DefaultJson);
                Log("Created " + ConfigPath);
            }
            var stamp = File.GetLastWriteTimeUtc(ConfigPath);
            if (stamp == rulesStamp) return;
            rulesStamp = stamp; // même en cas d'erreur : on ne relit pas en boucle un fichier cassé

            var config = EventConfig.Parse(File.ReadAllText(ConfigPath), Log);
            rules = config.Events.ToArray();
            warned.Clear();
            foreach (var rule in rules.Where(r => r.Enabled))
            {
                GetGif(rule.Gif); // décodage en arrière-plan dès maintenant
                if (FindFile(rule.Sound) is { } soundPath) audio.Preload(soundPath);
                Log($"Event \"{rule.Name}\": {string.Join(", ", rule.Tricks)}");
            }
        }
        catch (Exception e)
        {
            Log($"Cannot read {ConfigPath}: {e.Message} (keeping the previous events)");
        }
    }

    // ---------------------------------------------------------------- Détection

    void OnTrick(TrickPerformed t)
    {
        var name = (t.Name ?? "").Trim();
        if (name.Length == 0) return;
        if (LogTricks && !t.IsUpdate) Log($"Trick: \"{name}\"" + (string.IsNullOrWhiteSpace(t.Details) ? "" : $" ({t.Details})"));

        ReloadRulesIfChanged();
        if (name.EndsWith(" On", StringComparison.OrdinalIgnoreCase)) return; // encore sur un rail : pas posé
        if (!t.IsUpdate) { try { Shared.Learned.Add(TrickCatalog.Core(name)); } catch { /* sans importance */ } }
        var rule = AllRules().FirstOrDefault(r => r.Matches(name));
        QueueBanner(name, t, rule is not null && (!string.IsNullOrWhiteSpace(rule.Gif) || !string.IsNullOrWhiteSpace(rule.Sound))); // an event with no file does not mute the banner sound
        if (rule is null) return;
        if (t.IsUpdate && t.SequenceId == handledSequence) return; // mises à jour d'un combo déjà compté

        handledSequence = t.SequenceId;
        // Le jeu ne signale un trick qu'à l'atterrissage (un bail n'envoie rien).
        // On attend encore un peu : un reset/respawn pendant ce délai annule tout.
        pending = (rule, name, Stopwatch.GetTimestamp() + (long)(SettleSeconds * Stopwatch.Frequency));
        Log($"Matched \"{rule.Name}\" with \"{name}\"" + (SettleSeconds > 0 ? $", checking for a crash ({SettleSeconds:0.0}s)..." : ""));
    }

    // ---------------------------------------------------------------- Chaque frame (thread principal)

    void Frame()
    {
        try
        {
            var now = Stopwatch.GetTimestamp();

            if (pending is { } p && now >= p.Due)
            {
                pending = null;
                Play(p.Rule, p.Trick, force: false);
            }

            HideGameTrickList();
            UpdateBanner(now);

            if (shownVideo)
            {
                var ms = (long)Stopwatch.GetElapsedTime(startedAt, now).TotalMilliseconds;
                var over = ms >= durationSeconds * 1000;
                if (!over && video.LengthSeconds > 0) over = ms >= Math.Min(MaxSeconds, video.LengthSeconds + 0.5) * 1000;
                if (over || !video.Update(ms)) StopOverlay();
                return;
            }

            if (shownGif is not { } g) return;
            var elapsedMs = (long)Stopwatch.GetElapsedTime(startedAt, now).TotalMilliseconds;
            var soundEnded = shownHasSound && !audio.Playing && elapsedMs > 800;
            if (elapsedMs >= durationSeconds * 1000 || soundEnded)
            {
                StopOverlay();
                return;
            }
            var frame = g.FrameAt(elapsedMs);
            overlay.Draw(g, frame);
            if (extrasShown) foreach (var extra in extraOverlays) extra.Draw(g, frame);
        }
        catch (Exception e)
        {
            Log("Overlay error: " + e.Message);
            StopOverlay();
        }
    }

    // ---------------------------------------------------------------- Affichage + son

    void Play(EventRule rule, string reason, bool force)
    {
        var now = Stopwatch.GetTimestamp();
        if (!force)
        {
            if (shownGif is not null || shownVideo || audio.Playing) return;
            if (lastPlayed != 0 && Stopwatch.GetElapsedTime(lastPlayed, now).TotalSeconds < CooldownSeconds) return;
        }

        var isVideo = IsVideo(rule.Gif);
        var videoPath = isVideo ? FindFile(rule.Gif) : null;
        var g = isVideo ? null : GetGif(rule.Gif);
        var sound = isVideo ? null : FindFile(rule.Sound);
        if (!isVideo && sound is null && !string.IsNullOrWhiteSpace(rule.Sound))
            Log($"Event \"{rule.Name}\": sound file \"{rule.Sound}\" not found in {AssetsDir} (check the exact name and extension).");
        if (g is null && sound is null && videoPath is null)
        {
            if (warned.Add(rule.Name))
                Log($"Event \"{rule.Name}\": nothing to show yet (file missing or still loading). Put the files in {Path.Combine(Shared.FunDir, "<event number>")}");
            return;
        }

        if (force) StopOverlay();
        lastPlayed = now;

        if (videoPath is not null)
        {
            try
            {
                var vol = (rule.Volume ?? Volume) / 100f;
                video.Show(videoPath, rule.Size ?? SizePx, PlacementFor(rule.Position ?? Position), vol);
                shownVideo = true;
                startedAt = now;
                durationSeconds = MaxSeconds;
            }
            catch (Exception e)
            {
                Log($"Cannot play video {Path.GetFileName(videoPath)}: {e.Message}");
                StopOverlay();
                return;
            }
        }
        else if (g is not null)
        {
            var where = rule.Position ?? Position;
            if (where == "All 4 corners")
            {
                var height = rule.Size ?? SizePx;
                var corners = new GifOverlay.Placement[] { new(1, 0, -30, 30), new(0, 0, 30, 30), new(1, 1, -30, -30), new(0, 1, 30, -30) };
                overlay.Show(g, height, corners[0]);
                for (var i = 0; i < extraOverlays.Length; i++) extraOverlays[i].Show(g, height, corners[i + 1]);
                extrasShown = true;
            }
            else overlay.Show(g, rule.Size ?? SizePx, PlacementFor(where));
            shownGif = g;
            shownHasSound = sound is not null;
            startedAt = now;
            durationSeconds = sound is not null
                ? MaxSeconds
                : Math.Min(MaxSeconds, Math.Max(3, g.TotalMs / 1000.0));
        }
        if (sound is not null) audio.Play(sound, (int)(rule.Volume ?? Volume), Log);

        Log($"Overlay \"{rule.Name}\" ({reason})");
    }

    // ---------------------------------------------------------------- Bannières de tricks

    void QueueBanner(string name, TrickPerformed t, bool eventMatched)
    {
        if (!BannerOn) return;
        if (IsRailInProgress(name)) return; // encore en train de grinder : on attend le nom complet, une fois descendu
        if (bannerPending is { } bp && bp.Seq == t.SequenceId)
        {
            // Mise à jour du même combo (nom complet d'un rail) : on garde le dernier nom et on repart pour le délai « sans crash ».
            bannerPending = bp with { Name = name, EventMatched = bp.EventMatched || eventMatched, Due = Stopwatch.GetTimestamp() + (long)(SettleSeconds * Stopwatch.Frequency) };
        }
        else if (t.IsUpdate && t.SequenceId == lastBannerSeq) return;
        else
        {
            lastBannerSeq = t.SequenceId;
            bannerPending = (name, t.SequenceId, Stopwatch.GetTimestamp() + (long)(SettleSeconds * Stopwatch.Frequency), eventMatched);
        }
        var shown = DisplayName(bannerPending!.Value.Name);
        var tier = TrickBanner.Tier(shown);
        banners.GetOrAdd(shown + "|" + tier, _ => Task.Run(() =>
        {
            try { return TrickBanner.Render(shown, tier); }
            catch (Exception e) { Log("Banner render: " + e.Message); return null; }
        }));
    }

    string DisplayName(string name) => BannerNames == "Full" ? name : TrickCatalog.ShortName(name);

    /// <summary>« Left 270 On - Slide » : le rail n'est pas fini (le nom final se termine par « Out »).</summary>
    static bool IsRailInProgress(string name)
    {
        var words = name.Split(new[] { ' ', '-' }, StringSplitOptions.RemoveEmptyEntries);
        return words.Contains("On", StringComparer.OrdinalIgnoreCase) && !words.Contains("Out", StringComparer.OrdinalIgnoreCase);
    }

    void UpdateBanner(long now)
    {
        if (bannerPending is { } p && now >= p.Due)
        {
            var tier = TrickBanner.Tier(p.Name);
            if (!textBannerBroken && TryShowText(DisplayName(p.Name), now))
            {
                bannerPending = null;
                PlayBannerSound(tier, p.EventMatched);
                Log($"Banner: {p.Name} (tier {tier}, intensity {TrickBanner.Intensity(p.Name):0.00})");
            }
            else if (!banners.TryGetValue(DisplayName(p.Name) + "|" + tier, out var task)) bannerPending = null;
            else if (task.IsCompleted || now - p.Due > Stopwatch.Frequency) // on attend le dessin au plus 1 s
            {
                bannerPending = null;
                if (task.IsCompletedSuccessfully && task.Result is { } anim)
                {
                    var (bh, bplace, angle) = SplashSpot(anim);
                    bannerOverlay.Show(anim, bh, bplace);
                    if (angle != 0) bannerOverlay.Rotate(angle);
                    shownBanner = anim;
                    bannerStart = now;
                    PlayBannerSound(tier, p.EventMatched);
                    Log($"Banner: {p.Name} (tier {tier})");
                }
            }
        }

        if (shownText)
        {
            var ms = (long)Stopwatch.GetElapsedTime(textStart, now).TotalMilliseconds;
            if (!textBanner.Update(ms)) StopBanner();
        }

        if (shownBanner is { } b)
        {
            var ms = (long)Stopwatch.GetElapsedTime(bannerStart, now).TotalMilliseconds;
            if (ms >= b.TotalMs - 100) StopBanner();
            else bannerOverlay.Draw(b, Math.Min(b.FrameAt(ms), b.Frames.Count - 1));
        }
    }

    readonly Random rng = new();
    (float X, float Y)? lastBannerCenter;

    /// <summary>Choisit où afficher la bannière : au hasard hors du coureur, ou à l'endroit fixe demandé. Renvoie (taille en px, emplacement, inclinaison).</summary>
    (float Height, GifOverlay.Placement Placement, float Angle) SplashSpot(GifAnimation anim)
    {
        var height = anim.Height * BannerSizePct / 100f;
        if (!BannerPosition.StartsWith("Random")) return (height, BannerPlacement(), 0);
        try
        {
            var (cw, ch, sf) = ScreenLayout.CanvasSize();
            var forbidden = new List<(float, float, float, float)> { (cw / 2 - 210, ch / 2 - 260, cw / 2 + 210, ch / 2 + 260) }; // la caméra suit le coureur : le centre est toujours à lui
            try
            {
                if (ScreenLayout.PlayerPoint(sf, cw, ch) is { } p) forbidden.Add((p.X - 190, p.Y - 150, p.X + 190, p.Y + 330));
            }
            catch (Exception e) { LogOnce("rider position: " + e.Message); }

            for (var scale = 1f; scale >= 0.35f; scale *= 0.88f)
            {
                var h = height * scale;
                var w = h * anim.Width / anim.Height;
                if (ScreenLayout.Choose(rng, cw, ch, w, h, forbidden, lastBannerCenter) is { } pos)
                {
                    lastBannerCenter = (pos.X + w / 2, pos.Y + h / 2);
                    return (h, new GifOverlay.Placement(0, 0, pos.X + w / 2, pos.Y + h / 2, 0.5f, 0.5f), (float)(rng.NextDouble() * 14 - 7));
                }
            }
        }
        catch (Exception e) { LogOnce("banner layout: " + e.Message); }
        return (height * 0.6f, new GifOverlay.Placement(0.5f, 1, 0, -40), 0); // repli : en haut, loin du coureur
    }

    readonly HashSet<string> loggedOnce = new();
    void LogOnce(string message) { if (loggedOnce.Add(message)) Log(message); }

    // ---------------------------------------------------------------- Liste de tricks du jeu : on la masque

    nint[] gameTrickLists = Array.Empty<nint>();
    int hideCountdown;

    /// <summary>
    /// Cache l'affichage de tricks du jeu (classe « zo » de cette version : liste des tricks sous le coureur).
    /// Le jeu continue de la mettre à jour, mais elle est rendue invisible chaque frame.
    /// </summary>
    int hideApplyIn;
    void HideGameTrickList()
    {
        if (!HideGameTricks || !BannerOn) { originalTricks.Dispose(); return; }
        if (hideCountdown-- <= 0)
        {
            hideCountdown = 60; // on cherche de nouveau toutes les secondes (changement de map...)
            try
            {
                trickRoots.Dispose();
                gameTrickLists = Jibbers.Interop.Game.bij.Live().Select(z => z.Ptr).ToArray();
                foreach (var pointer in gameTrickLists) trickRoots.Keep(pointer);
                if (gameTrickLists.Length > 0) LogOnce($"Game trick display found ({gameTrickLists.Length}): hidden while banners are on.");
            }
            catch (Exception e) { LogOnce("Cannot find the game trick display: " + e.Message); gameTrickLists = Array.Empty<nint>(); }
        }
        if (hideApplyIn-- > 0) return;   // the game does not bring it back by itself: re-hidden about 3 times a second
        hideApplyIn = 20;
        foreach (var list in gameTrickLists)
        {
            try
            {
                if (Unity.IsNull(list)) continue;
                var rect = new Jibbers.Interop.Game.bij(list).zyt.Ptr;
                if (rect == 0 || Unity.IsNull(rect)) continue;
                originalTricks.Hide(rect);
            }
            catch (Exception e) { LogOnce("Hide trick display: " + e.Message); hideCountdown = 600; }
        }
    }

    void PlayBannerSound(int tier, bool eventMatched)
    {
        var file = Sfx.PathFor(Shared.SoundsDir, tier);
        if (BannerSound && !eventMatched && !audio.Playing && File.Exists(file)) audio.Play(file, (int)Volume, Log);
    }

    /// <summary>Affiche le nom avec la police du jeu. Renvoie faux (et passe au dessin intégré) si ce n'est pas possible.</summary>
    bool TryShowText(string name, long now)
    {
        try
        {
            if (textFont == 0 || Unity.IsNull(textFont)) textFont = TextBanner.FindFont();
            if (textFont == 0) return false; // le jeu n'a pas encore chargé de police : on réessaiera au prochain trick

            StopBanner();
            var intensity = TrickBanner.Intensity(name);
            var (w, h) = textBanner.Prepare(name, intensity, BannerSizePct / 100f, textFont);
            var spot = TextSpot(w, h);
            textBanner.Place(spot.X, spot.Y, spot.Angle, spot.Fit);
            shownText = true;
            textStart = now;
            textBanner.Update(0);
            return true;
        }
        catch (Exception e)
        {
            Log("Game font banner failed, using the built-in lettering: " + e.Message);
            textBannerBroken = true;
            textBanner.Dispose();
            return false;
        }
    }

    /// <summary>Centre, inclinaison et facteur de réduction d'une bannière de w x h pixels du Canvas.</summary>
    (float X, float Y, float Angle, float Fit) TextSpot(float w, float h)
    {
        var (cw, ch, sf) = ScreenLayout.CanvasSize();
        if (!BannerPosition.StartsWith("Random"))
        {
            var (x, y) = BannerPosition switch
            {
                "Center" => (cw / 2, ch / 2),
                "Bottom center" => (cw / 2, 120 + h / 2),
                "Top left" => (40 + w / 2, ch - 100 - h / 2),
                _ => (cw / 2, ch - 90 - h / 2),
            };
            return (x, y, 0, 1);
        }

        var forbidden = new List<(float, float, float, float)> { (cw / 2 - 210, ch / 2 - 260, cw / 2 + 210, ch / 2 + 260) }; // la caméra suit le coureur
        try
        {
            if (ScreenLayout.PlayerPoint(sf, cw, ch) is { } p) forbidden.Add((p.X - 190, p.Y - 150, p.X + 190, p.Y + 330));
        }
        catch (Exception e) { LogOnce("rider position: " + e.Message); }

        for (var fit = 1f; fit >= 0.35f; fit *= 0.88f)
        {
            if (ScreenLayout.Choose(rng, cw, ch, w * fit, h * fit, forbidden, lastBannerCenter) is { } pos)
            {
                var center = (X: pos.X + w * fit / 2, Y: pos.Y + h * fit / 2);
                lastBannerCenter = center;
                return (center.X, center.Y, (float)(rng.NextDouble() * 12 - 6), fit);
            }
        }
        return (cw / 2, ch - 60 - h * 0.3f, 0, 0.6f); // repli : en haut
    }

    GifOverlay.Placement BannerPlacement() => BannerPosition switch
    {
        "Center" => new(0.5f, 0.5f, 0, 0),
        "Bottom center" => new(0.5f, 0, 0, 120),
        "Top left" => new(0, 1, 40, -100),
        _ => new(0.5f, 1, 0, -90),
    };

    void StopBanner()
    {
        shownBanner = null;
        shownText = false;
        try { textBanner.Dispose(); }
        catch (Exception e) { Log("Banner cleanup: " + e.Message); }
        try { bannerOverlay.Dispose(); textBanner.Dispose(); }
        catch (Exception e) { Log("Banner cleanup: " + e.Message); }
    }

    internal void StopFun() { pending = null; StopOverlay(); }

    void StopOverlay()
    {
        shownGif = null;
        shownVideo = false;
        shownHasSound = false;
        audio.Stop();
        extrasShown = false;
        foreach (var extra in extraOverlays)
        {
            try { extra.Dispose(); }
            catch (Exception e) { Log("Overlay cleanup: " + e.Message); }
        }
        try { overlay.Dispose(); video.Dispose(); }
        catch (Exception e) { Log("Overlay cleanup: " + e.Message); }
    }

    string lastCorner = "";

    GifOverlay.Placement PlacementFor(string position)
    {
        if (position is "Random corner" or "All 4 corners")
        {
            string[] corners = { "Bottom right", "Bottom left", "Top right", "Top left" };
            var choices = corners.Where(c => c != lastCorner).ToArray(); // jamais deux fois le même coin de suite
            position = lastCorner = choices[rng.Next(choices.Length)];
        }
        return position switch
        {
            "Bottom left" => new(0, 0, 30, 30),
            "Top right" => new(1, 1, -30, -30),
            "Top left" => new(0, 1, 30, -30),
            "Center" => new(0.5f, 0.5f, 0, 0),
            _ => new(1, 0, -30, 30),
        };
    }

    // ---------------------------------------------------------------- Fichiers

    IEnumerable<string> SearchDirs()
    {
        yield return AssetsDir;
        yield return Shared.FunDir;
        for (var n = 1; n <= Shared.FunSlots; n++) yield return Path.Combine(Shared.FunDir, n.ToString());
        foreach (var sub in Shared.OverlayFolders()) yield return Path.Combine(AssetsDir, sub); // events.json peut citer « rick.gif » sans le dossier
        yield return Path.Combine(Bootstrap.ModsDir, "cs");
        yield return Bootstrap.ModsDir;
    }

    /// <summary>Chemin complet d'un fichier nommé dans events.json (dossier du mod en premier), ou null.</summary>
    string? FindFile(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        if (Path.IsPathRooted(name)) return File.Exists(name) ? name : null;
        foreach (var dir in SearchDirs())
        {
            var path = Path.Combine(dir, name);
            if (File.Exists(path)) return path;
        }
        return null;
    }

    /// <summary>
    /// Le GIF prêt à l'emploi, ou null s'il est absent ou encore en cours de décodage.
    /// Lance (ou relance si le fichier a changé) le décodage sur un thread à part.
    /// </summary>
    GifAnimation? GetGif(string? name)
    {
        var path = FindFile(name);
        if (path is null) return null;

        var entry = gifs.GetOrAdd(path, _ => new GifEntry());
        var stamp = File.GetLastWriteTimeUtc(path);
        if (!entry.Loading && entry.Stamp != stamp)
        {
            entry.Loading = true;
            Task.Run(() =>
            {
                try
                {
                    var loaded = GifAnimation.Load(path, Log);
                    entry.Gif = loaded;
                    Log($"GIF loaded: {Path.GetFileName(path)} ({loaded.Width}x{loaded.Height}, {loaded.Frames.Count} frames)");
                }
                catch (Exception e)
                {
                    Log($"Cannot read {Path.GetFileName(path)}: {e.Message}");
                }
                finally
                {
                    entry.Stamp = stamp; // un fichier cassé n'est pas relu en boucle
                    entry.Loading = false;
                }
            });
        }
        return entry.Gif;
    }
}
