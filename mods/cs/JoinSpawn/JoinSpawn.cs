using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Jibbers.Sdk;
using Jibbers.Sdk.Game.Map;
using Jibbers.Sdk.Game.Player;
using Jibbers.Sdk.Interop;
using Jibbers.Sdk.UI;
using EnvManager = Jibbers.Sdk.Game.Env.Manager;

namespace JoinSpawn;

enum Page { Closed, Players, Position }

/// <summary>Un autre joueur de la session, repéré par sa plaque de nom (« Nameplate ») : nom, objet de la plaque, distance à nous, plaque active ou non.</summary>
sealed record Entry(string Name, nint Plate, float Distance, bool Active)
{
    public string Label => Name + (Distance >= 0 ? $"  ({Distance:0} m)" : "") + (Active ? "" : JoinSpawnMod.T("  (loin)", "  (far)"));
}

/// <summary>
/// Fait apparaître ton rider près d'un autre joueur de la session.
/// Une touche ouvre la liste des joueurs en ligne ; une fois le joueur choisi, on règle la position
/// (devant/derrière, gauche/droite, hauteur) et on décide si on réapparaît en même temps que lui.
/// Ne lit que la position des autres joueurs ; ne modifie que TON point de spawn.
/// </summary>
public sealed class JoinSpawnMod : JibbersMod
{
    public override string Id => "join_spawn";
    public override string Name => "Join Spawn";
    public override string Author => "Hank";
    public override string Description => T(
        "Apparais à côté d'un joueur choisi dans une liste, avec l'écart de ton choix.",
        "Spawn next to a player picked from a list, with the offset of your choice.");

    [Choice("Language / Langue", "Auto", "Auto", "English", "Français", Key = "language", Section = "Controls / Contrôles",
        Tooltip = "Language of the in-game panel. Auto follows the Windows language. / Langue du panneau en jeu. Auto suit la langue de Windows.")]
    public string Language { get; private set; } = "Auto";

    static string language = "Auto";

    /// <summary>Texte français ou anglais selon le réglage de langue (Auto = langue de Windows).</summary>
    internal static string T(string fr, string en)
    {
        var french = language switch
        {
            "Français" => true,
            "English" => false,
            _ => CultureInfo.InstalledUICulture.TwoLetterISOLanguageName == "fr",
        };
        return french ? fr : en;
    }

    [KeyBinding("Open player list / Ouvrir la liste des joueurs", Key.F6, Key = "open", Section = "Controls / Contrôles",
        Tooltip = "Opens/closes the panel. Inside: up/down to choose, left/right to adjust, Enter to confirm. / Ouvre/ferme le panneau. Dedans : flèches haut/bas pour choisir, gauche/droite pour régler, Entrée pour valider.")]
    public Key OpenKey { get; private set; }

    [KeyBinding("Open (2nd key) / Ouvrir (2e touche)", Key.Insert, Key = "open2", Section = "Controls / Contrôles",
        Tooltip = "A second keyboard key to open/close the panel, in addition to the first. / Une deuxième touche du clavier pour ouvrir/fermer le panneau, en plus de la première.")]
    public Key OpenKey2 { get; private set; }

    [KeyBinding("Diagnostic (writes to the log / écrit dans le log)", Key.F10, Key = "diagnostic", Section = "Controls / Contrôles",
        Tooltip = "Writes the scene objects that look like players to jibbers_hotload.log. Press it near other players. / Écrit dans jibbers_hotload.log les objets de la scène qui ressemblent à des joueurs. À presser près d'autres joueurs.")]
    public Key DiagnosticKey { get; private set; }

    static readonly Dictionary<string, PadButton> PadChoices = new()
    {
        ["None"] = PadButton.None,
        ["Back"] = PadButton.Back,
        ["Start"] = PadButton.Start,
        ["LB + RB"] = PadButton.LeftShoulder | PadButton.RightShoulder,
        ["LB"] = PadButton.LeftShoulder,
        ["RB"] = PadButton.RightShoulder,
        ["Left stick click"] = PadButton.LeftStick,
        ["Right stick click"] = PadButton.RightStick,
        ["D-pad up"] = PadButton.DpadUp,
        ["D-pad down"] = PadButton.DpadDown,
        ["D-pad left"] = PadButton.DpadLeft,
        ["D-pad right"] = PadButton.DpadRight,
        ["A"] = PadButton.A,
        ["B"] = PadButton.B,
        ["X"] = PadButton.X,
        ["Y"] = PadButton.Y,
    };

