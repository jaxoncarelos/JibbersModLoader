using System.Text.RegularExpressions;
using Jibbers.Sdk;
using Jibbers.Sdk.Interop;

namespace HankModPack;

/// <summary>
/// Retrouve le HUD d'origine du jeu à partir de ses textes (« SPAWNPOINT », « Speed: », « KPH »), en lit les valeurs
/// (nom du spawnpoint, stance, vitesse de respawn, vitesse actuelle) et le rend invisible. Le jeu continue de le mettre à jour.
/// </summary>
sealed class GameHudReader
{
    static readonly Regex StanceRx = new(@"^\s*(regular|switch\b.*|fakie|goofy)\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex DigitsRx = new(@"\d+", RegexOptions.Compiled);

    nint spawnPanel, speedBlock;
    nint[] spawnTexts = Array.Empty<nint>();
    nint[] speedTexts = Array.Empty<nint>();
    int countdown, checkIn, readIn, hideIn;
    bool logged;
    long nextSceneSearch;
    readonly UiVisibility hidden = new();
    readonly ObjectRoots roots = new();

    public string SpawnName = "", Stance = "", RespawnSpeed = "";
    public int? Kmh;
    /// <summary>The Object Placer window (/place) is open: the HUD is hidden (the cursor alone does not always tell).</summary>
    public bool PlacerOpen;

    /// <summary>Vrai quand le HUD d'origine existe, donc quand on est en jeu (pas dans les menus).</summary>
    public bool InGame => spawnPanel != 0 && !Unity.IsNull(spawnPanel);

    nint themeRow;
    /// <summary>Vrai quand la ligne « HUD theme » du menu Options > Mods est affichée (section du HUD ouverte) : on montre l'aperçu.</summary>
    public bool PreviewOpen
    {
        get
        {
            try { return themeRow != 0 && !Unity.IsNull(themeRow) && Unity.Call<bool>(Unity.Call(themeRow, "get_gameObject"), "get_activeInHierarchy"); }
            catch { return false; }
        }
    }

    string lastState = "";
    int dumpsLeft = 12, valueLogCountdown;
    string lastValues = "";

    static string TextOf(nint t)
    {
        if (t == 0 || Unity.IsNull(t)) return "";
        var s = Unity.Call(t, "get_text");
        return s == 0 ? "" : Il2Cpp.ReadString(s).Trim();
    }

    static bool Active(nint t)
    {
        try { return t != 0 && !Unity.IsNull(t) && Unity.Call<bool>(Unity.Call(t, "get_gameObject"), "get_activeInHierarchy"); }
        catch { return false; }
    }

    static nint Parent(nint transform)
    {
        var p = Unity.Call(transform, "get_parent");
        return p == 0 || Unity.IsNull(p) ? 0 : p;
    }

    static List<nint> Ancestors(nint t)
    {
        var list = new List<nint>();
        for (var tr = Unity.Transform(t); tr != 0 && list.Count < 24; tr = Parent(tr)) list.Add(tr);
        return list;
    }

    /// <summary>Cherche (au plus une fois par seconde) les deux blocs du HUD d'origine ; lit ensuite leurs textes à chaque image.</summary>
    public void Refresh(Action<string> log)
    {
        try
        {
            // Si un bloc d'origine a été détruit ou désactivé (respawn, changement de map), on le cherche de nouveau tout de suite.
            // Performance: the checks and the (expensive) search of the whole scene run rarely, the texts are read about 20 times a second.
            if (checkIn-- <= 0)
            {
                checkIn = 20;
                if ((speedBlock != 0 && !Active(speedBlock)) || (spawnPanel != 0 && !Active(spawnPanel))) countdown = 0;
            }
            if (countdown-- <= 0 && Environment.TickCount64 >= nextSceneSearch)
            {
                nextSceneSearch = Environment.TickCount64 + 1000;
                Find(log);
                hideIn = 0;
                // Found: no need to search again for a long time (a destroyed block is noticed above). Not found (menus): once every 3 seconds.
                countdown = speedBlock != 0 && spawnPanel != 0 ? 1800 : 180;
            }
            if (readIn-- <= 0) { readIn = 2; ReadValues(); }
            if (InGame && dumpsLeft > 0 && valueLogCountdown-- <= 0)
            {
                valueLogCountdown = 90;
                var values = $"name='{SpawnName}' stance='{Stance}' respawn='{RespawnSpeed}' kmh={(Kmh?.ToString() ?? "null")}";
                if (values != lastValues)
                {
                    lastValues = values;
                    dumpsLeft--;
                    log("HUD values: " + values + " | spawn texts: [" + string.Join(" | ", spawnTexts.Select(TextOf)) + "] | speed texts: [" + string.Join(" | ", speedTexts.Select(TextOf)) + "]");
                }
            }
        }
        catch (Exception e) { if (!logged) log("HUD reader: " + e.Message); logged = true; }
    }

    void Find(Action<string> log)
    {
        hidden.Dispose();
        roots.Dispose();
        spawnPanel = speedBlock = themeRow = 0;
        spawnTexts = speedTexts = Array.Empty<nint>();
        var tmpClass = Unity.Class("TMPro.TMP_Text");
        themeRow = 0;
        PlacerOpen = false;
        nint spawnLabel = 0, speedLabel = 0, kph = 0;
        var kphCandidates = new List<nint>();
        foreach (var t in Unity.FindObjects(tmpClass))
        {
            var s = TextOf(t);
            if (s.Length > 0 && s.Length < 40 && s.StartsWith("HUD theme", StringComparison.OrdinalIgnoreCase)) { themeRow = t; continue; }
            if (s.Length == 0 || s.Length > 24) continue;
            if ((s.Equals("Object Placer", StringComparison.OrdinalIgnoreCase) || s.StartsWith("Object Controls", StringComparison.OrdinalIgnoreCase)) && Active(t)) PlacerOpen = true;
            if (!Active(t)) continue; // après un respawn le jeu garde l'ancien HUD, inactif : on ne veut que le nouveau
            if (spawnLabel == 0 && s.Equals("SPAWNPOINT", StringComparison.OrdinalIgnoreCase)) spawnLabel = t;
            else if (speedLabel == 0 && s.StartsWith("Speed", StringComparison.OrdinalIgnoreCase) && s.TrimEnd().EndsWith(':')) speedLabel = t;
            else if (Regex.IsMatch(s, @"^\s*(\d+\s*)?kph\s*$", RegexOptions.IgnoreCase)) kphCandidates.Add(t);
        }

        spawnPanel = 0;
        spawnTexts = Array.Empty<nint>();
        if (spawnLabel != 0 && speedLabel != 0)
        {
            // Le panneau est le plus petit parent commun du titre « SPAWNPOINT » et du texte « Speed: ».
            var a = Ancestors(spawnLabel);
            var b = Ancestors(speedLabel).ToHashSet();
            spawnPanel = a.FirstOrDefault(b.Contains);
            if (spawnPanel != 0) spawnTexts = Unity.GetComponentsInChildren(spawnPanel, tmpClass, true);
        }

        // « KPH » du compteur de vitesse : ni dans le panneau du spawnpoint (son « 15 kph » est la vitesse de respawn),
        // et de préférence le texte « KPH » seul, séparé du nombre.
        foreach (var c in kphCandidates.OrderBy(c => DigitsRx.IsMatch(TextOf(c)) ? 1 : 0))
        {
            if (spawnPanel != 0 && Ancestors(c).Contains(spawnPanel)) continue;
            kph = c;
            break;
        }

        speedBlock = 0;
        speedTexts = Array.Empty<nint>();
        if (kph != 0)
        {
            // Le bloc de vitesse : le premier parent de « KPH » qui contient aussi un nombre (la vitesse).
            foreach (var anc in Ancestors(kph).Take(5))
            {
                if (anc == spawnPanel) break;
                var texts = Unity.GetComponentsInChildren(anc, tmpClass, true);
                if (texts.Any(x => x != kph && int.TryParse(TextOf(x), out _)) || DigitsRx.IsMatch(TextOf(kph)))
                {
                    speedBlock = anc;
                    speedTexts = texts;
                    break;
                }
            }
        }

        foreach (var pointer in spawnTexts.Concat(speedTexts).Append(spawnPanel).Append(speedBlock).Append(themeRow)) roots.Keep(pointer);

        var state = $"Game HUD: spawn panel {(spawnPanel != 0 ? "found" : "NOT found")}, speed block {(speedBlock != 0 ? "found" : "NOT found")}.";
        if (state != lastState) { lastState = state; log(state); }
    }

    void ReadValues()
    {
        if (spawnTexts.Length > 0)
        {
            string name = "", stance = "", respawn = "";
            foreach (var t in spawnTexts)
            {
                var s = TextOf(t);
                if (s.Length == 0) continue;
                if (s.Equals("SPAWNPOINT", StringComparison.OrdinalIgnoreCase)) continue;
                if (s.StartsWith("Speed", StringComparison.OrdinalIgnoreCase) && s.EndsWith(':')) continue;
                if (s.EndsWith("kph", StringComparison.OrdinalIgnoreCase)) respawn = s;
                else if (StanceRx.IsMatch(s)) stance = s;
                else if (name.Length == 0) name = s;
            }
            if (name.Length > 0) SpawnName = name;
            if (stance.Length > 0) Stance = stance;
            if (respawn.Length > 0) RespawnSpeed = respawn.ToLowerInvariant().Replace("kph", "km/h");
        }

        Kmh = null;
        foreach (var t in speedTexts)
        {
            var s = TextOf(t);
            if (s.Length == 0 || s.EndsWith("kph", StringComparison.OrdinalIgnoreCase) && !DigitsRx.IsMatch(s.Replace("kph", "", StringComparison.OrdinalIgnoreCase))) continue;
            var m = DigitsRx.Match(s);
            if (m.Success && int.TryParse(m.Value, out var v)) { Kmh = v; break; }
        }
    }

    /// <summary>Rend invisible les deux blocs d'origine (le jeu continue de les mettre à jour, on lit leurs textes).</summary>
    public void HideOriginals()
    {
        if (hideIn-- > 0 && hidden.Count > 0) return;   // the game rarely shows them again: once every 20 frames is enough
        hideIn = 20;
        foreach (var tr in new[] { spawnPanel, speedBlock })
        {
            if (tr == 0 || Unity.IsNull(tr)) continue;
            try
            {
                hidden.Hide(tr);
            }
            catch { /* retenté à la prochaine recherche */ }
        }
    }

    /// <summary>Rend le HUD d'origine visible (HUD du mod coupé ou mod déchargé).</summary>
    public void Restore()
    {
        hidden.Dispose();
        roots.Dispose();
        spawnPanel = speedBlock = themeRow = 0;
        spawnTexts = speedTexts = Array.Empty<nint>();
        countdown = 0;
    }

    public void RestoreAll() => Restore();
}