    [Choice("Gamepad button to open / Bouton manette pour ouvrir", "None", "None", "Back", "Start", "LB + RB", "LB", "RB", "Left stick click", "Right stick click",
        "D-pad up", "D-pad down", "D-pad left", "D-pad right", "A", "B", "X", "Y", Key = "pad_open2", Section = "Controls / Contrôles",
        Tooltip = "Xbox/XInput button that opens or closes the panel. Avoid A/B/X/Y: the game uses them too. With the panel open: D-pad to navigate, A confirms, B closes. / Bouton de manette (Xbox/XInput) qui ouvre ou ferme le panneau. Évite A/B/X/Y : le jeu s'en sert aussi. Panneau ouvert : croix directionnelle pour naviguer, A valide, B ferme.")]
    public string PadOpen { get; private set; } = "None";

    [Slider("Forward (+) / back (-) in m / Devant (+) / derrière (-) en m", 0, -30, 30, 0.5, Key = "forward", Section = "Position",
        Tooltip = "Distance along the chosen player's direction. Positive = in front of them, negative = behind. / Distance le long de la direction du joueur choisi. Positif = devant lui, négatif = derrière lui.")]
    public float Forward { get; private set; }

    [Slider("Right (+) / left (-) in m / Droite (+) / gauche (-) en m", 3, -30, 30, 0.5, Key = "side", Section = "Position",
        Tooltip = "Sideways distance from the chosen player. Positive = to their right, negative = to their left. / Distance sur le côté du joueur choisi. Positif = à sa droite, négatif = à sa gauche.")]
    public float Side { get; private set; }

    [Slider("Height in m / Hauteur en m", -1.5, -5, 5, 0.25, Key = "height2", Section = "Position",
        Tooltip = "Height relative to their nameplate (which floats above their head). About -1.5 = same height as them. / Hauteur par rapport à sa plaque de nom (qui flotte au-dessus de sa tête). -1,5 environ = à la même hauteur que lui.")]
    public float Height { get; private set; }

    [Toggle("Copy their speed and angle / Copier sa vitesse et son angle", true, Key = "copy_motion", Section = "Position",
        Tooltip = "You spawn facing their direction of travel and with their speed. / Tu apparais orienté dans sa direction de marche et avec sa vitesse.")]
    public bool CopyMotion { get; private set; }

    [Choice("Reference / Repère", "Their direction", "Their direction", "World axes", Key = "reference2", Section = "Position",
        Tooltip = "Their direction: forward/back follow their recent direction of travel (otherwise where your camera looks). World axes: forward = the map's Z axis. / Their direction : devant/derrière suivent sa direction de marche récente (à défaut, là où regarde ta caméra). World axes : devant = axe Z de la map.")]
    public string Reference { get; private set; } = "Their direction";

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate nint SelectedSpawnFn(nint spawner, nint methodInfo);

    Detour<SelectedSpawnFn>? spawnDetour;
    OverlayPanel? panel;

    /// <summary>Nom (nametag) du joueur choisi, ou null = spawn normal.</summary>
    string? chosen;

    /// <summary>Points de spawn modifiés et leur position d'origine (gardés vivants par GameRef).</summary>
    readonly List<(GameRef Spawn, Vector3 Position, Vector3 Rotation, Vector3 Velocity)> touched = new();

    string? bodyPath;
    readonly Dictionary<nint, (string Tag, double At)> nametags = new();

    // --- interface ---
    Page page = Page.Closed;
    int cursor;
    List<Entry> players = new();
    GameRef? chosenRider;

    // --- suivi du joueur choisi (position de sa plaque, direction de marche, réapparition) ---
    Vector3 lastPosition;
    Vector3? heading;
    Vector3 velocity;
    long lastSample, lastResolve;
    bool haveSample;

    public override void OnLoad()
    {
        language = Language;
        panel = Overlay.Register("Join Spawn", new OverlayOptions { Anchor = HudAnchor.MiddleLeft, Offset = new(10, 0) });

        Input.OnPressed(() => OpenKey, ToggleOpen);
        Input.OnPressed(() => OpenKey2, ToggleOpen);
        Input.OnPressed(Key.Up, () => Navigate(-1));
        Input.OnPressed(Key.Down, () => Navigate(1));
        Input.OnPressed(Key.Left, () => Adjust(-1));
        Input.OnPressed(Key.Right, () => Adjust(1));
        Input.OnPressed(Key.Enter, Confirm);
        Input.OnPressed(() => DiagnosticKey, Diagnose);

        LoadEvent.Subscribe(_ =>
        {
            Restore();
            ClearTracking();
        });
        EnvManager.UpdateMethod.Before((_, _) => Tick());

        // Le jeu demande son point de spawn à chaque (re)spawn : on renvoie le même, déplacé.
        spawnDetour = RagdollSpawner.SelectedSpawnMethod.Detour<SelectedSpawnFn>(SelectedSpawn);
        spawnDetour.Enable();
    }

    // ===================== Spawn =====================

    nint SelectedSpawn(nint spawner, nint methodInfo)
    {
        var spawn = spawnDetour!.Original(spawner, methodInfo);
        try
        {
            if (spawn != 0 && Enabled && chosen is { } name)
                Move(spawn, name);
        }
        catch (Exception e)
        {
            Log("spawn non déplacé : " + e.Message);
        }
        return spawn;
    }

    void Move(nint spawn, string name)
    {
        var plate = FindPlate(name);
        if (plate == 0)
        {
            Log($"« {name} » n'est pas dans la session : spawn normal.");
            return;
        }
        var position = Unity.Position(plate);
        if (!PlateActive(plate))
        {
            // Joueur loin : sa plaque est masquée, on prend la dernière position connue de la plaque.
            if (position.Magnitude < 1)
            {
                Log($"« {name} » est loin et sa plaque n'a pas de position connue : spawn normal.");
                return;
            }
            Log($"« {name} » est loin : dernière position connue de sa plaque ({position.X:0}, {position.Y:0}, {position.Z:0}).");
        }
        var (forward, right) = Axes();
        var target = position + forward * Forward + right * Side + new Vector3(0, Height, 0);

        var point = new SpawnPoint(spawn);
        var raw = point.Raw;
        if (!touched.Any(t => t.Spawn.Target == spawn))
            touched.Add((new GameRef(spawn), point.Position, raw.rotation, raw.velocity));
        point.Position = target;
        if (CopyMotion)
        {
            if (heading is { } h)
            {
                var yaw = (float)(Math.Atan2(h.X, h.Z) * 180 / Math.PI);
                raw.rotation = new Vector3(raw.rotation.X, yaw, raw.rotation.Z);
            }
            raw.velocity = velocity;
            Log($"Mouvement copié : vitesse ({velocity.X:0.0}, {velocity.Y:0.0}, {velocity.Z:0.0}) m/s, cap {(heading is null ? "inconnu (il ne bouge pas)" : "ok")}.");
        }
        Log($"Spawn près de « {name} » : devant {Forward:0.#} m, droite {Side:0.#} m, hauteur {Height:0.##} m.");
    }

    /// <summary>
    /// Avant et droite, à plat. « Sa direction » = sa direction de marche récente (sa plaque se tourne vers
    /// la caméra, donc son orientation ne dit rien) ; à défaut, là où regarde ta caméra.
    /// </summary>
    (Vector3 Forward, Vector3 Right) Axes()
    {
        var f = new Vector3(0, 0, 1);
        if (Reference == "Their direction")
        {
            if (heading is { } h)
                f = h;
            else
                try
                {
                    var v = Unity.Call<Vector3>(Unity.Transform(Unity.MainCamera), "get_forward");
                    var flat = new Vector3(v.X, 0, v.Z);
                    if (flat.Magnitude > 0.01f)
                        f = flat * (1 / flat.Magnitude);
                }
                catch (Exception e) when (e is InvalidOperationException or MissingMethodException) { }
        }
        // Unity est main gauche : droite = haut x avant.
        return (f, new Vector3(f.Z, 0, -f.X));
    }

    /// <summary>Remet les points de spawn modifiés à leur position d'origine.</summary>
    void Restore()
    {
        foreach (var (spawn, original, rotation, speed) in touched)
        {
            try
            {
                var point = new SpawnPoint(spawn.Target);
                point.Position = original;
                var raw = point.Raw;
                raw.rotation = rotation;
                raw.velocity = speed;
            }
            catch (Exception e) { Log("restauration du spawn : " + e.Message); }
            spawn.Dispose();
        }
        touched.Clear();
    }

    // ===================== Suivi du joueur choisi =====================

    // --- manette ---
    readonly bool[] connected = new bool[4];
    PadButton previousPad;
    uint frameNumber;

    /// <summary>Boutons tenus, tous les pads XInput confondus (Steam Input et co. ajoutent des pads virtuels).</summary>
    PadButton ReadPads()
    {
        var held = PadButton.None;
        var probeEmptySlots = ++frameNumber % 120 == 1;
        for (var slot = 0; slot < connected.Length; slot++)
        {
            if (!connected[slot] && !probeEmptySlots)
                continue;
            var pad = Gamepad.Read(slot);
            if (pad.Connected != connected[slot])
            {
                connected[slot] = pad.Connected;
                Log($"manette {(pad.Connected ? "connectée" : "déconnectée")} (slot {slot + 1}).");
            }
            if (pad.Connected)
                held |= pad.Buttons;
        }
        return held;
    }

    /// <summary>Le bouton manette d'ouverture, et les touches de navigation du panneau (croix, A, B).</summary>
    void PollPad()
    {
        var held = ReadPads();
        var before = previousPad;
        previousPad = held;
        if (!Input.GameFocused || Input.Typing)
            return;
        bool Pressed(PadButton b) => b != PadButton.None && (held & b) == b && (before & b) != b;

        if (PadChoices.TryGetValue(PadOpen, out var open) && Pressed(open))
        {
            ToggleOpen();
            return;
        }
        if (page == Page.Closed)
            return;
        if (Pressed(PadButton.DpadUp)) Navigate(-1);
        else if (Pressed(PadButton.DpadDown)) Navigate(1);
        else if (Pressed(PadButton.DpadLeft)) Adjust(-1);
        else if (Pressed(PadButton.DpadRight)) Adjust(1);
        else if (Pressed(PadButton.A)) Confirm();
        else if (Pressed(PadButton.B)) Close();
    }

    /// <summary>Chaque image : lit la manette, suit la plaque du joueur choisi (direction de marche) ; s'il se téléporte, réapparaît aussi.</summary>
    void Tick()
    {
        PollPad();
        if (chosen is not { } name)
        {
            ClearTracking();
            return;
        }
        var now = Stopwatch.GetTimestamp();

        // Retrouve la plaque une fois par seconde : elle peut être réutilisée pour un autre joueur.
        if (chosenRider is null || Unity.IsNull(chosenRider.Target) || Stopwatch.GetElapsedTime(lastResolve, now).TotalSeconds > 1)
        {
            lastResolve = now;
            var found = FindPlate(name);
            if (found == 0)
            {
                ClearTracking();
                return;
            }
            if (chosenRider is null || chosenRider.Target != found)
            {
                chosenRider?.Dispose();
                chosenRider = new GameRef(found);
                haveSample = false;
            }
        }
        var plate = chosenRider!.Target;
        if (!PlateActive(plate))
        {
            if (haveSample)
                Log($"« {name} » : plaque masquée (trop loin ?) à {lastPosition.X:0},{lastPosition.Y:0},{lastPosition.Z:0}.");
            haveSample = false;
            return;
        }

        var position = Unity.Position(plate);
        var seconds = haveSample ? Stopwatch.GetElapsedTime(lastSample, now).TotalSeconds : 0;
        var delta = position - lastPosition;
        var jump = haveSample ? delta.Magnitude : 0;
        (lastPosition, lastSample, haveSample) = (position, now, true);
        if (seconds <= 0)
            return;

        // Une téléportation = un saut d'au moins 6 m à une vitesse impossible à ski (> 50 m/s) ;
        // après une image très longue (> 0,25 s), il faut un saut bien plus grand.
        var teleport = jump >= 6 && (seconds < 0.25 ? jump / seconds > 50 : jump >= 18);
        if (seconds >= 0.25 && !teleport)
            return;

        if (teleport)
        {
            // Une téléportation, pas de la marche.
            heading = null;
            velocity = new Vector3(0, 0, 0);
            return;
        }

        // Vitesse réelle (m/s), lissée.
        var instant = delta * (float)(1 / seconds);
        velocity = new Vector3(velocity.X * 0.7f + instant.X * 0.3f, velocity.Y * 0.7f + instant.Y * 0.3f, velocity.Z * 0.7f + instant.Z * 0.3f);

        // Direction de marche : lissée, mise à jour seulement quand il avance vraiment.
        var flat = new Vector3(delta.X, 0, delta.Z);
        if (flat.Magnitude / seconds > 1.5)
        {
            var d = flat * (1 / flat.Magnitude);
            var mixed = heading is { } h ? new Vector3(h.X * 0.8f + d.X * 0.2f, 0, h.Z * 0.8f + d.Z * 0.2f) : d;
            heading = mixed.Magnitude > 0.01f ? mixed * (1 / mixed.Magnitude) : d;
        }
    }

    void ClearTracking()
    {
        chosenRider?.Dispose();
        chosenRider = null;
        heading = null;
        velocity = new Vector3(0, 0, 0);
        haveSample = false;
    }

    // ===================== Panneau =====================

    void ToggleOpen()
    {
        if (page != Page.Closed)
        {
            Close();
            return;
        }
        RefreshPlayers();
        page = chosen is null ? Page.Players : Page.Position;
        cursor = page == Page.Players ? Math.Max(0, players.FindIndex(IsChosen) + 1) : 1;
        Redraw();
    }

    void Close()
    {
        page = Page.Closed;
        panel?.Hide();
    }

    /// <summary>Toutes les plaques de nom de la scène, avec leur texte (le nom du joueur). Actives ou non.</summary>
    static List<(string Name, nint Plate)> Plates()
    {
        var result = new List<(string, nint)>();
        foreach (var t in FindAll(Unity.Class("TMPro.TMP_Text")))
        {
            var root = CharacterRoot(t);
            if (!Unity.Name(root).StartsWith("Nameplate", StringComparison.Ordinal))
                continue;
            var name = Clean(Il2Cpp.ReadString(Unity.Call(t, "get_text")));
            if (name.Length > 0)
                result.Add((name, root));
        }
        return result;
    }

    static bool PlateActive(nint plate) => Unity.Call<bool>(Unity.Call(plate, "get_gameObject"), "get_activeInHierarchy");

    nint FindPlate(string name)
    {
        var wanted = Normalize(name);
        var matches = Plates().Where(p => Normalize(p.Name) == wanted).ToList();
        return (matches.FirstOrDefault(p => PlateActive(p.Plate)).Plate is var active && active != 0) ? active : matches.FirstOrDefault().Plate;
    }

    void RefreshPlayers()
    {
        players = new();
        var mine = 0;
        Vector3? here = null;
        if (RagdollSpawner.First() is { } spawner && !spawner.Camera.IsNull && !spawner.Camera.Target.IsNull)
        {
            here = Unity.Position(spawner.Camera.Target.Ptr);
            mine = 1;
        }
        foreach (var (name, plate) in Plates())
        {
            var active = PlateActive(plate);
            var distance = here is { } h ? (Unity.Position(plate) - h).Magnitude : -1;
            players.Add(new Entry(name, plate, distance, active));
        }
        players = players
            .GroupBy(p => Normalize(p.Name))
            .Select(g => g.OrderByDescending(p => p.Active).ThenBy(p => p.Distance).First())
            .OrderByDescending(p => p.Active)
            .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        Log($"liste des joueurs : {players.Count} plaque(s) de nom (position de référence : {(mine == 1 ? "ton rider" : "inconnue")}) : " +
            string.Join(", ", players.Take(40).Select(p => $"[{p.Name}{(p.Active ? "" : " loin")}]")));
    }

    // Lignes de la page Position : 0 joueur, 1 devant, 2 côté, 3 hauteur, 4 réapparition, 5 terminé.
    const int PositionRows = 5;

    void Navigate(int step)
    {
        if (page == Page.Closed)
            return;
        var count = page == Page.Players ? players.Count + 1 : PositionRows;
        cursor = (cursor + step + count) % count;
        Redraw();
    }

    void Adjust(int direction)
    {
        if (page != Page.Position)
            return;
        switch (cursor)
        {
            case 1: SetNumber("forward", Forward + direction * 0.5, -30, 30); break;
            case 2: SetNumber("side", Side + direction * 0.5, -30, 30); break;
            case 3: SetNumber("height2", Height + direction * 0.25, -5, 5); break;
        }
        Redraw();
    }

    void SetNumber(string key, double value, double min, double max) =>
        SetSetting(key, JsonValue.Create(Math.Round(Math.Clamp(value, min, max) * 4) / 4)!);

    void Confirm()
    {
        if (page == Page.Closed)
            return;
        if (page == Page.Players)
        {
            if (cursor == 0)
            {
                chosen = null;
                Restore();
                ClearTracking();
                Log("Joueur choisi : aucun (spawn normal).");
                Close();
                return;
            }
            var entry = players[cursor - 1];
            chosen = entry.Name;
            chosenRider?.Dispose();
            chosenRider = new GameRef(entry.Plate);
            heading = null;
            haveSample = false;
            Restore();
            ClearTracking();
            Log($"Joueur choisi : {ChosenLabel}.");
            page = Page.Position;
            cursor = 1;
        }
        else
        {
            switch (cursor)
            {
                case 0: // changer de joueur
                    RefreshPlayers();
                    page = Page.Players;
                    cursor = Math.Max(0, players.FindIndex(IsChosen) + 1);
                    break;
                case 4:
                    Close();
                    return;
            }
        }
        Redraw();
    }

    void Redraw()
    {
        if (panel is null || page == Page.Closed)
            return;
        var rows = new List<(string Label, string Value)>();
        string title, hint;
        if (page == Page.Players)
        {
            title = T("Choisis un joueur", "Pick a player");
            hint = T("↑↓ choisir   Entrée valider   ", "↑↓ choose   Enter confirm   ") + OpenKey + T(" fermer", " close");
            rows.Add((T("Aucun (spawn normal)", "None (normal spawn)"), ""));
            foreach (var p in players)
                rows.Add((p.Label, ""));
            if (players.Count == 0)
                rows.Add((T("Aucun autre joueur dans la session", "No other player in the session"), ""));
        }
        else
        {
            title = T("Position près de ", "Position near ") + ChosenLabel;
            hint = T("↑↓ ligne   ←→ régler   Entrée valider   ", "↑↓ row   ←→ adjust   Enter confirm   ") + OpenKey + T(" fermer", " close");
            rows.Add((T("Joueur", "Player"), ChosenLabel + T("  (Entrée : changer)", "  (Enter: change)")));
            rows.Add((T("Devant (+) / derrière (-)", "Forward (+) / back (-)"), $"{Forward:+0.0;-0.0;0.0} m"));
            rows.Add((T("Droite (+) / gauche (-)", "Right (+) / left (-)"), $"{Side:+0.0;-0.0;0.0} m"));
            rows.Add((T("Hauteur", "Height"), $"{Height:0.00} m"));
            rows.Add((T("Terminé", "Done"), ""));
        }

        const float margin = 24, width = 520, rowHeight = 44, pad = 22;
        const int maxRows = 9;
        var first = Math.Clamp(cursor - maxRows + 1, 0, Math.Max(0, rows.Count - maxRows));
        var shown = rows.Skip(first).Take(maxRows).ToList();
        var height = 92 + shown.Count * rowHeight + 54;

        var f = new OverlayFrame(width + margin * 2, height + margin * 2).Origin(margin, margin);
        f.Card(0, 0, width, height, 20);
        f.Gradient(20, 0, width - 40, 3, 1.5f, Theme.Ice, Theme.Violet, horizontal: true);
        f.Text("JOIN SPAWN", pad, 20, 300, 18, 13, Theme.Muted, bold: true, spacing: 6);
        f.Text(title, pad, 40, width - pad * 2, 36, 26, Theme.Text, bold: true);

        for (var i = 0; i < shown.Count; i++)
        {
            var y = 92 + i * rowHeight;
            var selected = first + i == cursor;
            if (selected)
            {
                f.Rect(pad - 8, y, width - pad * 2 + 16, rowHeight - 4, 12, Theme.Raised);
                f.Border(pad - 8, y, width - pad * 2 + 16, rowHeight - 4, 12, 1, Theme.Ice);
            }
            var color = selected ? Theme.Text : Theme.Muted;
            f.Text((selected ? "▸ " : "   ") + shown[i].Label, pad, y + 8, shown[i].Value.Length > 0 ? 270 : width - pad * 2, 26, 21, color, bold: selected);
            if (shown[i].Value.Length > 0)
                f.Text(shown[i].Value, pad + 270, y + 8, width - pad * 2 - 270, 26, 21, selected ? Theme.Ice : Theme.Faint, HudTextAlignment.Right);
        }
        f.Text(hint, pad, height - 38, width - pad * 2, 22, 15, Theme.Faint);

        panel.Draw(f);
        panel.Show();
    }

    // ===================== Lecture des autres riders (même technique que SLVSH) =====================

    string ChosenLabel => chosen is null ? "" : Clean(chosen);

    bool IsChosen(Entry e) => chosen is not null && Normalize(e.Name) == Normalize(chosen);

    /// <summary>Apprend quelle partie du rider la caméra suit, pour lire la position du corps et pas l'origine.</summary>
    void Learn(RagdollSpawner spawner)
    {
        if (bodyPath is not null || spawner.Camera.IsNull || spawner.Rider.IsNull)
            return;
        var root = Unity.Transform(spawner.Rider.Ptr);
        var names = new List<string>();
        for (var t = spawner.Camera.Target.Ptr; t != 0 && !Unity.IsNull(t); t = Unity.Call(t, "get_parent"))
        {
            if (t == root)
            {
                names.Reverse();
                bodyPath = string.Join("/", names);
                return;
            }
            names.Add(Unity.Name(t));
        }
        bodyPath = "";
    }

    nint BodyOf(nint ragdoll)
    {
        var root = Unity.Transform(ragdoll);
        if (string.IsNullOrEmpty(bodyPath))
            return root;
        var part = Unity.Call(root, "Find", Il2Cpp.String(bodyPath));
        return part != 0 && !Unity.IsNull(part) ? part : root;
    }

    string Nametag(nint ragdoll, double now)
    {
        if (now > 0 && nametags.TryGetValue(ragdoll, out var cached) && now - cached.At < 3)
            return cached.Tag;
        if (nametags.Count > 64)
            nametags.Clear();
        var tag = "";
        try
        {
            var text = Unity.Class("TMPro.TMP_Text");
            foreach (var t in Unity.GetComponentsInChildren(CharacterRoot(ragdoll), text, includeInactive: true))
            {
                var s = Il2Cpp.ReadString(Unity.Call(t, "get_text")).Trim();
                if (s.Length > 0) { tag = s; break; }
            }
        }
        catch (Exception e) when (e is InvalidOperationException or MissingMethodException) { }
        nametags[ragdoll] = (tag, now);
        return tag;
    }

    static nint CharacterRoot(nint component)
    {
        var t = Unity.Transform(component);
        nint clone = 0, belowRoot = t;
        while (t != 0 && !Unity.IsNull(t))
        {
            if (Unity.Name(t).EndsWith("(Clone)", StringComparison.Ordinal)) clone = t;
            var parent = Unity.Call(t, "get_parent");
            if (parent != 0 && !Unity.IsNull(parent)) belowRoot = t;
            t = parent;
        }
        return clone != 0 ? clone : belowRoot;
    }

    /// <summary>Nom sans balises de texte riche, pour comparer et afficher.</summary>
    static string Clean(string text) => Regex.Replace(text, "<[^>]*>", "").Trim();
    static string Normalize(string text) => Clean(text).ToLowerInvariant();


    // ===================== Diagnostic =====================

    static unsafe nint[] FindAll(Il2CppClass k)
    {
        var finder = Unity.Class("UnityEngine.Object").Method("FindObjectsByType", "System.Type", "UnityEngine.FindObjectsInactive", "UnityEngine.FindObjectsSortMode");
        if (finder.IsNull)
            throw new MissingMethodException("FindObjectsByType(Type, FindObjectsInactive, FindObjectsSortMode)");
        int include = 1, sort = 0;
        return Il2Cpp.ArrayObjects(finder.Invoke(0, k.SystemType, (nint)(&include), (nint)(&sort)));
    }

    static string Where(nint obj)
    {
        var v = Unity.Position(obj);
        return $"({v.X:0},{v.Y:0},{v.Z:0})";
    }

    /// <summary>Écrit dans le log ce qui ressemble à des joueurs : riders (avec inactifs), textes de nametag, objets racine « (Clone) ».</summary>
    void Diagnose()
    {
        try
        {
            Log("=== diagnostic Join Spawn ===");
            var me = RagdollSpawner.First() is { } sp && !sp.Rider.IsNull ? sp.Rider.Ptr : 0;
            Log($"ton rider : {(me == 0 ? "introuvable" : Where(me))}");

            try
            {
                var ragdolls = FindAll(Ragdoll.Class.Class);
                Log($"RagdollController : {Ragdoll.All().Length} actif(s), {ragdolls.Length} en comptant les inactifs.");
                foreach (var r in ragdolls.Take(40))
                {
                    var go = Unity.Call(r, "get_gameObject");
                    Log($"  rider {Unity.Name(r)} {Where(r)} actif={Unity.Call<bool>(go, "get_activeInHierarchy")}{(r == me ? " (toi)" : "")}");
                }
            }
            catch (Exception e) { Log("  riders (avec inactifs) : " + e.Message); }

            try
            {
                var roots = FindAll(Unity.Class("UnityEngine.Transform"))
                    .Where(t => Unity.Call(t, "get_parent") == 0)
                    .Select(Unity.Name)
                    .GroupBy(n => n)
                    .OrderByDescending(g => g.Count())
                    .ThenBy(g => g.Key)
                    .ToList();
                Log($"objets racine de la scène : {roots.Count} noms distincts. Ceux en « (Clone) » :");
                foreach (var g in roots.Where(g => g.Key.EndsWith("(Clone)", StringComparison.Ordinal)).Take(60))
                    Log($"  {g.Key} x{g.Count()}");
            }
            catch (Exception e) { Log("  objets racine : " + e.Message); }

            try
            {
                var texts = FindAll(Unity.Class("TMPro.TMP_Text"));
                var lines = 0;
                Log($"textes TextMeshPro : {texts.Length}. Ceux sous un objet « (Clone) » :");
                foreach (var t in texts)
                {
                    var text = Clean(Il2Cpp.ReadString(Unity.Call(t, "get_text")));
                    if (text.Length == 0)
                        continue;
                    var root = CharacterRoot(t);
                    if (!Unity.Name(root).EndsWith("(Clone)", StringComparison.Ordinal))
                        continue;
                    var go = Unity.Call(t, "get_gameObject");
                    Log($"  « {text} » racine={Unity.Name(root)} {Where(root)} actif={Unity.Call<bool>(go, "get_activeInHierarchy")}");
                    if (++lines >= 60)
                        break;
                }
            }
            catch (Exception e) { Log("  textes : " + e.Message); }
            Log("=== fin du diagnostic ===");
        }
        catch (Exception e)
        {
            Log("diagnostic impossible : " + e.Message);
        }
    }

    // ===================== Cycle de vie =====================

    public override void OnSettingChanged(string key, JsonNode value)
    {
        if (key == "language")
        {
            language = value.GetValue<string>();
            MainThread.Later(Redraw);
        }
        if (key == "enabled" && !value.GetValue<bool>())
            MainThread.Later(() =>
            {
                Restore();
                ClearTracking();
                Close();
            });
    }

    public override void OnUnload()
    {
        MainThread.Run(() =>
        {
            Restore();
            ClearTracking();
            chosenRider?.Dispose();
            panel?.Dispose();
            panel = null;
        });
    }
}
