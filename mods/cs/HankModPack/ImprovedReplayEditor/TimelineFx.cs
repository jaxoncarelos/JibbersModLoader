using System.Runtime.InteropServices;
using System.Text.Json;
using Jibbers.Sdk;
using Jibbers.Sdk.Interop;
using Jibbers.Sdk.Game.Camera;
using Jibbers.Sdk.Game.Event;
using Jibbers.Sdk.Game.Replay;
using Jibbers.Sdk.UI;
using EnvManager = Jibbers.Sdk.Game.Env.Manager;
using RawFormMenu = Jibbers.Interop.Game.FormMenu;

namespace TimelineFx;

/// <summary>
/// Adds "Time of day" and "Camera shake" to the replay editor's own keyframe panel. Each camera keyframe
/// carries the two values; they are interpolated between keyframes like the game's FOV or roll.
/// The values are saved per clip in JibbersMods\Hank Mod Pack\Replay Editor Keyframes\.
/// </summary>
public sealed unsafe class TimelineFxMod : HankModPack.PackPart
{
    protected override string PartId => "timeline_fx";
    public override string Name => "Hank Mod Pack: Improved Replay Editor";
    public override string Author => "Hank";
    public override string Description => "Replay editor: time of day and camera shake as keyframe parameters.";

    [Toggle("Smooth time of day between keyframes", Key = "smooth", Section = "Time of day")]
    public bool Smooth { get; private set; }
    [KeyBinding("Save the effect keyframes as preset", (Key)0, Key = "save_preset_key", Section = "Preset")]
    public Key SavePresetKey { get; private set; }
    [KeyBinding("Delete the effect keyframe at the playhead", (Key)0, Key = "delete_key", Section = "Keys")]
    public Key DeleteKey { get; private set; }

    [Slider("Shake max rotation at strength 1 (degrees)", 1.5, 0, 8, 0.05, Key = "shake_rot4", Section = "Camera shake", Tooltip = "Each keyframe has its own strength, speed, rotation/position/roll percentages and smoothness.")]
    public double RotationDeg { get; private set; }
    [Slider("Shake max position at strength 1 (metres)", 0.06, 0, 0.5, 0.005, Key = "shake_pos4", Section = "Camera shake")]
    public double PositionM { get; private set; }

    [Slider("FX timeline: gap below the game's text row (% of screen height)", 0.2, 0, 30, 0.5, Key = "strip_gap", Section = "Timeline", Tooltip = "Scales with the player's resolution.")]
    public double StripY { get; private set; }
    [Toggle("Group the game's own keyframe rows into tabs (experimental)", false, Key = "organize_base2", Section = "Timeline",
        Tooltip = "Transition tab (frame, path, position, rotation) and Camera tab (FOV, roll, time scale). Experimental: turn it off if the keyframe panel looks wrong.")]
    public bool OrganizeBaseRows { get; private set; }
    [Toggle("Add the Timeline FX tabs to the keyframe panel", true, Key = "menu_rows", Section = "Timeline")]
    public bool MenuRows { get; private set; }
    [Toggle("Apply the newest saved preset of the map to replays that have no effect keyframes yet", false, Key = "auto_preset", Section = "Preset",
        Tooltip = "Save a preset with the \"Save preset\" button of the keyframe panel. Every replay opened without effect keyframes of its own then starts from it.")]
    public bool AutoPreset { get; private set; }
    [Choice("Save preset: what to save", "Effects", "Effects", "Game keyframes", "Both", Key = "preset_content", Section = "Preset",
        Tooltip = "Used by the save key (F11). The buttons of the keyframe panel let you pick per click. Effects = this mod's effect keyframes. Game keyframes = the replay's own camera keyframes (position, rotation, FOV...). Both = the two together.")]
    public string PresetContent { get; private set; } = "Effects";
    [Toggle("Show the effects window (all the replay effects, live)", true, Key = "fx_window", Section = "Timeline",
        Tooltip = "A window in the style of the HUD theme with the value of every effect at the playhead, on the side of the screen opposite to the keyframe panel.")]
    public bool FxWindowOn { get; private set; }
    [Choice("Effects window side", "Right", "Right", "Left", Key = "fx_window_side", Section = "Timeline")]
    public string FxWindowSide { get; private set; } = "Right";
    [Toggle("Show the FX timeline", true, Key = "strip_on", Section = "Timeline")]
    public bool StripOn { get; private set; }

    // ---- per keyframe data ----
    sealed class KfData
    {
        public bool Tod { get; set; }
        public float TodVal { get; set; } = 0.5f;
        public bool ShakeOn { get; set; }
        public float Shake { get; set; }
        public float Hz { get; set; } = 3;
        public float RotPct { get; set; } = 100;
        public float PosPct { get; set; } = 100;
        public float RollPct { get; set; } = 20;
        public float Smoothness { get; set; } = 80;
        public Dictionary<string, float> Fx { get; set; } = new();
    }
    sealed class Saved
    {
        public int Frame { get; set; } public bool ShakeOn { get; set; } public bool Tod { get; set; } public float TodVal { get; set; } public float Shake { get; set; } public float Hz { get; set; } = 3;
        public float RotPct { get; set; } = 100; public float PosPct { get; set; } = 100; public float RollPct { get; set; } = 20; public float Smoothness { get; set; } = 80; public Dictionary<string, float>? Fx { get; set; }
    }

    sealed class FxKey { public int Frame; public KfData D = new(); }
    readonly List<FxKey> keys = new();                      // the effect keyframes of the clip
    (int Frame, KfData D)[] ordered = Array.Empty<(int, KfData)>();
    string clipKey = "";
    bool dirty; float nextSave;

    // ---- state (main thread) ----
    bool inReplay => HankModPack.CameraOwnership.InReplay;
    bool needInit, envSaved;
    GameRef? environmentRoot;
    readonly HankModPack.ObjectRoots cameraRoots = new();
    float playhead, maxSeen;
    float savedT, savedSpeed; bool savedFixed, savedUpdate;
    float phase;
    nint lastCam; Vector3 lastPosOffset; Quaternion lastRotOffset = Quaternion.Identity;
    int frame, hitFollow, hitRecorder, hitApply; float nextDiag;
    float previewTod = -1, previewUntil; KfData? previewShake;
    static TimelineFxMod? current;

    // ---- menu injection ----
    readonly Dictionary<nint, string> menuTitle = new();
    readonly Dictionary<nint, float> menuTouched = new();
    readonly HashSet<nint> injected = new();
    int injectedCount;

    public override void OnLoad()
    {
        ImportPackSettings();
        Log($"TLFX: Hank Mod Pack build {typeof(TimelineFxMod).Assembly.GetName().Version}");
        MoveOldFolder();
        current = this;
        Input.OnPressed(() => DeleteKey, () => { if (inReplay) DeleteHere(); });
        Input.OnPressed(() => SavePresetKey, () => { if (inReplay) SavePresetNamed(WhatFromOption(PresetContent), DefaultPresetName()); });
        EnterReplay.Subscribe(_ => { MainThread.Run(ResetAll);  needInit = true; Log("TLFX: EnterReplay"); });
        ExitReplay.Subscribe(_ => {   Log($"TLFX: ExitReplay (max playhead seen {maxSeen:0.###})"); MainThread.Run(ResetAll); });
        LoadReplayClip.Subscribe(_ => { clipKey = ""; });

        EnvManager.UpdateMethod.Before((self, _) => { try { OnEnvUpdate(self); } catch (Exception e) { LogOnce("env", e); } });
        HankModPack.CameraCallbacks.Follow.Register(this,
            _ => { hitFollow++; UndoShake(); },
            self => { ApplyShake("follow", self); ApplySunLight(); InjectPlayMenus(); }, order: 20);
        HankModPack.CameraCallbacks.Recorder.Register(this,
            _ => { hitRecorder++; UndoShake(); },
            _ => { ApplyShake("recorder"); InjectMenus(); TimelineStrip(); FxWindow(); }, order: 20);
        ChangeScene.Subscribe(_ => {  ResetAll(); });

        // Menus we add our rows to: the Camera Effects menu, and the game's "Keyframe #N" panels.
        Hooks.Before(RawFormMenu.Title_Method.Method, (self, arg0) =>
        {
            try
            {
                menuTitle[self] = arg0 == 0 ? "" : Il2Cpp.ReadString(arg0);
                if (!menuTitle[self].StartsWith("Keyframe", StringComparison.OrdinalIgnoreCase)) { fxHoldUntil = Unity.Time + 1.5f; nextFxScan = Math.Max(nextFxScan, Unity.Time + 1.5f); }
                menuTouched[self] = Unity.Time;
                injected.Remove(self);
            }
            catch (Exception e) { LogOnce("title", e); }
        });
        RawFormMenu.Clear_Method.Around((self, original) =>
        {
            try { menuTitle.Remove(self); injected.Remove(self); menuTouched[self] = Unity.Time; } catch { }
            original();
        });
    }

    protected override void OnDeactivated() => ResetAll();

    protected override void OnPackUnload()
    {
        ResetAll();
        fxView?.Dispose(); fxView = null;
        fxHint?.Dispose(); fxHint = null;
        current = null;
    }

    /// <summary>Forgets everything tied to the replay that just ended, so the next replay starts clean.</summary>
    void ResetAll()
    {
        if (environmentRoot is { } environment && !Unity.IsNull(environment.Target))
        {
            var previousEnvironment = new EnvManager(environment.Target).Raw;
            RestoreEnv(previousEnvironment);
            RestoreSky(previousEnvironment);
        }
        envSaved = skySaved = false;
        environmentRoot?.Dispose(); environmentRoot = null;
        try { UndoShake(); } catch { }
        try { fxView?.Hide(); fxHint?.Hide(); fxHintOn = false; fxWinSig = ""; if (fxMuted) { fxMute?.Restore(); fxMuted = false; } } catch { }
        dirty = false;
        try { RestoreFx(); } catch (Exception e) { LogOnce("restorefx", e); }
        try { RestoreAlp(); } catch (Exception e) { LogOnce("restorealp", e); }
        try { ClearStrip(); } catch { }
        keys.Clear(); ordered = Array.Empty<(int, KfData)>(); clipKey = ""; lastGameCount = -1; trimWant = -1;
        DropFx(); nextFxScan = 0; fxPreviewUntil = 0;
        try { DropLights(); } catch { }
        lightMode = "Off";
        menuTitle.Clear(); menuTouched.Clear(); injected.Clear(); injectedCount = 0;
        lock (bindings) { for (var index = 0; index < bindings.Count; index++) bindings[index] = default; }
        foreach (var g in keep) { try { g.Dispose(); } catch { } }
        keep.Clear();
        camCache = 0; lastCam = 0; camLogged = false;
        cameraRoots.Dispose();
        previewTod = -1; previewShake = null; previewUntil = 0;
        maxSeen = 0; curFrame = 0; playhead = 0;
        lastCam = 0; lastPosOffset = default; lastRotOffset = Quaternion.Identity;
        Log("TLFX: state reset");
    }

    readonly HashSet<string> logged = new();
    void LogOnce(string where, Exception e)
    {
        lock (logged) { if (logged.Add(where + e.GetType().Name)) Log($"TLFX error in {where}: {e}"); }
    }

    // ================= the clip and its keyframes =================

    float ReadPlayhead() => Recorder.Instance.Raw.playbackTime;

    static string Sanitize(string s) => new string(s.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_').ToArray());
    static string Dir => Path.Combine(RickBackflip.Shared.PackDir, "Replay Editor Keyframes");

    /// <summary>The clips' effects live in JibbersMods\Hank Mod Pack\Replay Editor Keyframes (moved there from the old JibbersMods\timeline_fx).</summary>
    static void MoveOldFolder()
    {
        try
        {
            // Older names of the folder (timeline_fx, then "Ire") are moved to the current one the first time.
            foreach (var old in new[] { Path.Combine(RickBackflip.Shared.PackDir, "Ire"), Path.Combine(Bootstrap.ModsDir, "timeline_fx") })
                if (Directory.Exists(old) && !Directory.Exists(Dir)) { Directory.CreateDirectory(RickBackflip.Shared.PackDir); Directory.Move(old, Dir); }
        }
        catch { /* the folder is created when the first clip is saved */ }
    }

    void RefreshClip()
    {
        var clip = Recorder.Instance.Clip;
        if (clip.IsNull) return;
        var name = clip.Raw.rvg;
        var key = Sanitize(string.IsNullOrWhiteSpace(name) ? "clip" : name);
        if (key == clipKey) return;
        clipKey = key;
        keys.Clear();
        try
        {
            var file = Path.Combine(Dir, key + ".fx.json");
            if (!File.Exists(file)) file = Path.Combine(Dir, key + ".kf.json");   // v5-v7 files
            if (File.Exists(file))
                foreach (var s in JsonSerializer.Deserialize<List<Saved>>(File.ReadAllText(file)) ?? new())
                    keys.Add(new FxKey { Frame = s.Frame, D = new KfData { Tod = s.Tod, TodVal = s.TodVal, ShakeOn = s.ShakeOn || s.Shake > 0, Shake = s.Shake, Hz = s.Hz, RotPct = s.RotPct, PosPct = s.PosPct, RollPct = s.RollPct, Smoothness = s.Smoothness, Fx = s.Fx ?? new() } });
        }
        catch (Exception e) { Log($"TLFX: could not read the clip's effects: {e.Message}"); }
        SyncKeyframes();
        if (keys.Count == 0 && AutoPreset && LatestPreset() is { } auto && ApplyPreset(auto, false, true)) { }
        Log($"TLFX: clip '{key}': {keys.Count} effect keyframes loaded");
    }

    // ---- preset: effect keyframes and/or the game's own camera keyframes, reusable on any replay ----
    // Positions are stored as a fraction of the clip (x10000), so a preset stretches to a longer or shorter replay.

    sealed class SavedCam
    {
        public int Frame { get; set; }
        public float[] Pos { get; set; } = new float[3];
        public float[] Rot { get; set; } = new float[4];
        public float[] F { get; set; } = new float[3];     // the three float values of the keyframe (FOV, roll, time scale)
        public bool[] B { get; set; } = new bool[6];       // which of them are keyed, and the other flags
        public string Command { get; set; } = "";
        public int[] Modes { get; set; } = new int[6];     // position mode, look direction, transition, path, ease in, ease out
    }
    sealed class PresetData
    {
        public List<Saved>? Effects { get; set; }
        public List<SavedCam>? Game { get; set; }
        public string Map { get; set; } = "";
        public string Name { get; set; } = "";
    }

    /// <summary>The replay's own camera keyframes, in time order.</summary>
    List<Jibbers.Interop.Game.op> GameKeyframes()
    {
        var result = new List<Jibbers.Interop.Game.op>();
        var clip = Recorder.Instance.Clip;
        if (clip.IsNull) return result;
        var list = clip.Raw.rvi;
        if (list == 0) return result;
        var n = Unity.Call<int>(list, "get_Count");
        for (var i = 0; i < n; i++)
        {
            var it = Unity.Call(list, "get_Item", (nint)(&i));
            if (it != 0) result.Add(new Jibbers.Interop.Game.op(it));
        }
        result.Sort((x, y) => x.rwi.CompareTo(y.rwi));
        return result;
    }

    List<SavedCam> CaptureGame()
    {
        var total = Math.Max(TotalFrames(), 1);
        return GameKeyframes().Select(k =>
        {
            var p = k.rwj; var r = k.rwk;
            return new SavedCam
            {
                Frame = (int)Math.Round(k.rwi / (double)total * 10000),
                Pos = new[] { p.X, p.Y, p.Z }, Rot = new[] { r.X, r.Y, r.Z, r.W },
                F = new[] { k.rwl, k.rwp, k.rwr },
                B = new[] { k.rwm, k.rwn, k.rwo, k.rwq, k.rws, k.rwu },
                Command = k.rwt ?? "",
                Modes = new[] { k.rwv, k.rww, k.rwx, k.rwy, k.rwz, k.rxa },
            };
        }).ToList();
    }

    // ---- presets live in Replay Editor Keyframes\Presets\<map>\<name>.json ----
    static string PresetsRoot => Path.Combine(Dir, "Presets");

    /// <summary>The name of the map being replayed (the active scene), or "Unknown map".</summary>
    string MapName()
    {
        if (mapName.Length > 0) return mapName;
        string name = "";
        try
        {
            var scene = Jibbers.Interop.UnityEngine.SceneManagement.SceneManager.GetActiveScene();
            if (scene != 0) { var p = Unity.Call(scene, "get_name"); if (p != 0) name = Il2Cpp.ReadString(p); }
        }
        catch (Exception e) { LogOnce("mapname", e); }
        if (string.IsNullOrWhiteSpace(name)) { try { name = Recorder.Instance.Clip.Raw.rvj ?? ""; } catch { } }
        if (string.IsNullOrWhiteSpace(name)) name = "Unknown map";
        mapName = name;
        Log($"TLFX: map name: {name}");
        return name;
    }
    string mapName = "";

    static string MapFolder(string map) => Path.Combine(PresetsRoot, Sanitize(map));

    /// <summary>The presets of one map, newest first.</summary>
    static List<string> PresetNames(string map)
    {
        try
        {
            var dir = MapFolder(map);
            if (!Directory.Exists(dir)) return new();
            return new DirectoryInfo(dir).GetFiles("*.json").OrderByDescending(x => x.LastWriteTimeUtc).Select(x => Path.GetFileNameWithoutExtension(x.Name)).ToList();
        }
        catch { return new(); }
    }

    /// <summary>Every preset of every map, as "Map / name".</summary>
    static List<(string Map, string Name)> AllPresets()
    {
        var all = new List<(string, string)>();
        try
        {
            if (Directory.Exists(PresetsRoot))
                foreach (var d in new DirectoryInfo(PresetsRoot).GetDirectories().OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
                    foreach (var file in d.GetFiles("*.json").OrderByDescending(x => x.LastWriteTimeUtc))
                        all.Add((d.Name, Path.GetFileNameWithoutExtension(file.Name)));
        }
        catch { }
        return all;
    }

    // State of the Save / Load / Delete rows of the keyframe panel.
    string saveWhat = "Both", saveName = "";
    int loadIndex, deleteIndex;

    string DefaultPresetName() => $"Preset {DateTime.Now:yyyy-MM-dd HH-mm}";

    /// <summary>Saves the chosen keyframes ("Game", "FX" or "Both") under this map's folder, with the given name.</summary>
    void SavePresetNamed(string what, string name)
    {
        try
        {
            var total = Math.Max(TotalFrames(), 1);
            var map = MapName();
            name = Sanitize(string.IsNullOrWhiteSpace(name) ? DefaultPresetName() : name.Trim());
            var data = new PresetData { Map = map, Name = name };
            var saved = new List<string>();
            if (what != "Game")
            {
                data.Effects = keys.Select(o => new Saved { Frame = (int)Math.Round(o.Frame / (double)total * 10000), ShakeOn = o.D.ShakeOn, Tod = o.D.Tod, TodVal = o.D.TodVal, Shake = o.D.Shake, Hz = o.D.Hz, RotPct = o.D.RotPct, PosPct = o.D.PosPct, RollPct = o.D.RollPct, Smoothness = o.D.Smoothness, Fx = new Dictionary<string, float>(o.D.Fx) }).ToList();
                saved.Add($"{data.Effects.Count} FX keyframes");
            }
            if (what != "FX")
            {
                data.Game = CaptureGame();
                saved.Add($"{data.Game.Count} game keyframes");
            }
            var dir = MapFolder(map);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, name + ".json"), JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }));
            Log($"TLFX: saved '{name}' for map '{map}' ({string.Join(", ", saved)}) in {dir}");
        }
        catch (Exception e) { Log($"TLFX: could not save the keyframes: {e.Message}"); }
    }

    static string WhatFromOption(string option) => option switch { "Effects" => "FX", "Game keyframes" => "Game", _ => "Both" };

    PresetData? ReadPreset(string map, string name)
    {
        try
        {
            var file = Path.Combine(MapFolder(map), name + ".json");
            return File.Exists(file) ? JsonSerializer.Deserialize<PresetData>(File.ReadAllText(file)) : null;
        }
        catch (Exception e) { Log($"TLFX: could not read '{name}': {e.Message}"); return null; }
    }

    void LoadPresetNamed(string name)
    {
        var data = ReadPreset(MapName(), name);
        if (data is null) { Log($"TLFX: no preset '{name}' for this map"); return; }
        ApplyPreset(data, true);
    }

    void DeletePresetNamed(string map, string name)
    {
        try
        {
            var file = Path.Combine(MapFolder(map), name + ".json");
            if (File.Exists(file)) { File.Delete(file); Log($"TLFX: deleted '{name}' ({map})"); }
            var dir = MapFolder(map);
            if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir);
        }
        catch (Exception e) { Log($"TLFX: could not delete '{name}': {e.Message}"); }
    }

    /// <summary>
    /// Applies a preset to this replay (stretched to its length): the FX keyframes are replaced, and the game's camera keyframes are applied,
    /// in time order, to the keyframes the replay already has (new ones cannot be created from here).
    /// </summary>
    bool ApplyPreset(PresetData data, bool log, bool effectsOnly = false)
    {
        try
        {
            var total = Math.Max(TotalFrames(), 1);
            var done = new List<string>();
            if (data.Effects is not null)
            {
                keys.Clear();
                foreach (var p in data.Effects)
                    keys.Add(new FxKey { Frame = (int)Math.Round(p.Frame / 10000.0 * total), D = new KfData { Tod = p.Tod, TodVal = p.TodVal, ShakeOn = p.ShakeOn || p.Shake > 0, Shake = p.Shake, Hz = p.Hz, RotPct = p.RotPct, PosPct = p.PosPct, RollPct = p.RollPct, Smoothness = p.Smoothness, Fx = p.Fx ?? new() } });
                SyncKeyframes(); dirty = true; previewTod = -1; previewShake = null;
                done.Add($"{keys.Count} FX keyframes");
            }
            if (!effectsOnly && data.Game is not null)
            {
                var mine = GameKeyframes();
                var n = Math.Min(mine.Count, data.Game.Count);
                for (var i = 0; i < n; i++)
                {
                    var c = data.Game[i]; var k = mine[i];
                    k.rwi = (int)Math.Round(c.Frame / 10000.0 * total);
                    k.rwj = new Vector3(c.Pos[0], c.Pos[1], c.Pos[2]);
                    k.rwk = new Quaternion(c.Rot[0], c.Rot[1], c.Rot[2], c.Rot[3]);
                    k.rwl = c.F[0]; k.rwp = c.F[1]; k.rwr = c.F[2];
                    k.rwm = c.B[0]; k.rwn = c.B[1]; k.rwo = c.B[2]; k.rwq = c.B[3]; k.rws = c.B[4]; k.rwu = c.B[5];
                    k.rwt = c.Command;
                    k.rwv = c.Modes[0]; k.rww = c.Modes[1]; k.rwx = c.Modes[2]; k.rwy = c.Modes[3]; k.rwz = c.Modes[4]; k.rxa = c.Modes[5];
                }
                done.Add($"{n} game keyframes");
                if (mine.Count != data.Game.Count)
                    Log($"TLFX: the preset has {data.Game.Count} game keyframes, this replay has {mine.Count}: only the first {n} were applied (add or remove keyframes to match).");
            }
            if (log) Log($"TLFX: loaded '{data.Name}' ({string.Join(", ", done)})");
            return done.Count > 0;
        }
        catch (Exception e) { Log($"TLFX: could not apply the preset: {e}"); return false; }
    }

    /// <summary>The newest preset of this map, for "apply automatically".</summary>
    PresetData? LatestPreset()
    {
        var names = PresetNames(MapName());
        return names.Count == 0 ? null : ReadPreset(MapName(), names[0]);
    }

    void Save()
    {
        if (clipKey.Length == 0) return;
        try
        {
            var list = keys.Select(o => new Saved { Frame = o.Frame, ShakeOn = o.D.ShakeOn, Tod = o.D.Tod, TodVal = o.D.TodVal, Shake = o.D.Shake, Hz = o.D.Hz, RotPct = o.D.RotPct, PosPct = o.D.PosPct, RollPct = o.D.RollPct, Smoothness = o.D.Smoothness, Fx = o.D.Fx }).ToList();
            Directory.CreateDirectory(Dir);
            File.WriteAllText(Path.Combine(Dir, clipKey + ".fx.json"), JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception e) { Log($"TLFX: could not save the clip's effects: {e.Message}"); }
    }

    /// <summary>Re-sorts the effect keyframes after any change.</summary>
    void SyncKeyframes()
    {
        keys.Sort((a, b) => a.Frame.CompareTo(b.Frame));
        ordered = keys.Select(k => (k.Frame, k.D)).ToArray();
    }

    float fullFrames, lastFullLogged;

    /// <summary>
    /// The game's bar always shows the whole recording; the trim handles only mark the kept part (clip end = rvt). So the bar's length is
    /// rvt divided by where the end handle sits on the bar.
    /// </summary>
    void ReadFullFrames(TrimHandleRef end)
    {
        var rvt = (float)Math.Max(TotalFrames(), 1);
        fullFrames = rvt;
        try
        {
            if (end.Circle == 0 || Unity.IsNull(end.Circle)) return;
            var x = Unity.Call<Vector3>(Unity.Transform(end.Circle), "get_position").X;
            var frac = (x - (gCenterX - gWidthPx / 2)) / Math.Max(gWidthPx, 1f);
            if (frac > 0.02f && frac < 1.5f) fullFrames = rvt / Math.Min(frac, 1f);
            if (Math.Abs(fullFrames - lastFullLogged) > 3f) { lastFullLogged = fullFrames; Log($"TLFX: bar length {fullFrames:0} frames (clip end rvt={rvt:0}, end handle at {frac:0.###} of the bar)"); }
        }
        catch (Exception e) { LogOnce("fullframes", e); }
    }
    readonly record struct TrimHandleRef(nint Circle);

    /// <summary>Position of a frame on the game's bar, 0..1.</summary>
    float Frac(float frame) => Math.Clamp(frame / Math.Max(fullFrames, 1f), 0f, 1f);

    int TotalFrames()
    {
        var clip = Recorder.Instance.Clip;
        return clip.IsNull ? 0 : clip.Raw.rvt;
    }

    int trimWant = -1; float trimUntil; int trimFixes;
    int lastGameCount = -1; float nextGameWatch;

    /// <summary>The first keyframe of the game's own timeline (when the clip has no keyframe at all yet) also moves the trim start onto it.</summary>
    void WatchGameKeys()
    {
        if (Unity.Time < nextGameWatch) return;
        nextGameWatch = Unity.Time + 0.25f;
        try
        {
            var g = GameKeyframes();
            if (lastGameCount < 0) { lastGameCount = g.Count; return; }
            if (lastGameCount == 0 && g.Count > 0 && keys.Count == 0)
            {
                var frame = g.Min(x => x.rwi);
                Log($"TLFX: first game keyframe at frame {frame}");
                TrimStartTo(frame);
            }
            lastGameCount = g.Count;
        }
        catch (Exception e) { LogOnce("gamewatch", e); }
    }

    /// <summary>The first effect keyframe of a clip moves the game's trim start there (only the start; the end is left alone).</summary>
    void TrimStartTo(int frame)
    {
        try
        {
            var clip = Recorder.Instance.Clip;
            if (clip.IsNull) { Log("TLFX: trim start: no clip"); return; }
            var raw = clip.Raw;
            var before = raw.rvs;
            if (frame <= before || frame >= raw.rvt - 1) { Log($"TLFX: trim start left at {before} (keyframe at {frame}, clip end {raw.rvt})"); return; }
            trimWant = frame; trimUntil = Unity.Time + 2f; trimFixes = 0;
            Log($"TLFX: trim start: moving from {before} to frame {frame} (first effect keyframe)");
            ApplyTrimStart();
        }
        catch (Exception e) { LogOnce("trimstart", e); }
    }

    void ApplyTrimStart()
    {
        var raw = Recorder.Instance.Clip.Raw;
        if (raw.rvs != trimWant) raw.rvs = trimWant;
        var handle = Recorder.Instance.Raw.trimStartHandle;
        if (handle.Ptr == 0 || handle.circle == 0 || Unity.IsNull(handle.circle)) return;
        var tr = Unity.Transform(handle.circle);
        var pos = Unity.Call<Vector3>(tr, "get_position");
        var x = gCenterX - gWidthPx / 2 + Frac(trimWant) * gWidthPx;
        if (Math.Abs(pos.X - x) > 1f) { pos.X = x; Unity.Call(tr, "set_position", (nint)(&pos)); }
    }

    /// <summary>For two seconds after the first keyframe, puts the trim start back if the game's own refresh moves it away again.</summary>
    void MaintainTrim()
    {
        if (trimWant < 0) return;
        try
        {
            if (Unity.Time > trimUntil) { Log($"TLFX: trim start: now {Recorder.Instance.Clip.Raw.rvs} (wanted {trimWant}, put back {trimFixes} time(s))"); trimWant = -1; return; }
            var raw = Recorder.Instance.Clip.Raw;
            if (raw.rvs != trimWant) { trimFixes++; if (trimFixes <= 3) Log($"TLFX: trim start was reset to {raw.rvs} by the game, putting {trimWant} back"); }
            ApplyTrimStart();
        }
        catch (Exception e) { LogOnce("trimmaintain", e); trimWant = -1; }
    }

    /// <summary>The effect keyframe at (or very near) the playhead, if any.</summary>
    FxKey? Selected()
    {
        if (keys.Count == 0) return null;
        var tol = Math.Max(2f, TotalFrames() * 0.002f);
        FxKey? best = null; var bd = float.MaxValue;
        foreach (var k in keys)
        {
            var d = Math.Abs(k.Frame - curFrame);
            if (d <= tol && d < bd) { bd = d; best = k; }
        }
        return best;
    }

    FxKey SelOrCreate()
    {
        var k = Selected();
        if (k != null) return k;
        Log($"TLFX: creating an effect keyframe at frame {(int)curFrame} (have {keys.Count})");
        var first = keys.Count == 0;
        k = new FxKey { Frame = (int)curFrame };
        keys.Add(k);
        if (first && lastGameCount <= 0) TrimStartTo(k.Frame);
        SyncKeyframes();
        dirty = true;
        Log($"TLFX: effect keyframe created at frame {k.Frame}");
        return k;
    }

    /// <summary>"Add keyframe" of a tab: creates the effect keyframe at the playhead (or reuses the one there) and switches this tab on, with the current values.</summary>
    void AddTab(string tab, IEnumerable<FxParam>? members)
    {
        var k = SelOrCreate();
        switch (tab)
        {
            case "Time of day":
                k.D.Tod = true; k.D.TodVal = CurrentEnvTime();
                if (EnvManager.First() is { } e0) { var r = e0.Raw; k.D.Fx[SunDir] = r.sunYAngle; k.D.Fx[SunHeight] = r.sunPeakAngle; k.D.Fx[MoonPh] = r.moonPhase; }
                break;
            case "Camera Shake": k.D.ShakeOn = true; if (k.D.Shake <= 0) k.D.Shake = 0.3f; break;
            case "Follow cam": case "POV": EnsureCam(k, tab); break;
            default:
                foreach (var m in members ?? Enumerable.Empty<FxParam>())
                    if (!k.D.Fx.ContainsKey(m.Label)) { try { k.D.Fx[m.Label] = FxRead(m); } catch { } }
                if (tab == "Blur")
                {
                    // A blur keyframe must actually blur: type Off shows nothing, so start with Radial and visible values.
                    foreach (var m in members ?? Enumerable.Empty<FxParam>())
                    {
                        var n = m.Label.Split('/').Last();
                        if (m.IsInt && n == "Blur" && k.D.Fx[m.Label] < 1) k.D.Fx[m.Label] = 1;
                        else if (n == "Blur Intensity" && k.D.Fx[m.Label] < 1) k.D.Fx[m.Label] = 4;
                        else if (n == "Radius" && k.D.Fx[m.Label] > 0.6f) k.D.Fx[m.Label] = 0.25f;
                        else if (n == "blur-radial-falloff" && k.D.Fx[m.Label] < 0.1f) k.D.Fx[m.Label] = 0.6f;
                    }
                }
                break;
        }
        SyncKeyframes();
        dirty = true;
        Log($"TLFX: '{tab}' keyframe added at frame {k.Frame}");
    }

    // ---- copy / paste / duplicate of one tab's options ----
    sealed class TabCopy { public float TodVal, Shake, Hz, RotPct, PosPct, RollPct, Smoothness; public Dictionary<string, float> Fx = new(); }
    readonly Dictionary<string, TabCopy> clipboard = new();
    readonly Dictionary<string, float> clipFrom = new();

    static bool HasTab(KfData d, string tab) => tab switch
    {
        "Time of day" => d.Tod || d.Fx.Keys.Any(l => InTab(l, tab)),
        "Camera Shake" => d.ShakeOn,
        _ => d.Fx.Keys.Any(l => InTab(l, tab)),
    };

    static TabCopy Capture(KfData d, string tab)
    {
        var c = new TabCopy { TodVal = d.TodVal, Shake = d.Shake, Hz = d.Hz, RotPct = d.RotPct, PosPct = d.PosPct, RollPct = d.RollPct, Smoothness = d.Smoothness };
        foreach (var (l, v) in d.Fx) if (InTab(l, tab)) c.Fx[l] = v;
        return c;
    }

    static void Paste(KfData d, string tab, TabCopy c)
    {
        switch (tab)
        {
            case "Time of day": d.Tod = true; d.TodVal = c.TodVal; foreach (var (l, v) in c.Fx) d.Fx[l] = v; break;
            case "Camera Shake":
                d.ShakeOn = true; d.Shake = c.Shake; d.Hz = c.Hz; d.RotPct = c.RotPct; d.PosPct = c.PosPct; d.RollPct = c.RollPct; d.Smoothness = c.Smoothness; break;
            default: foreach (var (l, v) in c.Fx) d.Fx[l] = v; break;
        }
    }

    /// <summary>What a tab looks like at the playhead when there is no effect keyframe on it: the values currently in effect.</summary>
    KfData LiveData(string tab)
    {
        var d = new KfData();
        switch (tab)
        {
            case "Time of day":
                d.Tod = true; d.TodVal = EvalTod() ?? CurrentEnvTime();
                if (EnvManager.First() is { } e)
                {
                    var r = e.Raw;
                    d.Fx[SunDir] = EvalSky(SunDir) ?? r.sunYAngle; d.Fx[SunHeight] = EvalSky(SunHeight) ?? r.sunPeakAngle; d.Fx[MoonPh] = EvalSky(MoonPh) ?? r.moonPhase;
                }
                break;
            case "Camera Shake":
                var sh = EvalShake();
                d.ShakeOn = true; d.Shake = sh.Amp > 0 ? sh.Amp : 0.3f; d.Hz = sh.Hz > 0 ? sh.Hz : 3;
                d.RotPct = sh.Amp > 0 ? sh.Rot * 100 : 100; d.PosPct = sh.Amp > 0 ? sh.Pos * 100 : 100; d.RollPct = sh.Amp > 0 ? sh.Roll * 100 : 20; d.Smoothness = sh.Amp > 0 ? sh.Smooth * 100 : 80;
                break;
            case "Follow cam": case "POV":
                if (HankModPack.ReplayCameraMod.Instance is { } rc0)
                    foreach (var (l, v) in rc0.CameraSnapshot()) if (InTab(l, tab)) d.Fx[l] = EvalSky(l) ?? v;
                break;
            default:
                foreach (var p in fx.Where(p => GroupOfFx(p.Label) == tab)) { try { d.Fx[p.Label] = FxRead(p); } catch { } }
                break;
        }
        return d;
    }

    void CopyTab(string tab)
    {
        var k = Selected();
        var src = k != null && HasTab(k.D, tab) ? k.D : LiveData(tab);
        clipboard[tab] = Capture(src, tab); clipFrom[tab] = k != null && ReferenceEquals(src, k.D) ? k.Frame : curFrame;
        Log($"TLFX: copy pressed on '{tab}', playhead {curFrame:0}, {clipboard[tab].Fx.Count} effect value(s) stored");
        Log($"TLFX: '{tab}' copied ({(k != null && ReferenceEquals(src, k.D) ? $"keyframe at frame {k.Frame}" : "values at the playhead")})");
    }

    void PasteTab(string tab)
    {
        if (!clipboard.TryGetValue(tab, out var c)) { Log($"TLFX: nothing copied for '{tab}' yet"); return; }
        var k = SelOrCreate();
        Paste(k.D, tab, c);
        SyncKeyframes(); dirty = true; previewTod = -1; previewShake = null;
        Log($"TLFX: paste '{tab}': {c.Fx.Count} effect value(s), keyframe at frame {k.Frame}, playhead {curFrame:0}");
        if (tab != "Time of day" && tab != "Camera Shake") { fxPreviewUntil = 0; foreach (var (l, v) in c.Fx) { var q = fx.FirstOrDefault(x => x.Label == l); if (q != null) { try { FxWrite(q, v); } catch { } } } PushFx(true); }
        Log($"TLFX: '{tab}' pasted at frame {k.Frame}");
    }

    /// <summary>
    /// Copies this tab of the keyframe at the playhead to the next game keyframe ("Next Frame", or a little later when there is none),
    /// or to every game keyframe created after it ("All").
    /// </summary>
    void DuplicateTab(string tab, bool all)
    {
        var src = Selected();
        var copy = Capture(src != null && HasTab(src.D, tab) ? src.D : LiveData(tab), tab);
        var from = src?.Frame ?? (int)curFrame;
        var total = Math.Max(TotalFrames(), 1);
        var tol = Math.Max(2f, total * 0.008f);
        // The keyframes that already exist after this one: the game's camera keyframes and the effect keyframes (those two lists are separate).
        var later = new List<int>();
        foreach (var f in GameKeyframes().Select(g => g.rwi).Concat(keys.Where(o => !ReferenceEquals(o, src)).Select(o => o.Frame)).Where(f => f > from + tol).OrderBy(f => f))
            if (later.Count == 0 || f - later[^1] > tol) later.Add(f);
        Log($"TLFX: duplicate '{tab}' ({(all ? "all" : "next")}) from frame {from}: {later.Count} existing keyframe(s) after it ({string.Join(", ", later)})");
        var targets = new List<int>();
        if (all && later.Count > 0) targets.AddRange(later);
        else targets.Add(later.Count > 0 ? later[0] : (int)Math.Min(from + Math.Max(10f, total * 0.05f), total));
        foreach (var target in targets)
        {
            var k = keys.FirstOrDefault(o => !ReferenceEquals(o, src) && Math.Abs(o.Frame - target) <= tol);
            if (k == null) { k = new FxKey { Frame = target }; keys.Add(k); }
            Paste(k.D, tab, copy);
        }
        SyncKeyframes(); dirty = true;
        Log($"TLFX: '{tab}' duplicated from frame {from} to {targets.Count} keyframe(s) ({(all ? "all" : "next frame")}): {string.Join(", ", targets)}");
    }

    /// <summary>Puts the tab's values of the keyframe under the playhead back to their defaults (the keyframe stays).</summary>
    void ResetTab(string tab, IEnumerable<FxParam>? members)
    {
        var k = Selected();
        if (k == null) { Log($"TLFX: no effect keyframe at the playhead to reset ('{tab}')"); return; }
        switch (tab)
        {
            case "Time of day": k.D.TodVal = 0.5f; foreach (var label in k.D.Fx.Keys.Where(l => InTab(l, tab)).ToList()) k.D.Fx.Remove(label); break;
            case "Camera Shake": k.D.Shake = 0.3f; k.D.Hz = 6; k.D.RotPct = 100; k.D.PosPct = 100; k.D.RollPct = 30; k.D.Smoothness = 60; break;
            case "Follow cam": case "POV":
                foreach (var label in k.D.Fx.Keys.Where(l => InTab(l, tab)).ToList()) k.D.Fx.Remove(label);
                EnsureCam(k, tab);
                break;
            default:
                foreach (var m in members ?? Enumerable.Empty<FxParam>())
                    if (k.D.Fx.ContainsKey(m.Label)) { k.D.Fx[m.Label] = m.Default; try { FxWrite(m, m.Default); } catch { } }
                fxPreviewUntil = Unity.Time + 1.5f; PushFx();
                break;
        }
        previewTod = -1; previewShake = null;
        SyncKeyframes(); dirty = true;
        Log($"TLFX: '{tab}' values reset at frame {k.Frame}");
    }

    /// <summary>Removes every effect keyframe of the clip and gives the image effects back to the game.</summary>
    void RemoveAllEffectKeyframes()
    {
        keys.Clear();
        SyncKeyframes();
        RestoreFx();
        previewTod = -1; previewShake = null; fxPreviewUntil = 0;
        dirty = true;
        Log("TLFX: all effect keyframes removed");
    }

    /// <summary>Tints a tab's header with the colour of its keyframe icon, so tabs and timeline icons match.</summary>
    /// <summary>The Save / Load / Delete drop-downs of the keyframe panel.</summary>
    void SaveLoadRows(RawFormMenu menu, Func<Action, nint> AC, Func<Func<string>, nint> GS, Func<Action<string>, nint> SS)
    {
        string Cycle(List<string> list, ref int index, int step)
        {
            if (list.Count == 0) { index = 0; return ""; }
            index = ((index + step) % list.Count + list.Count) % list.Count;
            return list[index];
        }
        string WhatLabel() => saveWhat switch { "Game" => "Game keyframes", "FX" => "FX keyframes", _ => "Game + FX keyframes" };

        menu.Accordion("Save keyframes", AC(() =>
        {
            menu.Info(GS(() => $"1. What to save: {WhatLabel()}"), false, null);
            menu.Horizontal(AC(() =>
            {
                menu.Button("Game", AC(() => saveWhat = "Game"));
                menu.Button("FX", AC(() => saveWhat = "FX"));
                menu.Button("Both", AC(() => saveWhat = "Both"));
            }));
            menu.Info(GS(() => $"2. Name (map: {MapName()})"), false, null);
            menu.TextInput("Name", GS(() => saveName.Length == 0 ? DefaultPresetName() : saveName), SS(v => saveName = v), "Preset name");
            menu.Button("3. Save", AC(() => { SavePresetNamed(saveWhat, saveName); saveName = ""; }));
        }), false);

        // Load / Delete are real drop-downs: one button per saved preset (the lists are read when the panel opens).
        var mine = PresetNames(MapName());
        menu.Accordion("Load keyframes", AC(() =>
        {
            if (mine.Count == 0) menu.Info(GS(() => $"No saved keyframes for {MapName()} yet."), false, null);
            foreach (var name in mine)
            {
                var n = name;
                menu.Button(n, AC(() => LoadPresetNamed(n)));
            }
        }), false);

        var all = AllPresets();
        menu.Accordion("Delete keyframes (all maps)", AC(() =>
        {
            menu.Info(GS(() => armedDelete is { } a ? $"Click again to delete: {a.Map} / {a.Name}" : "Click a preset to delete it (twice to confirm)."), false, null);
            foreach (var (map, name) in all)
            {
                var m = map; var n = name;
                menu.Button($"{m} / {n}", AC(() =>
                {
                    if (armedDelete is { } a && a.Map == m && a.Name == n) { DeletePresetNamed(m, n); armedDelete = null; }
                    else armedDelete = (m, n);
                }));
            }
        }), false);
    }
    (string Map, string Name)? armedDelete;

    /// <summary>The tab's buttons, laid out like the game's own: Add, then Copy | Paste and Duplicate | Delete.</summary>
    void TabToggle(RawFormMenu menu, Func<Func<bool>, nint> GB, Func<Action<bool>, nint> SB, string tab, IEnumerable<FxParam>? members)
    {
        var bit = Array.FindIndex(TabColors, t => t.Tab == tab);
        menu.hhs("Use at this keyframe", GB(() => Selected() is { } k && bit >= 0 && (TabMask(k.D) & (1 << bit)) != 0),
            SB(v => { if (v) AddTab(tab, members); else ClearTab(tab); }), false);
    }

    void EditButtons(RawFormMenu menu, Func<Action, nint> AC, Func<Func<string>, nint> GS, string tab, IEnumerable<FxParam>? members)
    {
        menu.Button("Add Keyframe", AC(() => AddTab(tab, members)));
        menu.Button("Copy", AC(() => CopyTab(tab)));
        menu.Button("Paste", AC(() => PasteTab(tab)));
        menu.Accordion("Duplicate", AC(() =>
        {
            menu.Button("Next Frame", AC(() => DuplicateTab(tab, false)));
            menu.Button("All", AC(() => DuplicateTab(tab, true)));
        }), false);
        menu.Button("Delete", AC(() => ClearTab(tab)));
        menu.Button("Reset values", AC(() => ResetTab(tab, members)));
    }

    /// <summary>Removes only one tab's options from the keyframe under the playhead; the keyframe disappears once it has none left.</summary>
    void ClearTab(string tab)
    {
        var k = Selected();
        if (k == null) return;
        switch (tab)
        {
            case "Time of day": k.D.Tod = false; foreach (var label in k.D.Fx.Keys.Where(l => InTab(l, tab)).ToList()) k.D.Fx.Remove(label); break;
            case "Camera Shake": k.D.ShakeOn = false; k.D.Shake = 0; break;
            default:
                foreach (var label in k.D.Fx.Keys.Where(l => InTab(l, tab)).ToList()) k.D.Fx.Remove(label);
                break;
        }
        previewTod = -1; previewShake = null; fxPreviewUntil = 0;
        if (TabMask(k.D) == 0) keys.Remove(k);
        SyncKeyframes();
        dirty = true;
        Log($"TLFX: '{tab}' removed from the effect keyframe at frame {k.Frame}");
    }

    void DeleteHere()
    {
        var k = Selected();
        if (k == null) return;
        keys.Remove(k);
        SyncKeyframes();
        dirty = true; previewTod = -1; previewShake = null; fxPreviewUntil = 0;
        Log($"TLFX: effect keyframe at frame {k.Frame} deleted");
    }

    // ================= evaluation =================

    float curFrame;
    float CurFrame => curFrame;

    /// <summary>Value of a sky option (sun direction / height, moon phase) at the playhead, blended between the keyframes that have it.</summary>
    float? EvalSky(string label)
    {
        var k = ordered.Where(o => o.D.Fx.ContainsKey(label)).ToArray();
        if (k.Length == 0) return null;
        var f = CurFrame;
        if (f < k[0].Frame) return null;
        if (label is "Cam/FollowOn" or "Cam/PovOn" or "Cam/Mode")
        {
            // The on / off switches of the cameras are not blended: the value holds from a keyframe until the next one.
            // (Every other camera value, FOV, distance, angle... is blended smoothly between keyframes, like the effects.)
            var held = k[0].D.Fx[label];
            foreach (var kk in k) { if (kk.Frame <= f) held = kk.D.Fx[label]; else break; }
            return held;
        }
        if (k.Length == 1 || f >= k[^1].Frame) return k[^1].D.Fx[label];
        for (var i = 0; i < k.Length - 1; i++)
        {
            if (f > k[i + 1].Frame) continue;
            var span = k[i + 1].Frame - k[i].Frame;
            var u = span <= 0 ? 1 : (f - k[i].Frame) / span;
            if (Smooth) u = u * u * (3 - 2 * u);
            return k[i].D.Fx[label] + (k[i + 1].D.Fx[label] - k[i].D.Fx[label]) * u;
        }
        return k[^1].D.Fx[label];
    }

    // ================= replay camera as keyframes =================

    /// <summary>Gives the keyframe the full set of camera values (so they blend with the neighbouring keyframes).</summary>
    void EnsureCam(FxKey k, string tab)
    {
        if (HankModPack.ReplayCameraMod.Instance is not { } rc) return;
        foreach (var (l, v) in rc.CameraSnapshot())
        {
            if (!InTab(l, tab) || k.D.Fx.ContainsKey(l)) continue;
            k.D.Fx[l] = l is "Cam/FollowOn" or "Cam/PovOn" ? 1 : EvalSky(l) ?? v;
        }
        dirty = true;
    }

    /// <summary>A camera value at the playhead: blended between the keyframes that have it, null when the clip has none (then the setting applies).</summary>
    public static float? CameraKey(string label) => current is { Enabled: true, inReplay: true } ? current.EvalSky(label) : null;

    /// <summary>The camera mode at the playhead (0 off, 1 follow cam, 2 POV): the last keyframe before the playhead, no blending.</summary>
    public static int? CameraModeKey() => current is { Enabled: true, inReplay: true } ? current.EvalCamMode() : null;

    /// <summary>The Time Scale keyed in the replay's own (game) keyframes at the playhead: null when none is keyed (before the first one too), the last value after the last one.</summary>
    public static float? GameTimeScaleKey() => current is { Enabled: true, inReplay: true } ? current.EvalGameTimeScale() : null;

    List<(float F, float V)>? tsPts; long tsScanAt;
    float? EvalGameTimeScale()
    {
        var now = Environment.TickCount64;
        if (tsPts == null || now - tsScanAt > 250)
        {
            tsScanAt = now;
            try { tsPts = GameKeyframes().Where(k => k.rws || MathF.Abs(k.rwr - 1f) > 0.001f).Select(k => ((float)k.rwi, k.rwr)).OrderBy(t => t.Item1).ToList(); }
            catch { tsPts = new(); }
        }
        if (tsPts.Count == 0) return null;
        var f = CurFrame;
        if (f < tsPts[0].F) return null;
        for (var i = 0; i < tsPts.Count - 1; i++)
        {
            var (f0, v0) = tsPts[i]; var (f1, v1) = tsPts[i + 1];
            if (f >= f0 && f <= f1) return f1 > f0 ? v0 + (v1 - v0) * (f - f0) / (f1 - f0) : v1;
        }
        return tsPts[^1].V;
    }

    int? EvalCamMode()
    {
        // Each tab has its own on/off value (Cam/FollowOn, Cam/PovOn); "Cam/Mode" is the old shared value of earlier saves.
        var ks = ordered.Where(o => o.D.Fx.ContainsKey("Cam/FollowOn") || o.D.Fx.ContainsKey("Cam/PovOn") || o.D.Fx.ContainsKey("Cam/Mode")).ToArray();
        if (ks.Length == 0) return null;
        var f = CurFrame;
        if (f < ks[0].Frame) return null;
        KfData? last = null;
        foreach (var k in ks) { if (k.Frame <= f) last = k.D; else break; }
        if (last == null) return null;
        var hasP = last.Fx.TryGetValue("Cam/PovOn", out var p);
        var hasF = last.Fx.TryGetValue("Cam/FollowOn", out var fo);
        if (hasP && p > 0.5f) return 2;
        if (hasF && fo > 0.5f) return 1;
        if (hasP || hasF) return 0;   // an explicit Off wins over the old shared value
        return last.Fx.TryGetValue("Cam/Mode", out var m) ? (int)Math.Round(m) : 0;
    }

    bool skySaved; float savedSunDir, savedSunHeight, savedMoon;

    void ApplySky(Jibbers.Interop.Game.EnvironmentManager env)
    {
        var d = EvalSky(SunDir); var h = EvalSky(SunHeight); var m = EvalSky(MoonPh);
        if (d == null && h == null && m == null) { RestoreSky(env); return; }
        if (!skySaved) { skySaved = true; savedSunDir = env.sunYAngle; savedSunHeight = env.sunPeakAngle; savedMoon = env.moonPhase; Log("TLFX: taking over the sun / moon"); }
        if (d is { } dv) env.sunYAngle = dv;
        if (h is { } hv) env.sunPeakAngle = hv;
        if (m is { } mv) env.moonPhase = mv;
    }

    void RestoreSky(Jibbers.Interop.Game.EnvironmentManager env)
    {
        if (!skySaved) return;
        skySaved = false;
        env.sunYAngle = savedSunDir; env.sunPeakAngle = savedSunHeight; env.moonPhase = savedMoon;
        Log("TLFX: gave the sun / moon back");
    }

    float? EvalTod()
    {
        var k = ordered.Where(o => o.D.Tod).ToArray();
        if (k.Length == 0) return null;
        var f = CurFrame;
        if (f < k[0].Frame) return null;                       // like the game's keyframes: nothing happens before the first one
        if (k.Length == 1 || f >= k[^1].Frame) return k[^1].D.TodVal;
        for (var i = 0; i < k.Length - 1; i++)
        {
            if (f > k[i + 1].Frame) continue;
            var span = k[i + 1].Frame - k[i].Frame;
            var u = span <= 0 ? 1 : (f - k[i].Frame) / span;
            if (Smooth) u = u * u * (3 - 2 * u);
            return k[i].D.TodVal + (k[i + 1].D.TodVal - k[i].D.TodVal) * u;
        }
        return k[^1].D.TodVal;
    }

    struct Shk { public float Amp, Hz, Rot, Pos, Roll, Smooth; }

    static Shk FromData(KfData d) => new() { Amp = d.Shake, Hz = d.Hz, Rot = d.RotPct / 100f, Pos = d.PosPct / 100f, Roll = d.RollPct / 100f, Smooth = d.Smoothness / 100f };

    static Shk Lerp(Shk a, Shk b, float u) => new()
    {
        Amp = a.Amp + (b.Amp - a.Amp) * u, Hz = a.Hz + (b.Hz - a.Hz) * u, Rot = a.Rot + (b.Rot - a.Rot) * u,
        Pos = a.Pos + (b.Pos - a.Pos) * u, Roll = a.Roll + (b.Roll - a.Roll) * u, Smooth = a.Smooth + (b.Smooth - a.Smooth) * u,
    };

    Shk EvalShake()
    {
        if (previewShake != null && Unity.Time < previewUntil) return FromData(previewShake);
        var k = ordered.Where(o => o.D.ShakeOn).ToArray();
        if (k.Length == 0) return default;
        var f = CurFrame;
        if (f < k[0].Frame) return default;
        if (k.Length == 1 || f >= k[^1].Frame) return FromData(k[^1].D);
        for (var i = 0; i < k.Length - 1; i++)
        {
            if (f > k[i + 1].Frame) continue;
            var span = k[i + 1].Frame - k[i].Frame;
            var u = span <= 0 ? 1 : (f - k[i].Frame) / span;
            return Lerp(FromData(k[i].D), FromData(k[i + 1].D), u);
        }
        return default;
    }



    float nextSunLog;

    /// <summary>
    /// The game positions the sun from time of day every frame, so the sun light itself is turned after the game's update:
    /// its yaw becomes the keyed Sun direction, its elevation is scaled by keyed height / original peak height.
    /// </summary>
    unsafe void ApplySunLight()
    {
        if (!inReplay || !Enabled) return;
        var d = EvalSky(SunDir); var h = EvalSky(SunHeight);
        if (d == null && h == null) return;
        if (EnvManager.First() is not { } e) return;
        var env = e.Raw;
        var light = env.qdx.Ptr;
        if (light == 0 || Unity.IsNull(light)) return;
        var tr = Unity.Transform(light);
        var euler = Unity.Call<Vector3>(tr, "get_eulerAngles");
        var before = euler;
        if (d is { } dv) euler.Y = dv;
        if (h is { } hv && skySaved && Math.Abs(savedSunHeight) > 0.5f)
        {
            var x = euler.X > 180f ? euler.X - 360f : euler.X;
            x *= hv / savedSunHeight;
            euler.X = x < 0 ? x + 360f : x;
        }
        Unity.Call(tr, "set_eulerAngles", (nint)(&euler));
        if (Unity.Time > nextSunLog)
        {
            nextSunLog = Unity.Time + 3f;
            Log($"TLFX: sun: fields yaw={env.sunYAngle:0.#} peak={env.sunPeakAngle:0.#} (original {savedSunDir:0.#}/{savedSunHeight:0.#}); light euler {before.X:0.#},{before.Y:0.#} -> {euler.X:0.#},{euler.Y:0.#}");
        }
    }



    // ================= the play cameras, in the in-game camera menu (F3) =================

    readonly HashSet<string> playTitlesLogged = new();
    readonly Dictionary<nint, float> playInjectedAt = new();

    /// <summary>
    /// Outside the replay: only writes the title of each menu the game opens to the log (so the F3 camera menu can be identified).
    /// It does NOT touch the menus: their pointers can outlive the scene, and reading one crashed the game while a map was loading.
    /// The play camera tabs (<see cref="AddPlayCameraRows"/>) stay switched off until the menu to put them in is known; the camera editor
    /// (D-pad down + Share) replaces them.
    /// </summary>
    void InjectPlayMenus()
    {
        if (inReplay || menuTitle.Count == 0) return;
        foreach (var title in menuTitle.Values.ToList())
            if (playTitlesLogged.Add(title)) Log($"TLFX: menu '{title}' opened while playing");
    }

    void AddPlayCameraRows(nint menuPtr, string where)
    {
        InitDelegateClasses();
        var pc = HankModPack.PlayCamerasMod.Instance!;
        var menu = new RawFormMenu(menuPtr);
        var getF = (nint)(delegate* unmanaged<nint, nint, float>)&GetF;
        var getB = (nint)(delegate* unmanaged<nint, nint, byte>)&GetB;
        var getS = (nint)(delegate* unmanaged<nint, nint, nint>)&GetS;
        var setF = (nint)(delegate* unmanaged<nint, float, nint, void>)&SetF;
        var setB = (nint)(delegate* unmanaged<nint, byte, nint, void>)&SetB;
        var runAct = (nint)(delegate* unmanaged<nint, nint, void>)&RunAct;
        nint GF(Func<float> g) => MakeDelegate(funcFloat!.Value, getF, new Binding { GetF = g });
        nint SF(Action<float> s) => MakeDelegate(actionFloat!.Value, setF, new Binding { SetF = s });
        nint GB(Func<bool> g) => MakeDelegate(funcBool!.Value, getB, new Binding { GetB = g });
        nint SB(Action<bool> s) => MakeDelegate(actionBool!.Value, setB, new Binding { SetB = s });
        nint GS(Func<string> g) => MakeDelegate(funcString!.Value, getS, new Binding { GetS = g });
        nint AC(Action a) => MakeDelegate(actionPlain!.Value, runAct, new Binding { Act = a });
        void Put(string key, System.Text.Json.Nodes.JsonNode v) { try { pc.SetSetting(key, v); } catch (Exception e) { LogOnce("play-set", e); } }

        menu.Header("Hank Mod Pack: play cameras");
        menu.Info(GS(() => $"Camera in use: {pc.CurrentName}. Opening a tab switches to that camera; every change is live."), false, null);
        menu.hhs("Game camera (no play camera)", GB(() => pc.CurrentName == "Game camera"), SB(v => { if (v) pc.Select("Game camera"); }), false);

        foreach (var (cam, section) in new[] { ("POV", "POV"), ("Follow cam", "Follow cam"), ("Drone", "Drone") })
        {
            menu.Accordion($"Camera: {cam}", AC(() =>
            {
                // Opening the tab shows this camera in the game while its settings are edited (not while the menu is still being built).
                if (playInjectedAt.TryGetValue(menuPtr, out var at) && Unity.Time - at > 0.5f && pc.CurrentName != cam)
                {
                    pc.Select(cam);
                    Log($"TLFX: play camera tab opened: {cam}");
                }
                menu.hhs($"Use the {cam} camera", GB(() => pc.CurrentName == cam), SB(v => pc.Select(v ? cam : "Game camera")), false);
                foreach (var st in pc.Settings.Where(x => x.Section == section).ToList())
                {
                    var s = st;
                    switch (s.Kind)
                    {
                        case SettingKind.Toggle:
                            menu.hhs(s.Label, GB(() => pc.GetBool(s.Key)), SB(v => Put(s.Key, System.Text.Json.Nodes.JsonValue.Create(v)!)), false);
                            break;
                        case SettingKind.Slider:
                            var dec = s.Step >= 1 ? 0 : s.Step >= 0.1 ? 1 : 2;
                            menu.Slider(s.Label, (float)s.Min, (float)s.Max, GF(() => (float)pc.GetDouble(s.Key)), SF(v => Put(s.Key, System.Text.Json.Nodes.JsonValue.Create((double)v)!)), (float)Math.Max(s.Step, 0.0001), dec);
                            break;
                    }
                }
            }), false);
        }
        Log($"TLFX: play camera tabs added to the menu '{where}'");
    }

    // ================= Alpine Vision: live controls for the other mod =================

    // Alpine Vision settings changed during a replay (window or keyframe panel): the value it had before, put back when the replay ends.
    readonly Dictionary<string, System.Text.Json.Nodes.JsonNode> alpOrig = new();
    JibbersMod? alpMod;
    readonly HankModPack.BackgroundActions alpineChanges = new();

    void RememberAlp(JibbersMod mod, string key)
    {
        try
        {
            if (alpOrig.ContainsKey(key)) return;
            var s = mod.Settings.FirstOrDefault(x => x.Key == key);
            if (s == null) return;
            alpMod = mod;
            alpOrig[key] = s.Kind switch
            {
                SettingKind.Toggle => System.Text.Json.Nodes.JsonValue.Create(mod.GetBool(key))!,
                SettingKind.Slider => System.Text.Json.Nodes.JsonValue.Create(mod.GetDouble(key))!,
                _ => System.Text.Json.Nodes.JsonValue.Create(mod.GetString(key))!,
            };
        }
        catch (Exception e) { LogOnce("alpine-remember", e); }
    }

    void RestoreAlp()
    {
        if (alpOrig.Count == 0 || alpMod == null) return;
        var mod = alpMod; var list = alpOrig.ToList(); alpOrig.Clear();
        // SetSetting waits for the main thread, which is this one: sent from another thread.
        alpineChanges.Enqueue(() => { foreach (var (k, v) in list) { try { mod.SetSetting(k, v); } catch (Exception e) { LogOnce("alpine-restore", e); } } });
    }

    /// <summary>The Alpine Vision mod, if the player has it (found through the loader's list of mods).</summary>
    static JibbersMod? FindMod(string id)
    {
        try
        {
            var ctx = typeof(JibbersMod).Assembly.GetType("Jibbers.Sdk.ModContext");
            var all = ctx?.GetMethod("All", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)?.Invoke(null, null) as IEnumerable<JibbersMod>;
            return all?.FirstOrDefault(m => m.Id == id);
        }
        catch { return null; }
    }

    /// <summary>One tab with every setting of Alpine Vision, as sliders, switches and lists, changed live.</summary>
    void AlpineRows(RawFormMenu menu, Func<Action, nint> AC, Func<Func<float>, nint> GF, Func<Action<float>, nint> SF, Func<Func<bool>, nint> GB, Func<Action<bool>, nint> SB, Func<Func<string>, nint> GS)
    {
        var mod = FindMod("alpinevision");
        if (mod == null) { menu.Info(GS(() => "Alpine Vision is not loaded."), false, null); return; }
        void Put(string key, System.Text.Json.Nodes.JsonNode v) { RememberAlp(mod, key); alpineChanges.Enqueue(() => { try { mod.SetSetting(key, v); } catch (Exception e) { LogOnce("alpine-set", e); } }); }
        foreach (var st in mod.Settings.ToList())
        {
            var s = st;
            switch (s.Kind)
            {
                case SettingKind.Toggle:
                    menu.hhs(s.Label, GB(() => mod.GetBool(s.Key)), SB(v => Put(s.Key, System.Text.Json.Nodes.JsonValue.Create(v)!)), false);
                    break;
                case SettingKind.Slider:
                    var dec = s.Step >= 1 ? 0 : s.Step >= 0.1 ? 1 : 2;
                    menu.Slider(s.Label, (float)s.Min, (float)s.Max, GF(() => (float)mod.GetDouble(s.Key)), SF(v => Put(s.Key, System.Text.Json.Nodes.JsonValue.Create((double)v)!)), (float)Math.Max(s.Step, 0.0001), dec);
                    break;
                case SettingKind.Choice:
                    menu.Accordion(s.Label, AC(() =>
                    {
                        menu.Info(GS(() => $"Now: {mod.GetString(s.Key)}"), false, null);
                        foreach (var o in s.Options)
                        {
                            var opt = o;
                            menu.Button(opt, AC(() => Put(s.Key, System.Text.Json.Nodes.JsonValue.Create(opt)!)));
                        }
                    }), false);
                    break;
            }
        }
    }

    // ================= lights: the rider's headlamp and a light on the camera =================

    string lightMode = "Off";
    readonly List<(GameRef Ref, bool Was)> headLights = new();
    OwnedGameObject? camLight; nint camLightComp;
    float nextLampScan;

    string LightLabel() => lightMode switch { "Headlight" => "Headlight on", "CamLight" => "CamLight on", "Both" => "Headlight + CamLight on", _ => "Off (the game decides)" };

    void SetLights(string mode)
    {
        lightMode = mode;
        nextLampScan = 0;
        Log($"TLFX: lights set to {mode}");
    }

    unsafe void ApplyLights()
    {
        var wantHead = false;
        var wantCam = lightMode == "CamLight";

        if (wantHead)
        {
            if (Unity.Time > nextLampScan)
            {
                nextLampScan = Unity.Time + 1.5f;
                foreach (var light in Unity.FindObjects(Unity.Class("UnityEngine.Light")))
                {
                    if (Unity.IsNull(light) || headLights.Any(h => h.Ref.Target == light)) continue;
                    var mount = Unity.Call(Unity.Transform(light), "get_parent");
                    if (Unity.IsNull(mount) || Unity.Name(mount) != "Lamp") continue;
                    var head = Unity.Call(mount, "get_parent");
                    if (Unity.IsNull(head) || Unity.Name(head) != "QuickRigCharacter_Head") continue;
                    headLights.Add((new GameRef(light), Unity.Call<byte>(light, "get_enabled") != 0));
                    Log($"TLFX: found a headlamp ({headLights.Count})");
                }
            }
            var on = (byte)1;
            foreach (var h in headLights)
                if (!Unity.IsNull(h.Ref.Target)) Unity.Call(h.Ref.Target, "set_enabled", (nint)(&on));
        }
        else RestoreHeadLights();

        if (wantCam) EnsureCamLight(); else DisableCamLight();
    }

    unsafe void RestoreHeadLights()
    {
        if (headLights.Count == 0) return;
        foreach (var h in headLights)
        {
            try { if (!Unity.IsNull(h.Ref.Target)) { var was = (byte)(h.Was ? 1 : 0); Unity.Call(h.Ref.Target, "set_enabled", (nint)(&was)); } } catch { }
            try { h.Ref.Dispose(); } catch { }
        }
        headLights.Clear();
    }

    unsafe void EnsureCamLight()
    {
        var cam = ResolveCamera(0);
        if (cam == 0 || Unity.IsNull(cam)) return;
        var camTr = Unity.Transform(cam);
        if (camLight == null || camLightComp == 0 || Unity.IsNull(camLightComp))
        {
            camLight?.Dispose();
            camLight = OwnedGameObject.Create("Hank.CamLight");
            camLightComp = camLight.AddComponent("UnityEngine.Light");
            var type = 0; var intensity = 4f; var range = 80f; var angle = 100f;   // a wide spot light, like a camera lamp
            Unity.Call(camLightComp, "set_type", (nint)(&type));
            Unity.Call(camLightComp, "set_intensity", (nint)(&intensity));
            Unity.Call(camLightComp, "set_range", (nint)(&range));
            Unity.Call(camLightComp, "set_spotAngle", (nint)(&angle));
            Log("TLFX: CamLight created");
        }
        var myTr = camLight.Transform;
        if (Unity.Call(myTr, "get_parent") != camTr)
        {
            camLight.AttachTo(camTr);
            var zero = new Vector3(0, 0, 0); var id = Quaternion.Identity;
            Unity.Call(myTr, "set_localPosition", (nint)(&zero));
            Unity.Call(myTr, "set_localRotation", (nint)(&id));
        }
        var on = (byte)1;
        Unity.Call(camLightComp, "set_enabled", (nint)(&on));
    }

    unsafe void DisableCamLight()
    {
        if (camLightComp == 0 || Unity.IsNull(camLightComp)) return;
        var off = (byte)0;
        Unity.Call(camLightComp, "set_enabled", (nint)(&off));
    }

    void DropLights()
    {
        try { RestoreHeadLights(); } catch { }
        try { if (camLight != null && !Unity.IsNull(camLight.Target)) Unity.Destroy(camLight.Target); } catch { }
        try { camLight?.Dispose(); } catch { }
        camLight = null; camLightComp = 0;
    }

    // ================= time of day =================

    float CurrentEnvTime()
    {
        var env = EnvManager.First();
        return env is { } e ? e.Raw.dayNightT : 0;
    }

    void OnEnvUpdate(nint self)
    {
        frame++;
        if (environmentRoot?.Target != self)
        {
            envSaved = skySaved = false;
            environmentRoot?.Dispose(); environmentRoot = new GameRef(self);
        }
        var env = new EnvManager(self).Raw;

        if (!inReplay || !Enabled) { RestoreEnv(env); RestoreSky(env); if (camLight != null || headLights.Count > 0) DropLights(); return; }

        if (needInit)
        {
            needInit = false; maxSeen = 0; clipKey = "";
            Log($"TLFX: replay: dayNightT={env.dayNightT:0.###} fixedTime={env.fixedTime} updateTime={env.updateTime} timeFlowSpeed={env.timeFlowSpeed:0.###} nightStartT={env.nightStartT:0.###}");
        }
        if (frame % 20 == 0 || clipKey.Length == 0) RefreshClip();
        if (frame % 6 == 0) SyncKeyframes();

        playhead = ReadPlayhead();
        curFrame = Recorder.Instance.Raw.pxc;
        if (float.IsFinite(playhead) && playhead > maxSeen) maxSeen = playhead;

        if ((fx.Count == 0 || Unity.Time > nextFxScan) && Unity.Time > nextFxScan) { nextFxScan = Unity.Time + 2f; try { DiscoverFx(); } catch (Exception e) { LogOnce("fxscan", e); } }
        try { ApplyFx(); PushFx(); } catch (Exception e) { LogOnce("fxapply", e); }
        MaintainTrim();
        WatchGameKeys();
        dirty = false;   // nothing is written to disk by itself: only the "Save keyframes" button saves

        if (Unity.Time > nextDiag)
        {
            nextDiag = Unity.Time + 3f;
            var rec = Recorder.Instance.Raw;
            var clip = Recorder.Instance.Clip;
            var sh = EvalShake(); var a = sh.Amp; var hz = sh.Hz;
            Log($"TLFX: diag playhead={playhead:0.###} frame={CurFrame:0} (playhead*{(playhead > 0 ? CurFrame / playhead : 0):0.#}) keyframes={ordered.Length} (with time {ordered.Count(o => o.D.Tod)}, with shake {ordered.Count(o => o.D.ShakeOn)}, with image fx {ordered.Count(o => o.D.Fx.Count > 0)}, fx sliders {fx.Count}, total frames {TotalFrames()}) shakeNow={a:0.##}@{hz:0}Hz todNow={(EvalTod() is { } t ? t.ToString("0.00") : "-")} hits follow={hitFollow} recorder={hitRecorder} applied={hitApply} menusInjected={injectedCount} " +
                $"[maxFrames={rec.maxFrames} clip rvs={(clip.IsNull ? -1 : clip.Raw.rvs)} rvt={(clip.IsNull ? -1 : clip.Raw.rvt)} pxc={rec.pxc} pxd={rec.pxd} pxm={rec.pxm} pyp={rec.pyp}]");
        }

        float? wanted = previewTod >= 0 && Unity.Time < previewUntil ? previewTod : EvalTod();
        if (wanted is { } w)
        {
            if (!envSaved)
            {
                envSaved = true;
                savedT = env.dayNightT; savedSpeed = env.timeFlowSpeed; savedFixed = env.fixedTime; savedUpdate = env.updateTime;
                Log("TLFX: taking over the time of day");
            }
            env.fixedTime = false;
            env.updateTime = true;
            env.timeFlowSpeed = 0;
            env.dayNightT = Math.Clamp(w, 0, 1);
        }
        else RestoreEnv(env);
        try { ApplySky(env); } catch (Exception e) { LogOnce("sky", e); }
        try { ApplyLights(); } catch (Exception e) { LogOnce("lights", e); }
    }

    void RestoreEnv(Jibbers.Interop.Game.EnvironmentManager env)
    {
        if (!envSaved) return;
        envSaved = false;
        env.timeFlowSpeed = savedSpeed;
        env.fixedTime = savedFixed;
        env.updateTime = savedUpdate;
        env.dayNightT = savedT;
        Log("TLFX: gave the time of day back");
    }

    // ================= camera shake =================

    static Quaternion Mul(Quaternion a, Quaternion b) => new(
        a.W * b.X + a.X * b.W + a.Y * b.Z - a.Z * b.Y,
        a.W * b.Y - a.X * b.Z + a.Y * b.W + a.Z * b.X,
        a.W * b.Z + a.X * b.Y - a.Y * b.X + a.Z * b.W,
        a.W * b.W - a.X * b.X - a.Y * b.Y - a.Z * b.Z);

    static Quaternion Inv(Quaternion q) => new(-q.X, -q.Y, -q.Z, q.W);

    static Quaternion Euler(float xDeg, float yDeg, float zDeg)
    {
        const float h = MathF.PI / 360f;
        var (sx, cx) = MathF.SinCos(xDeg * h);
        var (sy, cy) = MathF.SinCos(yDeg * h);
        var (sz, cz) = MathF.SinCos(zDeg * h);
        return Mul(Mul(new Quaternion(0, sy, 0, cy), new Quaternion(sx, 0, 0, cx)), new Quaternion(0, 0, sz, cz));
    }

    static Vector3 Rotate(Quaternion q, Vector3 v)
    {
        var u = new Vector3(q.X, q.Y, q.Z);
        var dotUv = u.X * v.X + u.Y * v.Y + u.Z * v.Z;
        var dotUu = u.X * u.X + u.Y * u.Y + u.Z * u.Z;
        var cross = new Vector3(u.Y * v.Z - u.Z * v.Y, u.Z * v.X - u.X * v.Z, u.X * v.Y - u.Y * v.X);
        return u * (2 * dotUv) + v * (q.W * q.W - dotUu) + cross * (2 * q.W);
    }

    /// <summary>Smooth wandering noise. smooth = 1 keeps only the slow wave, 0 adds fast jitter.</summary>
    static float Noise(float phase, float seed, float smooth, float rate = 1f)
    {
        // Slow wandering made of a few incommensurate waves, like a hand-held camera; "smooth" removes the faster layers.
        var rough = 1f - Math.Clamp(smooth, 0, 1);
        var p = phase * rate;
        var v = 0.62f * MathF.Sin(p + seed) + 0.28f * MathF.Sin(p * 0.47f + seed * 3.1f) + 0.10f * MathF.Sin(p * 0.19f + seed * 5.3f)
              + rough * (0.35f * MathF.Sin(p * 2.3f + seed * 1.7f) + 0.15f * MathF.Sin(p * 4.1f + seed * 2.9f));
        return v / (1f + rough * 0.5f);
    }

    nint camCache;

    /// <summary>Camera.main is null in the replay, so look for the camera the game really uses.</summary>
    nint ResolveCamera(nint followSelf)
    {
        if (camCache != 0 && !Unity.IsNull(camCache) && Unity.Call<byte>(camCache, "get_enabled") != 0) return camCache;
        camCache = 0;
        var k = Unity.Class("UnityEngine.Camera");
        nint found = 0; string how = "";
        if (followSelf != 0 && !Unity.IsNull(followSelf))
        {
            found = Unity.GetComponent(followSelf, k); how = "on the follow camera object";
            if (found == 0) { var c = Unity.GetComponentsInChildren(followSelf, k, true); if (c.Length > 0) { found = c[0]; how = "in the follow camera's children"; } }
        }
        if (found == 0)
        {
            var main = Unity.MainCamera;
            if (main != 0 && !Unity.IsNull(main)) { found = main; how = "Camera.main"; }
        }
        if (found == 0)
        {
            float best = float.MinValue;
            foreach (var c in Unity.FindObjects(k))
            {
                if (Unity.IsNull(c) || Unity.Call<byte>(c, "get_enabled") == 0) continue;
                if (Unity.Call(c, "get_targetTexture") != 0) continue;
                var d = Unity.Call<float>(c, "get_depth");
                if (d > best) { best = d; found = c; how = "highest-depth enabled camera"; }
            }
        }
        if (found != 0) { cameraRoots.Keep(found); camCache = found; Log($"TLFX: shaking camera '{Unity.Name(found)}' (found {how})"); }
        return found;
    }

    /// <summary>Takes back last frame's offset, so the game's own camera code never sees it.</summary>
    void UndoShake()
    {
        if (lastCam == 0) return;
        var cam = lastCam; lastCam = 0;
        if (Unity.IsNull(cam)) return;
        var tr = Unity.Transform(cam);
        var pos = Unity.Call<Vector3>(tr, "get_position") - lastPosOffset;
        var rot = Mul(Unity.Call<Quaternion>(tr, "get_rotation"), Inv(lastRotOffset));
        Unity.Call(tr, "set_position", (nint)(&pos));
        Unity.Call(tr, "set_rotation", (nint)(&rot));
    }

    bool camLogged;

    void ApplyShake(string via, nint followSelf = 0)
    {
        if (!inReplay || !Enabled) return;
        hitApply++;
        var sh = EvalShake();
        var hz = sh.Hz;
        var amp = MathF.Pow(Math.Clamp(sh.Amp, 0f, 1f), 1.4f);   // gentle curve: the low end of the slider stays subtle, the top end is strong
        if (amp <= 0.00005f) return;
        var cam = ResolveCamera(followSelf);
        if (cam == 0) { if (logged.Add("nocam")) Log("TLFX: no camera found during the replay"); return; }

        var dt = Math.Min(Unity.DeltaTime, 0.1f);
        phase += dt * hz * 0.35f * 2 * MathF.PI;   // "Speed" 3 = about one wander per second
        var rotDeg = (float)RotationDeg * amp * sh.Rot;
        var posM = (float)PositionM * amp * sh.Pos;
        var rollDeg = (float)RotationDeg * amp * sh.Roll;
        var rotOff = Euler(Noise(phase, 1.3f, sh.Smooth, 1.00f) * rotDeg, Noise(phase, 4.1f, sh.Smooth, 0.83f) * rotDeg, Noise(phase, 7.9f, sh.Smooth, 0.61f) * rollDeg);
        var localPos = new Vector3(Noise(phase, 2.2f, sh.Smooth, 0.72f) * posM, Noise(phase, 5.5f, sh.Smooth, 0.9f) * posM, Noise(phase, 8.8f, sh.Smooth, 0.55f) * posM * 0.5f);

        var tr = Unity.Transform(cam);
        var rot = Unity.Call<Quaternion>(tr, "get_rotation");
        var pos = Unity.Call<Vector3>(tr, "get_position");
        var worldOff = Rotate(rot, localPos);
        var newRot = Mul(rot, rotOff);
        var newPos = pos + worldOff;
        Unity.Call(tr, "set_position", (nint)(&newPos));
        Unity.Call(tr, "set_rotation", (nint)(&newRot));

        lastCam = cam; lastPosOffset = worldOff; lastRotOffset = rotOff;
        if (!camLogged) { camLogged = true; Log($"TLFX: camera shake is applying via {via} (amp {amp:0.##}, {hz:0}Hz, camera {Unity.Name(cam)})"); }
    }

    // ================= image effects (the game's Camera Effects sliders) =================

    sealed class FxParam { public string Label = ""; public nint Get, Set, Row; public int Idx; public bool Stale, IsInt; public string[]? Options; public float Min, Max, Default; }
    readonly List<GameRef> fxKeep = new();
    nint fxList; int fxListCount; float fxHoldUntil;

    void DropFx() { foreach (var kv in fxOrig) fxPending.TryAdd(kv.Key, kv.Value); foreach (var q in fx) q.Stale = true; fx.Clear(); fxOrig.Clear(); lastFx.Clear(); fxMenuSeen = 0; fxLastCount = -1; fxList = 0; foreach (var g in fxKeep) { try { g.Dispose(); } catch { } } fxKeep.Clear(); }

    /// <summary>The Camera Effects sliders are still the ones we found (the menu was not rebuilt behind our back).</summary>
    bool FxListIntact()
    {
        if (fx.Count == 0 || fxList == 0) return false;
        try
        {
            if (Unity.Call<int>(fxList, "get_Count") != fxListCount) return false;
            foreach (var p in new[] { fx[0], fx[^1] }) { var i = p.Idx; if (Unity.Call(fxList, "get_Item", (nint)(&i)) != p.Row) return false; }
            return true;
        }
        catch { return false; }
    }
    readonly List<FxParam> fx = new();
    readonly Dictionary<string, float> fxOrig = new(), lastFx = new();
    nint fxMenuSeen; int fxLastCount = -1;
    float nextFxScan, fxPreviewUntil;

    float FxRead(FxParam p) { if (p.Stale) return 0; var r = Delegates.Invoke(p.Get); return r == 0 ? 0 : p.IsInt ? Il2Cpp.Unbox<int>(r) : Il2Cpp.Unbox<float>(r); }
    /// <summary>Every write remembers the value the game had first, so the end of the replay can put it back (the window and the keyframe panel write too).</summary>
    void FxWrite(FxParam p, float v)
    {
        if (p.Stale) return;
        if (!fxOrig.ContainsKey(p.Label)) { try { fxOrig[p.Label] = FxRead(p); } catch { } }
        FxWriteRaw(p, v);
    }
    /// <summary>Originals that could not be restored when the replay ended (the menu was already gone): put back as soon as the sliders are found again.</summary>
    readonly Dictionary<string, float> fxPending = new();
    void FxWriteRaw(FxParam p, float v) { if (p.Stale) return; if (p.IsInt) { var iv = (int)MathF.Round(v); Delegates.Invoke(p.Set, (nint)(&iv)); } else Delegates.Invoke(p.Set, (nint)(&v)); fxDirty = true; }

    // The slider setters only change the Camera Effects settings; the menu then runs its own "apply" action to push them to the
    // post-processing volume. Run that action after our writes, otherwise the values change but the picture does not.
    bool fxDirty, pushLogged;
    float nextPush; bool pushing, pushPending; int pushCount;
    /// <summary>Runs the Camera Effects menu's apply action. At most 4 times a second and never re-entrant: calling it every frame while two keyframes blend crashed the game.</summary>
    void PushFx(bool force = false)
    {
        if (fxDirty) pushPending = true;
        fxDirty = false;
        if (!pushPending || pushing || !force && Unity.Time < fxHoldUntil) return;
        if (!force && Unity.Time < nextPush) return;
        pushing = true;
        try
        {
            var cm = Recorder.Instance.Raw.cameraEffectsMenu;
            if (cm.Ptr == 0) { pushPending = false; return; }
            var apply = cm.sry;
            if (apply == 0) { pushPending = false; if (!pushLogged) { pushLogged = true; Log("TLFX: the Camera Effects menu has no apply action"); } return; }
            pushCount++;
            if (pushCount <= 15) Log($"TLFX: push #{pushCount} at frame {CurFrame:0}");
            nextPush = Unity.Time + 0.25f;
            pushPending = false;
            Delegates.Invoke(apply);
            if (!pushLogged) { pushLogged = true; Log("TLFX: image effects are pushed to the picture through the Camera Effects menu's apply action"); }
        }
        catch (Exception e) { LogOnce("push", e); }
        finally { pushing = false; }
    }

    /// <summary>Lists the sliders of the game's Camera Effects menu: label, range, getter and setter.</summary>
    void DiscoverFx()
    {
        var cm = Recorder.Instance.Raw.cameraEffectsMenu;
        if (cm.Ptr == 0) return;
        var menu = cm.srz;
        if (menu.Ptr == 0) return;
        nint best = 0; var bestCount = -1;
        foreach (var list in new[] { menu.slx, menu.sly, menu.sme })
        {
            if (list == 0) continue;
            var n = Unity.Call<int>(list, "get_Count");
            var c = 0;
            for (var i = 0; i < n; i++) { var it = Unity.Call(list, "get_Item", (nint)(&i)); if (it != 0 && Il2Cpp.ClassOf(it).Name is "qr" or "qt") c++; }
            if (c > bestCount) { bestCount = c; best = list; }
        }
        if (best == 0 || bestCount <= 0) { if (fx.Count == 0 && logged.Add("fxnone")) Log("TLFX: no sliders found in the Camera Effects menu yet"); return; }
        if (bestCount == fxLastCount && fxMenuSeen == menu.Ptr) return;

        var found = new List<FxParam>();
        var count = Unity.Call<int>(best, "get_Count");
        for (var r = 0; r < count; r++)
        {
            try
            {
                var row = Unity.Call(best, "get_Item", (nint)(&r));
                if (row == 0) continue;
                string rl; try { rl = new Jibbers.Interop.Game.qj(row).hkk() ?? ""; } catch { rl = "?"; }
                Log($"TLFX: camera menu row {r}: {Il2Cpp.ClassOf(row).Name} '{rl}'");
            }
            catch { }
        }
        var pins = new List<GameRef>();
        var names = new HashSet<string>();
        for (var i = 0; i < count; i++)
        {
            var it = Unity.Call(best, "get_Item", (nint)(&i));
            if (it == 0) continue;
            var cls = Il2Cpp.ClassOf(it).Name;
            if (cls != "qr" && cls != "qt") continue;
            if (cls == "qt")
            {
                // A list of choices (for example the blur type): kept as a number, switched at the keyframe, never blended.
                var sel = new Jibbers.Interop.Game.qt(it);
                if (sel.spn == 0 || sel.spo == 0) continue;
                string slabel;
                try { slabel = new Jibbers.Interop.Game.qj(it).hkk() ?? ""; } catch { slabel = ""; }
                slabel = slabel.Split(':')[0].Trim();
                if (slabel.Length == 0) slabel = $"Choice {found.Count + 1}";
                while (!names.Add(slabel)) slabel += "'";
                string[] opts;
                try { opts = sel.spm == 0 ? Array.Empty<string>() : Il2Cpp.ArrayObjects(sel.spm).Select(o => o == 0 ? "" : Il2Cpp.ReadString(o)).ToArray(); } catch { opts = Array.Empty<string>(); }
                var sp = new FxParam { Label = slabel, Get = sel.spn, Set = sel.spo, Row = it, Idx = i, IsInt = true, Options = opts, Min = 0, Max = Math.Max(opts.Length - 1, 1) };
                pins.Add(new GameRef(it)); pins.Add(new GameRef(sel.spn)); pins.Add(new GameRef(sel.spo));
                try { sp.Default = FxRead(sp); } catch { }
                found.Add(sp);
                Log($"TLFX: fx choice '{slabel}' options [{string.Join(", ", opts)}] value {sp.Default:0}");
                continue;
            }
            var q = new Jibbers.Interop.Game.qr(it);
            if (q.spa == 0 || q.spb == 0) continue;
            string label;
            try { label = new Jibbers.Interop.Game.qj(it).hkk() ?? ""; } catch { label = ""; }
            label = label.Split(':')[0].Trim();
            if (label.Length == 0) label = $"Effect {found.Count + 1}";
            while (!names.Add(label)) label += "'";
            float lo = q.spe, hi = q.spf;
            var p = new FxParam { Label = label, Get = q.spa, Set = q.spb, Row = it, Idx = i, Min = lo, Max = hi };
            pins.Add(new GameRef(it)); pins.Add(new GameRef(q.spa)); pins.Add(new GameRef(q.spb));
            float cur = 0;
            try { cur = FxRead(p); } catch { }
            p.Default = cur;
            if (!(hi > lo)) { lo = Math.Min(0, cur - 1); hi = Math.Max(1, cur + 1); p.Min = lo; p.Max = hi; }
            found.Add(p);
            Log($"TLFX: fx slider '{label}' range {p.Min:0.###}..{p.Max:0.###} (raw {q.spe:0.###}..{q.spf:0.###}) value {cur:0.###}");
        }
        DropFx(); fx.AddRange(found); fxKeep.AddRange(pins);
        // Values a previous replay left behind (set from the effects window): back to what the game had.
        if (fxPending.Count > 0)
        {
            foreach (var p in fx) if (fxPending.TryGetValue(p.Label, out var o)) { try { FxWriteRaw(p, o); } catch { } }
            fxPending.Clear();
        }
        fxList = best; fxListCount = count; fxLastCount = bestCount;
        fxMenuSeen = menu.Ptr;
        Log($"TLFX: {fx.Count} image effect sliders found");
    }

    void ApplyFx()
    {
        if (fx.Count == 0 || !inReplay || !Enabled) return;
        var cmenu = Recorder.Instance.Raw.cameraEffectsMenu;
        if (cmenu.Ptr == 0 || cmenu.srz.Ptr != fxMenuSeen)
        {
            // The Camera Effects menu is gone or was rebuilt: its sliders are stale, never call them.
            DropFx();
            return;
        }
        if (Unity.Time < fxHoldUntil) return;
        if (!FxListIntact()) { Log("TLFX: the Camera Effects menu changed, image effect sliders will be scanned again"); DropFx(); nextFxScan = Unity.Time + 1f; return; }
        if (Unity.Time < fxPreviewUntil) return;
        var f = CurFrame;
        var fishP = FisheyeParam();
        var fish = HankModPack.ReplayCameraMod.Instance;
        foreach (var p in fx)
        {
            if (fishP != null && ReferenceEquals(p, fishP) && fish is { WantsFisheye: true })
            {
                var target = FisheyeValue(p, fish.FisheyeAmount, fish.FisheyeInvert);
                if (!fxOrig.ContainsKey(p.Label)) fxOrig[p.Label] = FxRead(p);
                if (Math.Abs(FxRead(p) - target) > 1e-4f * Math.Max(1, Math.Abs(p.Max - p.Min))) FxWrite(p, target);
                continue;
            }
            var keys = ordered.Where(o => o.D.Fx.ContainsKey(p.Label)).ToArray();
            if (keys.Length == 0)
            {
                if (fxOrig.TryGetValue(p.Label, out var o)) { FxWrite(p, o); fxOrig.Remove(p.Label); lastFx.Remove(p.Label); }
                continue;
            }
            if (f < keys[0].Frame)
            {
                // Before the first keyframe: the game's own value.
                if (fxOrig.TryGetValue(p.Label, out var o0)) { FxWrite(p, o0); fxOrig.Remove(p.Label); lastFx.Remove(p.Label); }
                continue;
            }
            float v;
            if (keys.Length == 1 || f >= keys[^1].Frame) v = keys[^1].D.Fx[p.Label];
            else
            {
                v = keys[^1].D.Fx[p.Label];
                for (var i = 0; i < keys.Length - 1; i++)
                {
                    if (f > keys[i + 1].Frame) continue;
                    var span = keys[i + 1].Frame - keys[i].Frame;
                    var u = span <= 0 ? 1 : (f - keys[i].Frame) / span;
                    if (Smooth) u = u * u * (3 - 2 * u);
                    var a = keys[i].D.Fx[p.Label]; var b = keys[i + 1].D.Fx[p.Label];
                    v = p.IsInt ? a : a + (b - a) * u;
                    break;
                }
            }
            if (!fxOrig.ContainsKey(p.Label)) fxOrig[p.Label] = FxRead(p);
            // The game may put its own value back between frames, so compare with what the slider holds now (not with our last write).
            var now = FxRead(p);
            if (Math.Abs(now - v) > 1e-4f * Math.Max(1, Math.Abs(p.Max - p.Min)))
            {
                FxWrite(p, v);
                if (lastFx.TryAdd(p.Label, v)) Log($"TLFX: image effect '{p.Label}' driven by keyframes (now {now:0.###} -> {v:0.###}, read back {FxRead(p):0.###})");
                lastFx[p.Label] = v;
            }
        }
    }

    // ================= fisheye (driven by the Replay Camera) =================

    FxParam? FisheyeParam()
    {
        FxParam? best = null; var rank = 0;
        foreach (var p in fx)
        {
            if (p.IsInt || p.Stale) continue;
            var l = p.Label.ToLowerInvariant();
            var r = l.Contains("fisheye") ? 4 : l.Contains("lens") && l.Contains("distortion") ? 3 : l.Contains("distortion") && !l.Contains("chromatic") ? 2 : l.Contains("panini") ? 1 : 0;
            if (r > rank) { rank = r; best = p; }
        }
        if (best != null && logged.Add("fish:" + best.Label)) Log($"TLFX: fisheye uses the image effect '{best.Label}' ({best.Min:0.###}..{best.Max:0.###})");
        if (best == null && HankModPack.ReplayCameraMod.Instance is { WantsFisheye: true } && logged.Add("fishnone")) Log("TLFX: fisheye: no lens distortion slider found (open the Camera Effects menu once); only the wide FOV is applied");
        return best;
    }

    static float FisheyeValue(FxParam p, float amount, bool invert)
    {
        if (p.Min < 0 && p.Max > 0) return amount * (invert ? p.Min : p.Max);
        return p.Min + (p.Max - p.Min) * amount;
    }

    string FisheyeInfo()
    {
        var p = FisheyeParam();
        return p == null ? "Open the Camera Effects menu once so the lens distortion can be found (until then only the FOV widens)." : $"Lens distortion: '{p.Label}'.";
    }

    void RestoreFx()
    {
        if (!FxListIntact()) { foreach (var kv in fxOrig) fxPending.TryAdd(kv.Key, kv.Value); fxOrig.Clear(); lastFx.Clear(); return; }
        foreach (var p in fx)
            if (fxOrig.TryGetValue(p.Label, out var o)) { try { FxWriteRaw(p, o); } catch { } }
        fxOrig.Clear(); lastFx.Clear();
        PushFx(force: true);
    }

    // ================= rows in the game's keyframe panel =================

    struct Binding { public Func<float>? GetF; public Action<float>? SetF; public Func<bool>? GetB; public Action<bool>? SetB; public Action? Act; public Func<string>? GetS; public Action<string>? SetS; }
    static readonly List<Binding> bindings = new();
    static Il2CppClass? funcFloat, funcBool, funcString, actionFloat, actionBool, actionPlain, actionString;
    static readonly List<GameRef> keep = new();

    [UnmanagedCallersOnly] static float GetF(nint ctx, nint m) { try { return bindings[(int)ctx].GetF!(); } catch { return 0; } }
    [UnmanagedCallersOnly] static byte GetB(nint ctx, nint m) { try { return (byte)(bindings[(int)ctx].GetB!() ? 1 : 0); } catch { return 0; } }
    [UnmanagedCallersOnly] static void SetF(nint ctx, float v, nint m) { try { bindings[(int)ctx].SetF!(v); } catch (Exception e) { current?.LogOnce("setf", e); } }
    [UnmanagedCallersOnly] static nint GetS(nint ctx, nint m) { try { return Il2Cpp.String(bindings[(int)ctx].GetS!()); } catch { return 0; } }
    [UnmanagedCallersOnly] static void SetS(nint ctx, nint str, nint m) { try { bindings[(int)ctx].SetS!(str == 0 ? "" : Il2Cpp.ReadString(str)); } catch (Exception e) { current?.LogOnce("sets", e); } }
    [UnmanagedCallersOnly] static void RunAct(nint ctx, nint m) { try { bindings[(int)ctx].Act!(); } catch (Exception e) { current?.LogOnce("act", e); } }
    [UnmanagedCallersOnly] static void SetB(nint ctx, byte v, nint m) { try { bindings[(int)ctx].SetB!(v != 0); } catch (Exception e) { current?.LogOnce("setb", e); } }

    nint MakeDelegate(Il2CppClass k, nint fn, Binding b)
    {
        int idx;
        lock (bindings) { idx = bindings.Count; bindings.Add(b); }
        var d = Delegates.Native(k, fn, idx);
        keep.Add(new GameRef(d));
        return d;
    }

    void InitDelegateClasses()
    {
        if (funcFloat != null) return;
        var single = Il2Cpp.RequireClass("mscorlib.dll", "System.Single");
        var boolean = Il2Cpp.RequireClass("mscorlib.dll", "System.Boolean");
        var func = Il2Cpp.RequireClass("mscorlib.dll", "System.Func`1");
        funcFloat = Delegates.Instantiate(func, single);
        funcBool = Delegates.Instantiate(func, boolean);
        funcString = Delegates.Instantiate(func, Il2Cpp.RequireClass("mscorlib.dll", "System.String"));
        actionFloat = Delegates.ActionOf(single);
        actionBool = Delegates.ActionOf(boolean);
        actionString = Delegates.ActionOf(Il2Cpp.RequireClass("mscorlib.dll", "System.String"));
        actionPlain = Il2Cpp.RequireClass("mscorlib.dll", "System.Action");
    }

    void Touch(KfData d, bool previewTime, bool previewShk)
    {
        dirty = true;
        previewUntil = Unity.Time + 1.5f;
        previewTod = previewTime ? d.TodVal : -1;
        previewShake = previewShk ? d : null;
        SyncKeyframes();
    }

    void InjectMenus()
    {
        if (!inReplay || !Enabled || !MenuRows) return;
        var now = Unity.Time;

        // The game's keyframe panels.
        foreach (var (menu, title) in menuTitle.ToList())
        {
            if (injected.Contains(menu)) continue;
            if (Unity.IsNull(menu)) { menuTitle.Remove(menu); continue; }
            if (!title.StartsWith("Keyframe #", StringComparison.OrdinalIgnoreCase)) continue;
            if (now - (menuTouched.TryGetValue(menu, out var t) ? t : -10f) < 0.2f) continue;
            injected.Add(menu);
            try { AddRows(menu, title); }
            catch (Exception e) { Log($"TLFX: could not add the rows to '{title}': {e}"); }
        }
    }

    string InfoText()
    {
        var k = Selected();
        if (k == null) return $"Playhead at frame {curFrame:0}: no effect keyframe here. Moving a slider creates one; {DeleteKey} deletes it.";
        return $"Playhead at frame {curFrame:0}: effect keyframe at frame {k.Frame} ({keys.IndexOf(k) + 1} of {keys.Count}); {DeleteKey} deletes it.";
    }

    const string SunDir = "Sun/Direction", SunHeight = "Sun/Height", MoonPh = "Moon/Phase";

    static bool InTab(string label, string tab) => GroupOfFx(label) == tab;

    static string GroupOfFx(string label)
    {
        if (label.StartsWith("Sun/") || label.StartsWith("Moon/")) return "Time of day";
        if (label.StartsWith("Cam/Pov")) return "POV";
        if (label.StartsWith("Cam/")) return "Follow cam";
        var l = label.ToLowerInvariant();
        if (l.Contains("blur") || l.Contains("falloff") || l.Contains("tilt") || l.Contains("radial") || l.Contains("gaussian") || l.Contains("bokeh") || l.EndsWith("/radius") || l.EndsWith("/width")) return "Blur";
        if (l.Contains("distortion") || l.Contains("panini") || l.Contains("pixel") || l.Contains("chromatic") || l.Contains("sharp") || l.Contains("focus") || l.Contains("depth") || l.Contains("dof")) return "Lens & Focus";
        if (l.Contains("exposure") || l.Contains("bright") || l.Contains("contrast") || l.Contains("fade") || l.Contains("highlight") || l.Contains("shadow")) return "Exposure & Tone";
        if (l.Contains("saturation") || l.Contains("temperature") || l.Contains("sepia") || l.Contains("tint")) return "Color";
        return "Stylize";
    }

    static Il2CppClass? tmpText;

    /// <summary>The text the player sees on a row, e.g. "Frame: 407" (before the colon).</summary>
    string VisibleLabel(nint item)
    {
        try
        {
            var rect = new Jibbers.Interop.Game.qj(item)._snp_k__BackingField.Ptr;
            if (rect == 0) return "";
            tmpText ??= Il2Cpp.RequireClass("Unity.TextMeshPro.dll", "TMPro.TMP_Text");
            var texts = Unity.GetComponentsInChildren(rect, tmpText.Value, true);
            foreach (var t in texts)
            {
                var str = Unity.Call(t, "get_text");
                var text = str == 0 ? "" : Il2Cpp.ReadString(str);
                if (!string.IsNullOrWhiteSpace(text)) return text.Split(':')[0].Trim();
            }
        }
        catch (Exception e) { LogOnce("vislabel", e); }
        return "";
    }

    string ItemLabel(nint item)
    {
        var vis = VisibleLabel(item);
        if (vis.Length > 0) return vis;
        var q = new Jibbers.Interop.Game.qj(item);
        string l = "";
        try { l = q.hkk() ?? ""; } catch { }
        if (l.Length == 0) { try { l = q._snn_k__BackingField ?? ""; } catch { } }
        if (l.Length == 0) { try { l = q._sno_k__BackingField ?? ""; } catch { } }
        return l;
    }

    /// <summary>Groups the game's own keyframe rows (Frame, Path, Transition, Position, Look... in "Transition"; FOV, Roll, Time Scale, Command in "Camera") into collapsible tabs.</summary>
    void OrganizeBase(RawFormMenu menu, Func<Action, nint> AC)
    {
        nint list = 0;
        if (menu.smc.Ptr != 0) list = menu.smc.sqz;
        if (list == 0)
        {
            // The usual root is not set. The log shows two lists: slx (the top-level rows, 20 here) and sly (the same plus the rows nested in
            // Copy|Paste and Duplicate|Delete, 24). Group through slx, the top-level one.
            list = menu.slx;
            if (list == 0 || Unity.Call<int>(list, "get_Count") < 8) { Log("TLFX: could not find the keyframe panel's rows"); return; }
        }
        var n = Unity.Call<int>(list, "get_Count");
        var items = new List<nint>();
        for (var i = 0; i < n; i++) items.Add(Unity.Call(list, "get_Item", (nint)(&i)));
        Log($"TLFX: keyframe panel has {n} top-level rows: " + string.Join(" | ", items.Select(it => $"{Il2Cpp.ClassOf(it).Name}:{ItemLabel(it)}")));

        var groups = new (string Title, string[] Words)[]
        {
            ("Transition", new[] { "frame", "path", "transition", "position", "look", "rotation", "adjust" }),
            ("Camera", new[] { "fov", "roll", "time scale", "timescale", "command" }),
        };
        var picked = new List<nint>[groups.Length];
        for (var g = 0; g < groups.Length; g++) picked[g] = new List<nint>();
        var firstIndex = int.MaxValue;
        for (var i = 0; i < items.Count; i++)
        {
            var cls = Il2Cpp.ClassOf(items[i]).Name;
            if (cls is "rh" or "ri" or "rc") continue;           // horizontal rows (Copy/Paste, Duplicate/Delete), separators, headers stay where they are
            var label = ItemLabel(items[i]).ToLowerInvariant();
            if (label.Length == 0) continue;
            for (var g = 0; g < groups.Length; g++)
                if (groups[g].Words.Any(w => label.Contains(w))) { picked[g].Add(items[i]); firstIndex = Math.Min(firstIndex, i); break; }
        }
        if (firstIndex == int.MaxValue)
        {
            // Labels unreadable: fall back to the known order of the panel (Frame, Path, Transition In/Out, Position Mode, Position, Look, Rotation, Adjust Pose, FOV, Roll, Time Scale, Command).
            var content = new List<int>();
            for (var i = 0; i < items.Count; i++)
            {
                var cls = Il2Cpp.ClassOf(items[i]).Name;
                if (cls is "rh" or "ri" or "rc") continue;
                if (content.Count == 0 && cls == "qu") continue;       // the Back button
                content.Add(i);
            }
            if (content.Count >= 12)
            {
                var map = new[] { 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1 };
                for (var c = 0; c < content.Count && c < map.Length; c++) { picked[map[c]].Add(items[content[c]]); firstIndex = Math.Min(firstIndex, content[c]); }
                Log("TLFX: base keyframe rows grouped by position (labels unreadable)");
            }
        }
        if (firstIndex == int.MaxValue) { Log("TLFX: no base keyframe rows recognised"); return; }

        // Detach the picked rows, then put one accordion per group where the first picked row was.
        foreach (var grp in picked) foreach (var it in grp) Unity.Call(list, "Remove", it);
        var at = Math.Min(firstIndex, Unity.Call<int>(list, "get_Count"));
        for (var g = 0; g < groups.Length; g++)
        {
            if (picked[g].Count == 0) continue;
            var acc = menu.Accordion(groups[g].Title, AC(() => { }), false);
            if (acc.Ptr == 0) continue;
            Unity.Call(list, "Remove", acc.Ptr);
            var idx = at++;
            Unity.Call(list, "Insert", (nint)(&idx), acc.Ptr);
            foreach (var it in picked[g])
            {
                Unity.Call(acc.sqw, "Add", it);
                try { new Jibbers.Interop.Game.qj(it).hka(acc); } catch (Exception e) { LogOnce("hka", e); }
            }
        }
        Log($"TLFX: base keyframe rows grouped into {groups.Count((x) => true)} tabs");
    }

    void AddRows(nint menuPtr, string where)
    {
        InitDelegateClasses();
        var menu = new RawFormMenu(menuPtr);

        var getF = (nint)(delegate* unmanaged<nint, nint, float>)&GetF;
        var getB = (nint)(delegate* unmanaged<nint, nint, byte>)&GetB;
        var getS = (nint)(delegate* unmanaged<nint, nint, nint>)&GetS;
        var setF = (nint)(delegate* unmanaged<nint, float, nint, void>)&SetF;
        var setB = (nint)(delegate* unmanaged<nint, byte, nint, void>)&SetB;
        var runAct = (nint)(delegate* unmanaged<nint, nint, void>)&RunAct;

        nint GF(Func<float> g) => MakeDelegate(funcFloat!.Value, getF, new Binding { GetF = g });
        nint SF(Action<float> s) => MakeDelegate(actionFloat!.Value, setF, new Binding { SetF = s });
        nint GB(Func<bool> g) => MakeDelegate(funcBool!.Value, getB, new Binding { GetB = g });
        nint SB(Action<bool> s) => MakeDelegate(actionBool!.Value, setB, new Binding { SetB = s });
        nint GS(Func<string> g) => MakeDelegate(funcString!.Value, getS, new Binding { GetS = g });
        nint AC(Action a) => MakeDelegate(actionPlain!.Value, runAct, new Binding { Act = a });
        var setS = (nint)(delegate* unmanaged<nint, nint, nint, void>)&SetS;
        nint SS(Action<string> a) => MakeDelegate(actionString!.Value, setS, new Binding { SetS = a });

        // 1. The game's own rows, grouped into tabs.
        if (OrganizeBaseRows)
        {
            try { OrganizeBase(menu, AC); }
            catch (Exception e) { Log($"TLFX: could not group the base keyframe rows: {e}"); }
        }

        // 2. Our rows, in tabs laid out like the Camera Effects menu.
        void Tab(string title, Action rows)
        {
            // A coloured dot, in the colour of the tab's icon on the FX timeline, before the name (rich text).
            var c = TabColors.FirstOrDefault(t => t.Tab == title).C;
            var hex = c.A > 0 ? $"{(int)(c.R * 255):X2}{(int)(c.G * 255):X2}{(int)(c.B * 255):X2}" : "FFFFFF";
            menu.Accordion($"<color=#{hex}>\u25CF</color> {title}", AC(rows), false);
        }

        menu.Header("Timeline FX: effect keyframes");
        menu.Info(GS(InfoText), false, null);
        SaveLoadRows(menu, AC, GS, SS);
        menu.Button("Reset all effect keyframes", AC(RemoveAllEffectKeyframes));
        menu.hhs("CamLight (light on the replay camera)", GB(() => lightMode == "CamLight"), SB(v => SetLights(v ? "CamLight" : "Off")), false);

        Tab("Time of day", () =>
        {
            EditButtons(menu, AC, GS, "Time of day", null);
            menu.Slider("Time of day", 0f, 1f, GF(() => Selected()?.D.TodVal ?? CurrentEnvTime()), SF(v => { var k = SelOrCreate(); k.D.TodVal = v; k.D.Tod = true; Touch(k.D, true, false); }), 0.01f, 2);
            void Sky(string label, string key, float min, float max, float step, Func<Jibbers.Interop.Game.EnvironmentManager, float> live)
                => menu.Slider(label, min, max,
                    GF(() => Selected() is { } k && k.D.Fx.TryGetValue(key, out var v) ? v : (EnvManager.First() is { } e ? live(e.Raw) : 0f)),
                    SF(v => { var k = SelOrCreate(); k.D.Fx[key] = v; dirty = true; SyncKeyframes(); }), step, 1);
            Sky("Sun direction", SunDir, 0f, 360f, 1f, e => e.sunYAngle);
            Sky("Sun height", SunHeight, 0f, 90f, 1f, e => e.sunPeakAngle);
            Sky("Moon phase", MoonPh, 0f, 1f, 0.01f, e => e.moonPhase);
        });

        void Shake(string label, float min, float max, Func<KfData, float> get, Action<KfData, float> set, float step, int dec)
            => menu.Slider(label, min, max, GF(() => get(Selected()?.D ?? new KfData())), SF(v => { var k = SelOrCreate(); set(k.D, v); k.D.ShakeOn = true; Touch(k.D, false, true); }), step, dec);
        Tab("Camera Shake", () =>
        {
            EditButtons(menu, AC, GS, "Camera Shake", null);
            Shake("Strength", 0f, 1f, d => d.Shake, (d, v) => d.Shake = v, 0.01f, 2);
            Shake("Speed", 0.5f, 20f, d => d.Hz, (d, v) => d.Hz = v, 0.5f, 1);
            Shake("Smoothness (%)", 0f, 100f, d => d.Smoothness, (d, v) => d.Smoothness = v, 1f, 0);
            Shake("Rotation (%)", 0f, 100f, d => d.RotPct, (d, v) => d.RotPct = v, 1f, 0);
            Shake("Position (%)", 0f, 100f, d => d.PosPct, (d, v) => d.PosPct = v, 1f, 0);
            Shake("Roll (%)", 0f, 100f, d => d.RollPct, (d, v) => d.RollPct = v, 1f, 0);
        });

        if (HankModPack.ReplayCameraMod.Instance is { } rc)
        {
            void RcSlider(string label, string key, float min, float max, float step, int dec, Func<float> get)
                => menu.Slider(label, min, max, GF(get), SF(v => rc.Set(key, v)), step, dec);
            void RcChoice(string title, string key, Func<string> get, params string[] options)
                => menu.Accordion(title, AC(() =>
                {
                    menu.Info(GS(() => "Now: " + get()), false, null);
                    foreach (var o in options) { var opt = o; menu.Button(opt, AC(() => rc.Set(key, opt))); }
                }), false);
            void RcToggle(string label, string key, Func<bool> get)
                => menu.hhs(label, GB(get), SB(v => rc.Set(key, v)), false);
            void CamKey(string tab, string label, string key, float min, float max, float step, int dec, Func<float> baseVal)
                => menu.Slider(label, min, max,
                    GF(() => Selected() is { } k && k.D.Fx.TryGetValue("Cam/" + key, out var v) ? v : (EvalSky("Cam/" + key) ?? baseVal())),
                    SF(v => { var k = SelOrCreate(); EnsureCam(k, tab); k.D.Fx["Cam/" + key] = v; dirty = true; SyncKeyframes(); }), step, dec);
            void CamTop(string tab)
            {
                menu.Info(GS(() => "Now: " + rc.Mode + (CameraModeKey() is { } mk ? $" (keyframed: {(mk == 1 ? "Follow cam" : mk == 2 ? "POV" : "Off")})" : " (no camera keyframe before the playhead)")), false, null);
                EditButtons(menu, AC, GS, tab, null);
                menu.Info(GS(() => tab + " at this keyframe:"), false, null);
                menu.Horizontal(AC(() =>
                {
                    var mine = tab == "POV" ? "Cam/PovOn" : "Cam/FollowOn"; var other = tab == "POV" ? "Cam/FollowOn" : "Cam/PovOn";
                    void Pick(string name, float on) => menu.Button(name, AC(() =>
                    {
                        var k = SelOrCreate(); EnsureCam(k, tab);
                        k.D.Fx.Remove("Cam/Mode");
                        k.D.Fx[mine] = on; if (on > 0.5f && k.D.Fx.ContainsKey(other)) k.D.Fx[other] = 0;
                        dirty = true; SyncKeyframes();
                    }));
                    Pick("Off", 0); Pick("On", 1);
                }));
            }

            Tab("Follow cam", () =>
            {
                CamTop("Follow cam");
                    RcChoice("Shot preset", "cam_style", () => rc.CamStyle, "Behind", "Side (right)", "Side (left)", "Front", "Drone", "Skate fisheye", "Low chase", "In-game camera", "In-game drone", "Custom");
                    CamKey("Follow cam", "Angle around the rider (180 = behind)", "Angle", 0f, 360f, 5f, 0, () => rc.CamAngle);
                    RcSlider("Orbit around the rider (deg/s)", "cam_sweep", -90f, 90f, 1f, 0, () => rc.CamSweep);
                    CamKey("Follow cam", "Distance (m)", "Distance", 0.8f, 30f, 0.1f, 1, () => rc.CamDistance);
                    CamKey("Follow cam", "Height (m)", "Height", -2f, 15f, 0.1f, 1, () => rc.CamHeight);
                    CamKey("Follow cam", "Aim height on the rider (m)", "Aim", -0.5f, 2f, 0.05f, 2, () => rc.CamAim);
                    CamKey("Follow cam", "Keep the rider in frame (%)", "Focus", 0f, 100f, 5f, 0, () => rc.CamFocus);
                    CamKey("Follow cam", "Tilt / dutch angle (deg)", "Roll", -45f, 45f, 1f, 0, () => rc.CamRoll);
                    RcSlider("Reactivity", "cam_react", 0.5f, 12f, 0.5f, 1, () => rc.CamReactivity);
                    RcSlider("Lead-ahead (%)", "cam_lead", 0f, 100f, 5f, 0, () => rc.CamLead);
                    RcSlider("Pull back with speed (%)", "cam_pull", 0f, 100f, 5f, 0, () => rc.CamPull);
                    RcSlider("Hand-held shake (%)", "cam_shake", 0f, 100f, 5f, 0, () => rc.CamShake);
                    menu.Info(GS(() => FisheyeInfo()), false, null);
                    RcToggle("Fisheye", "cam_fisheye", () => rc.CamFisheye);
                    CamKey("Follow cam", "Fisheye strength (%)", "FisheyeStrength", 0f, 100f, 5f, 0, () => rc.FisheyeStrength);
                    RcToggle("Reverse the distortion", "fish_invert", () => rc.FisheyeInvert);
            });

            Tab("POV", () =>
            {
                CamTop("POV");
                    menu.Button("Learn the body axes", AC(() => rc.Recalibrate()));
                    RcChoice("POV preset", "pov_preset", () => rc.PovPreset, "Realistic (look ahead)", "GoPro helmet", "Chest cam", "Cinematic", "In-game camera", "Custom");
                    RcChoice("Lock", "pov_lock", () => rc.PovLock, "Real (look where you go)", "Head (locked)", "Head (level horizon)", "Travel direction");
                    RcChoice("Aim from", "pov_base", () => rc.PovBase, "Shoulders", "Chest", "Hips", "Head");
                    CamKey("POV", "Horizon height in the frame (% from top)", "PovHorizon", 15f, 70f, 1f, 0, () => rc.PovHorizon);
                    CamKey("POV", "Look up / down (deg)", "PovPitch", -30f, 30f, 1f, 0, () => rc.PovPitch);
                    RcSlider("Over-the-shoulder look in switch (%)", "pov_shoulder", 0f, 100f, 5f, 0, () => rc.PovShoulder);
                    CamKey("POV", "Shift towards the shoulder in switch (m)", "PovShift", 0f, 0.6f, 0.01f, 2, () => rc.PovShift);
                    RcSlider("Max head turn over the shoulder (deg)", "pov_neck", 30f, 180f, 5f, 0, () => rc.PovNeckTurn);
                    RcSlider("Calm the view in the air (%)", "pov_airstab", 0f, 100f, 5f, 0, () => rc.PovAirStab);
                    RcSlider("Look along the slope (%)", "pov_slope", 0f, 100f, 5f, 0, () => rc.PovSlope);
                    RcSlider("Torso lean influence (%)", "pov_headpitch", 0f, 100f, 5f, 0, () => rc.PovHeadPitch);
                    RcSlider("Body turn influence (%)", "pov_headyaw", 0f, 100f, 5f, 0, () => rc.PovHeadYaw);
                    RcSlider("Max look up / down (deg)", "pov_maxpitch", 5f, 90f, 1f, 0, () => rc.PovMaxPitch);
                    RcSlider("Smoothing (%)", "pov_smooth", 0f, 95f, 5f, 0, () => rc.PovSmooth);
                    RcSlider("Speed FOV boost (deg)", "pov_boost", 0f, 30f, 1f, 0, () => rc.PovBoost);
                    RcSlider("Head shake (%)", "pov_shake", 0f, 100f, 5f, 0, () => rc.PovShake);
                    RcSlider("Eye forward offset (m)", "pov_fwd", -0.3f, 0.6f, 0.01f, 2, () => rc.PovForward);
                    RcSlider("Eye up offset (m)", "pov_up", -0.6f, 0.4f, 0.01f, 2, () => rc.PovUp);
                    RcToggle("Hide the rider's head and helmet", "pov_hide", () => rc.PovHideHead);
                    RcToggle("Fisheye", "pov_fisheye", () => rc.PovFisheye);
                    CamKey("POV", "Fisheye strength (%)", "FisheyeStrength", 0f, 100f, 5f, 0, () => rc.FisheyeStrength);
                    RcChoice("Head forward axis", "pov_axis", () => rc.PovAxis, "Auto", "Z", "-Z", "X", "-X", "Y", "-Y");
            });
        }

        if (fx.Count == 0) { try { DiscoverFx(); } catch (Exception e) { LogOnce("fxscan2", e); } }
        if (fx.Count == 0)
        {
            menu.Info(GS(() => "Image effects appear once the Camera Effects menu has been opened."), false, null);
        }
        else
        {
            foreach (var title in new[] { "Lens & Focus", "Exposure & Tone", "Color", "Blur", "Stylize" })
            {
                var members = fx.Where(p => GroupOfFx(p.Label) == title).ToList();
                if (members.Count == 0) continue;
                Tab(title, () =>
                {
                    EditButtons(menu, AC, GS, title, members);
                    foreach (var p in members)
                    {
                        var q = p;
                        if (q.IsInt && q.Options is { Length: > 0 })
                        {
                            menu.Accordion(q.Label.Split('/').Last(), AC(() =>
                            {
                                menu.Info(GS(() => { var i = (int)Math.Round(Selected() is { } k && k.D.Fx.TryGetValue(q.Label, out var v) ? v : FxRead(q)); return $"Now: {(i >= 0 && i < q.Options.Length ? q.Options[i] : i.ToString())}"; }), false, null);
                                for (var oi = 0; oi < q.Options.Length; oi++)
                                {
                                    var idx = oi;
                                    menu.Button(q.Options[oi], AC(() => { var k = SelOrCreate(); k.D.Fx[q.Label] = idx; dirty = true; fxPreviewUntil = Unity.Time + 1.5f; try { FxWrite(q, idx); PushFx(true); } catch { } SyncKeyframes(); }));
                                }
                            }), false);
                            continue;
                        }
                        var step = Math.Max((q.Max - q.Min) / 200f, 0.0001f);
                        menu.Slider(q.Label, q.Min, q.Max,
                            GF(() => Selected() is { } k && k.D.Fx.TryGetValue(q.Label, out var v) ? v : FxRead(q)),
                            SF(v => { var k = SelOrCreate(); k.D.Fx[q.Label] = v; dirty = true; fxPreviewUntil = Unity.Time + 1.5f; try { FxWrite(q, v); PushFx(); } catch { } SyncKeyframes(); }),
                            step, 0);
                    }
                });
            }
        }
        if (FindMod("alpinevision") != null)
            menu.Accordion("Alpine Vision (live)", AC(() => AlpineRows(menu, AC, GF, SF, GB, SB, GS)), false);
        injectedCount++;
        try { menu.Apply(); } catch (Exception e) { LogOnce("apply", e); }
        try { menu.Refresh(); } catch (Exception e) { LogOnce("refresh", e); }
        Log($"TLFX: organised '{where}'");
    }

    // ================= the FX timeline under the game's timeline =================

    bool spriteLogged;
    readonly List<nint> strip = new();
    nint stripParent, stripSource, stripHead;
    string stripSig = "";
    bool stripBroken;

    void ClearStrip()
    {
        foreach (var m in strip) { try { if (!Unity.IsNull(m)) Unity.Destroy(Unity.Call(m, "get_gameObject")); } catch { } }
        strip.Clear(); stripSig = ""; stripParent = 0; stripHead = 0;
    }

    nint MakeMarker(nint parent, Vector3 world, Color color, float height, float width, bool rect = false)
    {
        var clone = Unity.Instantiate(stripSource);
        if (clone == 0) return 0;
        if (Unity.GetComponent(clone, Jibbers.Interop.Game.TrimHandle.Class.Class) != 0)
        {
            Unity.Destroy(Unity.Call(clone, "get_gameObject"));
            stripBroken = true;
            Log("TLFX: the handle's circle shares its object with the trim handle: FX timeline disabled");
            return 0;
        }
        if (rect)   // no sprite: a plain rectangle (the bar and the playhead); keyframe icons keep the handle's round sprite
        {
            try { Unity.Call(clone, "set_sprite", (nint)0); }
            catch (Exception e) { LogOnce("sprite", e); }
        }
        var tr = Unity.Transform(clone);
        var no = (byte)0;
        Unity.Call(tr, "SetParent", parent, (nint)(&no));
        var half = new Vector2(0.5f, 0.5f);
        var size = new Vector2(width, height);
        Unity.Call(tr, "set_anchorMin", (nint)(&half));
        Unity.Call(tr, "set_anchorMax", (nint)(&half));
        Unity.Call(tr, "set_pivot", (nint)(&half));
        Unity.Call(tr, "set_sizeDelta", (nint)(&size));
        var one = new Vector3(1, 1, 1);
        Unity.Call(tr, "set_localScale", (nint)(&one));
        var w = world;
        Unity.Call(tr, "set_position", (nint)(&w));
        var c = color;
        Unity.Call(clone, "set_color", (nint)(&c));
        var off = (byte)0;
        Unity.Call(clone, "set_raycastTarget", (nint)(&off));
        strip.Add(clone);
        return clone;
    }

    // The game's bar geometry in screen units (world space of its canvas).
    float gCenterX, gCenterY, gWidthPx, gHeightPx, gScale, gZ;

    bool ReadBar(nint bar)
    {
        var tr = Unity.Transform(bar);
        var pos = Unity.Call<Vector3>(tr, "get_position");
        var sc = Unity.Call<Vector3>(tr, "get_lossyScale").X;
        var rect = Unity.Call<Rect>(tr, "get_rect");
        var piv = Unity.Call<Vector2>(tr, "get_pivot");
        if (rect.Width <= 1 || sc <= 0) return false;
        gScale = sc; gWidthPx = rect.Width * sc; gHeightPx = rect.Height * sc; gZ = pos.Z;
        gCenterX = pos.X + (0.5f - piv.X) * gWidthPx;
        gCenterY = pos.Y + (0.5f - piv.Y) * gHeightPx;
        return true;
    }

    // Resolution reference: the game's bar scales with the UI, so its width stands in for the screen size (1080 px wide bar ~ 1080p).
    float screenH => Math.Max(gWidthPx, 300f);

    // All sizes below are in pixels, derived from the screen height so they follow the resolution.
    float StripHeightPx => Math.Max(gHeightPx * 0.75f, 14f);
    // Under the game's bar, but never lower than the bottom edge of the screen (y = 0): the strip sits at the very bottom when the bar is close to it.
    // Below the game's text row (Cam: ... / time / Speed), which sits about 2.1 bar heights under the bar's centre and is about 1.1 bar heights tall.
    float StripCenterY() => Math.Max(gCenterY - gHeightPx * 2.7f - screenH * (float)StripY / 100f - StripHeightPx / 2, StripHeightPx / 2 + 2f);
    float XAt(float f) => gCenterX - gWidthPx / 2 + f * gWidthPx;

    // One colour per option tab. A keyframe shows one dot per tab that has options keyed on it.
    static readonly (string Tab, Color C)[] TabColors =
    {
        ("Time of day", new Color(1f, 0.65f, 0.1f, 1f)),
        ("Camera Shake", new Color(1f, 0.3f, 0.3f, 1f)),
        ("Lens & Focus", new Color(0.3f, 0.6f, 1f, 1f)),
        ("Exposure & Tone", new Color(1f, 0.92f, 0.3f, 1f)),
        ("Color", new Color(0.9f, 0.4f, 0.95f, 1f)),
        ("Stylize", new Color(0.35f, 0.9f, 0.5f, 1f)),
        ("Blur", new Color(0.3f, 0.9f, 0.95f, 1f)),
        ("Follow cam", new Color(1f, 1f, 1f, 1f)),
        ("POV", new Color(0.75f, 0.55f, 1f, 1f)),
    };

    static int TabMask(KfData d)
    {
        var m = 0;
        if (d.Tod) m |= 1;
        if (d.ShakeOn) m |= 2;
        foreach (var label in d.Fx.Keys)
        {
            var group = GroupOfFx(label);
            switch (group)
            {
                case "Time of day": m |= 1; break;
                case "Lens & Focus": m |= 4; break;
                case "Exposure & Tone": m |= 8; break;
                case "Color": m |= 16; break;
                case "Blur": m |= 64; break;
                case "Follow cam": m |= 128; break;
                case "POV": m |= 256; break;
                default: m |= 32; break;
            }
        }
        return m;
    }

    /// <summary>Turns a circle image into slice i of n of a pie (radial fill from the top, clockwise).</summary>
    void MakePie(nint img, int n, int i)
    {
        try
        {
            var type = 3;       // Image.Type.Filled
            var method = 4;     // Image.FillMethod.Radial360
            var origin = 2;     // Radial360 origin: Top
            var clockwise = (byte)1;
            var amount = 1f / n;
            Unity.Call(img, "set_type", (nint)(&type));
            Unity.Call(img, "set_fillMethod", (nint)(&method));
            Unity.Call(img, "set_fillOrigin", (nint)(&origin));
            Unity.Call(img, "set_fillClockwise", (nint)(&clockwise));
            Unity.Call(img, "set_fillAmount", (nint)(&amount));
            var rot = new Vector3(0, 0, -i * 360f / n);
            Unity.Call(Unity.Transform(img), "set_localEulerAngles", (nint)(&rot));
        }
        catch (Exception e) { LogOnce("pie", e); }
    }

    long nextStripBuild;
    // ---------------------------------------------------------------- Effects window (every effect at the playhead, editable with the gamepad and the mouse)
    HankModPack.QuickMenuOverlay? fxView, fxHint; bool fxHintOn; string fxWinSig = "";
    int fxSel, fxTab, fxDragRow = -1; bool fxInTab, fxEdit, fxOpen, fxLtPrev, fxRtPrev, fxMuted;
    JoinSpawn.RewiredMute? fxMute;
    PadButton fxPrev; long fxHeldSince, fxNextRep; int fxHeldDir; bool[] fxConnected = new bool[4];
    delegate void RowAdjust(int dir, float mult);
    sealed record WRow(string L, string V, float Bar, bool Mod, RowAdjust? Adjust, Action? Reset, Action? Next, Action<float>? SetTo = null);
    readonly record struct FxIn(int Move, int Dir, float Mult, int TabStep, bool A, bool B, bool X, bool Y, bool R3, bool L3 = false);

    /// <summary>L2 (while a keyframe panel is open) opens the window and mutes the game's controls, Circle closes it; hold R2 for bigger steps.
    /// D-pad: navigate (left / right change a value), Cross / A: open a tab or next choice, Circle / B: back (then close), Square / X: create the keyframe, Triangle / Y: delete it, L1 / R1: change tab, R3: reset the row.</summary>
    FxIn FxInput(bool panelOpen)
    {
        PadButton b = 0; float lt = 0, rt = 0;
        for (var slot = 0; slot < fxConnected.Length; slot++)
        {
            var pad = Gamepad.Read(slot);
            fxConnected[slot] = pad.Connected;
            if (!pad.Connected) continue;
            b |= pad.Buttons; lt = Math.Max(lt, pad.LeftTrigger); rt = Math.Max(rt, pad.RightTrigger);
        }
        var ltDown = lt > 0.5f; var rtDown = rt > 0.5f;
        if (!panelOpen) fxOpen = false;
        else if (ltDown && !fxLtPrev) { fxOpen = true; fxSel = 0; fxInTab = false; fxPrev = b; }
        fxLtPrev = ltDown; fxRtPrev = rtDown;
        var edit = fxOpen;
        if (edit != fxMuted)
        {
            try { fxMute ??= new JoinSpawn.RewiredMute { IncludeUi = true }; if (edit) fxMute.Mute(); else fxMute.Restore(); fxMuted = edit; } catch (Exception e) { LogOnce("fxmute", e); fxMuted = edit; }
        }
        var press = b & ~fxPrev; fxPrev = b;
        if (!edit) return new(0, 0, 1, 0, false, false, false, false, false);
        var boost = rtDown ? 5f : 1f;   // R2 held: bigger steps
        var now = Environment.TickCount64; var step = 0; float mult = 1;
        var dir = (b.HasFlag(PadButton.DpadRight) ? 1 : 0) - (b.HasFlag(PadButton.DpadLeft) ? 1 : 0);
        if (dir == 0) fxHeldDir = 0;
        else if (dir != fxHeldDir) { fxHeldDir = dir; fxHeldSince = now; fxNextRep = now + 350; step = dir; mult = boost; }
        else if (now >= fxNextRep) { fxNextRep = now + 50; step = dir; mult = (1 + Math.Min((now - fxHeldSince) / 700f, 9f)) * boost; }
        return new((press.HasFlag(PadButton.DpadDown) ? 1 : 0) - (press.HasFlag(PadButton.DpadUp) ? 1 : 0), step, mult,
            (press.HasFlag(PadButton.RightShoulder) ? 1 : 0) - (press.HasFlag(PadButton.LeftShoulder) ? 1 : 0),
            press.HasFlag(PadButton.A), press.HasFlag(PadButton.B), press.HasFlag(PadButton.X), press.HasFlag(PadButton.Y), press.HasFlag(PadButton.RightStick), press.HasFlag(PadButton.LeftStick));
    }

    void FxWindow()
    {
        // Shown only while one of the game's "Keyframe #N" panels is open (that is, once a keyframe is clicked), and opened with L2.
        var panelOpen = false;
        foreach (var (m, t) in menuTitle)
        {
            if (!t.StartsWith("Keyframe #", StringComparison.OrdinalIgnoreCase) || Unity.IsNull(m)) continue;
            try { if (Unity.Call<bool>(m, "get_isActiveAndEnabled")) { panelOpen = true; break; } } catch { panelOpen = true; break; }
        }
        var inp = inReplay && Enabled && FxWindowOn ? FxInput(panelOpen) : new FxIn(0, 0, 1, 0, false, false, false, false, false);
        if (!inReplay || !Enabled || !FxWindowOn || !panelOpen || !fxOpen)
        {
            if (fxWinSig != "") { fxView?.Hide(); fxWinSig = ""; }
            fxEdit = false; fxDragRow = -1;
            // A keyframe panel is open: a small reminder of how to open the effects window.
            if (inReplay && Enabled && FxWindowOn && panelOpen)
            {
                fxHint ??= new HankModPack.QuickMenuOverlay();
                fxHint.RightSide = true; fxHint.Compact = true;
                var st = HankModPack.QuickMenuMod.Instance?.IconStyle == "PlayStation" ? HankModPack.PadStyle.PlayStation : HankModPack.PadStyle.Xbox;
                fxHint.Show("EFFECTS", new List<HankModPack.QuickMenuOverlay.Row> { new(HankModPack.PadBtn.A, "", "", false, false, HasIcon: false, Small: true, Legend: new[] { (HankModPack.PadBtn.LT, "open the effects window") }) },
                    st, 125, 100, HankModPack.HudThemes.WithWindow(HankModPack.HankHudMod.Instance?.CurrentLook), 360);
                fxHintOn = true;
            }
            else if (fxHintOn) { fxHint?.Hide(); fxHintOn = false; }
            return;
        }
        if (fxHintOn) { fxHint?.Hide(); fxHintOn = false; }
        fxEdit = true;
        fxView ??= new HankModPack.QuickMenuOverlay();
        fxView.RightSide = FxWindowSide != "Left";

        var sections = new List<(string Title, List<WRow> Rows, IEnumerable<FxParam>? Members)>();
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        // A slider row: current value, range, step and the function that applies a new value.
        WRow Sl(string label, float cur, float min, float max, float step, int dec, bool mod, Action<float> set, Action? reset = null)
            => new(label, cur.ToString(dec == 0 ? "0" : "0." + new string('#', dec), inv), max > min ? Math.Clamp((cur - min) / (max - min), 0, 1) : 0, mod,
                (d, m) => set(Math.Clamp(cur + d * step * m, min, max)), reset, null, f => set(min + f * (max - min)));
        // A change made in the window always leaves a keyframe at the playhead (the camera and Alpine Vision settings have no data of their own in it).
        void KfMark() { SelOrCreate(); dirty = true; SyncKeyframes(); }
        var tod = EvalTod();
        {
            var h = (tod ?? CurrentEnvTime()) * 24f;
            void SetTod(float v) { var k = SelOrCreate(); k.D.TodVal = Math.Clamp(v, 0f, 1f); k.D.Tod = true; Touch(k.D, true, false); }
            var cur = tod ?? CurrentEnvTime();
            var rows = new List<WRow> { new("Time of day", $"{(int)h % 24:00}:{(int)((h % 1f) * 60):00}", Math.Clamp(h / 24f, 0, 1), tod != null, (d, m) => SetTod(cur + d * 0.004f * m), null, null, SetTod) };
            WRow Sky(string label, string key, float min, float max, float step, int dec, Func<Jibbers.Interop.Game.EnvironmentManager, float> live)
            {
                var v = Selected() is { } k && k.D.Fx.TryGetValue(key, out var kv) ? kv : EvalSky(key) ?? (EnvManager.First() is { } e ? live(e.Raw) : 0f);
                return Sl(label, v, min, max, step, dec, EvalSky(key) != null, nv => { var kk = SelOrCreate(); kk.D.Fx[key] = nv; dirty = true; SyncKeyframes(); });
            }
            rows.Add(Sky("Sun direction", SunDir, 0f, 360f, 1f, 0, e => e.sunYAngle));
            rows.Add(Sky("Sun height", SunHeight, 0f, 90f, 1f, 0, e => e.sunPeakAngle));
            rows.Add(Sky("Moon phase", MoonPh, 0f, 1f, 0.01f, 2, e => e.moonPhase));
            sections.Add(("Time of day", rows, null));
        }
        {
            var sh = EvalShake();
            void SetShake(Action<KfData> a) { var k = SelOrCreate(); if (!k.D.ShakeOn) { k.D.Shake = sh.Amp; k.D.Hz = sh.Hz > 0 ? sh.Hz : 3; k.D.RotPct = sh.Amp > 0 ? sh.Rot * 100 : 100; k.D.PosPct = sh.Amp > 0 ? sh.Pos * 100 : 100; k.D.RollPct = sh.Amp > 0 ? sh.Roll * 100 : 20; k.D.Smoothness = sh.Amp > 0 ? sh.Smooth * 100 : 80; } a(k.D); k.D.ShakeOn = true; Touch(k.D, false, true); }
            var on = sh.Amp > 0;
            sections.Add(("Camera Shake", new()
            {
                Sl("Strength", sh.Amp, 0f, 1f, 0.01f, 2, on, v => SetShake(k => k.Shake = v), () => SetShake(k => k.Shake = 0)),
                Sl("Speed (Hz)", sh.Hz, 0.5f, 20f, 0.5f, 1, on, v => SetShake(k => k.Hz = v), () => SetShake(k => k.Hz = 3)),
                Sl("Smoothness (%)", sh.Smooth * 100, 0f, 100f, 1f, 0, on, v => SetShake(k => k.Smoothness = v), () => SetShake(k => k.Smoothness = 80)),
                Sl("Rotation (%)", sh.Rot * 100, 0f, 100f, 1f, 0, on, v => SetShake(k => k.RotPct = v), () => SetShake(k => k.RotPct = 100)),
                Sl("Position (%)", sh.Pos * 100, 0f, 100f, 1f, 0, on, v => SetShake(k => k.PosPct = v), () => SetShake(k => k.PosPct = 100)),
                Sl("Roll (%)", sh.Roll * 100, 0f, 100f, 1f, 0, on, v => SetShake(k => k.RollPct = v), () => SetShake(k => k.RollPct = 20)),
            }, null));
        }
        if (HankModPack.ReplayCameraMod.Instance is { } rcm)
        {
            // The replay camera's own settings (the same sliders as the Follow cam / POV tabs of the keyframe panel).
            List<WRow> CamRows(params (string Label, string Key, float Min, float Max, float Step, int Dec)[] defs)
                => defs.Select(d => Sl(d.Label, (float)rcm.GetDouble(d.Key), d.Min, d.Max, d.Step, d.Dec, false, v => { rcm.Set(d.Key, v); KfMark(); })).ToList();
            WRow Ch(string label, string key, params string[] opts)
            {
                var cur = rcm.GetString(key);
                var i = Math.Max(0, Array.IndexOf(opts, cur));
                return new(label, "◄ " + cur + " ►", -1f, false, (d, m) => { rcm.Set(key, opts[((i + Math.Sign(d)) % opts.Length + opts.Length) % opts.Length]); KfMark(); }, null, () => { rcm.Set(key, opts[(i + 1) % opts.Length]); KfMark(); });
            }
            WRow Tg(string label, string key)
            {
                var on = rcm.GetBool(key);
                return new(label, on ? "ON" : "OFF", -1f, on, (d, m) => { rcm.Set(key, d > 0); KfMark(); }, null, () => { rcm.Set(key, !on); KfMark(); });
            }
            var follow = new List<WRow> { Ch("Shot preset", "cam_style", "Behind", "Side (right)", "Side (left)", "Front", "Drone", "Skate fisheye", "Low chase", "In-game camera", "In-game drone", "Custom") };
            follow.AddRange(CamRows(
                ("Angle around the rider", "cam_angle", 0f, 360f, 5f, 0), ("Orbit (deg / s)", "cam_sweep", -90f, 90f, 1f, 0), ("Distance (m)", "cam_dist", 0.8f, 30f, 0.1f, 1), ("Height (m)", "cam_height", -2f, 15f, 0.1f, 1),
                ("Aim height (m)", "cam_aim", -0.5f, 2f, 0.05f, 2), ("Tilt (deg)", "cam_roll", -45f, 45f, 1f, 0), ("Keep the rider in frame %", "cam_focus", 0f, 100f, 5f, 0),
                ("Reactivity", "cam_react", 0.5f, 12f, 0.5f, 1), ("Lead-ahead %", "cam_lead", 0f, 100f, 5f, 0), ("Pull back with speed %", "cam_pull", 0f, 100f, 5f, 0),
                ("Hand-held shake %", "cam_shake", 0f, 100f, 5f, 0)));
            sections.Add(("Follow cam", follow, null));
            var pov = new List<WRow>
            {
                // Same button as the keyframe panel's POV tab: A (or a click) learns the body axes again.
                new("Learn the body axes", "press A", -1f, false, null, null, () => { try { rcm.Recalibrate(); Log("TLFX: effect window: body axes learned"); } catch (Exception e) { LogOnce("learn-axes", e); } }),
                Ch("POV preset", "pov_preset", "Realistic (look ahead)", "GoPro helmet", "Chest cam", "Cinematic", "In-game camera", "Custom"),
                Ch("Lock", "pov_lock", "Real (look where you go)", "Head (locked)", "Head (level horizon)", "Travel direction"),
                Ch("Aim from", "pov_base", "Shoulders", "Chest", "Hips", "Head"),
            };
            pov.AddRange(CamRows(
                ("Smoothing %", "pov_smooth", 0f, 95f, 5f, 0), ("Stabilizer %", "pov_stab", 0f, 100f, 5f, 0), ("Speed FOV boost", "pov_boost", 0f, 30f, 1f, 0),
                ("Head shake %", "pov_shake", 0f, 100f, 5f, 0), ("Eye forward (m)", "pov_fwd", -0.3f, 0.6f, 0.01f, 2), ("Eye up (m)", "pov_up", -0.6f, 0.4f, 0.01f, 2),
                ("Max look up / down", "pov_maxpitch", 5f, 90f, 1f, 0), ("Look along the slope %", "pov_slope", 0f, 100f, 5f, 0), ("Torso lean influence %", "pov_headpitch", 0f, 100f, 5f, 0),
                ("Body turn influence %", "pov_headyaw", 0f, 100f, 5f, 0), ("Over-the-shoulder look in switch %", "pov_shoulder", 0f, 100f, 5f, 0),
                ("Max head turn (deg)", "pov_neck", 30f, 180f, 5f, 0), ("Calm the view in the air %", "pov_airstab", 0f, 100f, 5f, 0)));
            pov.Add(Tg("Hide the rider's head and helmet", "pov_hide"));
            pov.Add(Tg("Fisheye", "pov_fisheye"));
            sections.Add(("POV", pov, null));
        }
        foreach (var g in new[] { "Lens & Focus", "Exposure & Tone", "Color", "Blur", "Stylize" })
        {
            var rows = new List<WRow>();
            var members = fx.Where(p => GroupOfFx(p.Label) == g).ToList();
            foreach (var p0 in members)
            {
                var p = p0;
                float v; try { v = FxRead(p); } catch { continue; }
                var hasOpts = p.IsInt && p.Options is { Length: > 0 };
                var txt = hasOpts ? "◄ " + p.Options![Math.Clamp((int)MathF.Round(v), 0, p.Options.Length - 1)] + " ►" : v.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
                void Set(float nv)
                {
                    nv = Math.Clamp(nv, p.Min, p.Max); if (p.IsInt) nv = MathF.Round(nv);
                    var k = SelOrCreate(); k.D.Fx[p.Label] = nv; dirty = true; fxPreviewUntil = Unity.Time + 1.5f;
                    try { FxWrite(p, nv); PushFx(p.IsInt); } catch { }
                    SyncKeyframes();
                }
                var step = p.IsInt ? 1f : Math.Max((p.Max - p.Min) / 100f, 0.0001f);
                rows.Add(new(p.Label.Split('/').Last(), txt, hasOpts || !(p.Max > p.Min) ? -1f : Math.Clamp((v - p.Min) / (p.Max - p.Min), 0, 1), Math.Abs(v - p.Default) > 1e-3f,
                    (d, m) => Set(v + d * step * (p.IsInt ? 1f : m)), () => Set(p.Default),
                    p.IsInt && p.Options is { Length: > 1 } oo ? () => Set(v + 1 >= oo.Length ? 0 : v + 1) : null,
                    hasOpts ? null : f => Set(p.Min + f * (p.Max - p.Min))));
            }
            if (rows.Count > 0) sections.Add((g, rows, members));
        }

        // Same order as the game's keyframe panel: Alpine Vision (live) comes last.
        if (FindMod("alpinevision") is { } alp)
        {
            // Alpine Vision (the other mod), changed live. SetSetting waits for the main thread, which is this one: it is sent from another thread.
            void PutAlp(string key, System.Text.Json.Nodes.JsonNode v) { KfMark(); RememberAlp(alp, key); alpineChanges.Enqueue(() => { try { alp.SetSetting(key, v); } catch (Exception e) { LogOnce("alpine-set", e); } }); }
            var rows = new List<WRow>();
            foreach (var st in alp.Settings.ToList())
            {
                var set = st;
                switch (set.Kind)
                {
                    case SettingKind.Toggle:
                        var on = alp.GetBool(set.Key);
                        rows.Add(new(set.Label, on ? "ON" : "OFF", -1f, on, (d, m) => PutAlp(set.Key, System.Text.Json.Nodes.JsonValue.Create(d > 0)!), null, () => PutAlp(set.Key, System.Text.Json.Nodes.JsonValue.Create(!on)!)));
                        break;
                    case SettingKind.Slider:
                        var dec = set.Step >= 1 ? 0 : set.Step >= 0.1 ? 1 : 2;
                        rows.Add(Sl(set.Label, (float)alp.GetDouble(set.Key), (float)set.Min, (float)set.Max, (float)Math.Max(set.Step, 0.0001), dec, false,
                            v => PutAlp(set.Key, System.Text.Json.Nodes.JsonValue.Create((double)v)!)));
                        break;
                    case SettingKind.Choice:
                        var opts = set.Options.ToArray(); var cur = alp.GetString(set.Key);
                        var ix = Math.Max(0, Array.IndexOf(opts, cur));
                        if (opts.Length > 0) rows.Add(new(set.Label, "◄ " + cur + " ►", -1f, false, (d, m) => PutAlp(set.Key, System.Text.Json.Nodes.JsonValue.Create(opts[((ix + Math.Sign(d)) % opts.Length + opts.Length) % opts.Length])!), null, () => PutAlp(set.Key, System.Text.Json.Nodes.JsonValue.Create(opts[(ix + 1) % opts.Length])!)));
                        break;
                }
            }
            if (rows.Count > 0) sections.Add(("Alpine Vision", rows, null));
        }

        var tabs = sections.Count;
        fxTab = Math.Clamp(fxTab, 0, tabs - 1);
        // Is the tab part of the keyframe under the playhead? (the dot of the list)
        bool InKey(string tab) { var bit = Array.FindIndex(TabColors, t => t.Tab == tab); return Selected() is { } k && bit >= 0 && (TabMask(k.D) & (1 << bit)) != 0; }

        // ---- Gamepad
        if (!fxInTab)
        {
            fxSel = Math.Clamp(fxSel + inp.Move, 0, tabs - 1);
            fxTab = fxSel;
            if (inp.A || inp.Dir != 0 && inp.Mult == 1) { if (inp.A) { fxInTab = true; fxSel = 0; } }
            if (inp.B) { fxOpen = false; fxView.Hide(); fxWinSig = ""; return; }
        }
        else
        {
            if (inp.TabStep != 0) { fxTab = ((fxTab + inp.TabStep) % tabs + tabs) % tabs; fxSel = 0; }
            var rowsT = sections[fxTab].Rows;
            fxSel = Math.Clamp(fxSel + inp.Move, 0, rowsT.Count - 1);
            var row = rowsT[fxSel];
            if (inp.Dir != 0) row.Adjust?.Invoke(inp.Dir, inp.Mult);
            if (inp.A) row.Next?.Invoke();
            if (inp.B) { fxInTab = false; fxSel = fxTab; }
        }
        if (inp.X) { AddTab(sections[fxTab].Title, sections[fxTab].Members); Log($"TLFX: effect window: '{sections[fxTab].Title}' keyframe added"); }
        if (inp.Y) ClearTab(sections[fxTab].Title);
        // L3: copy this tab of the keyframe to the next one; R3: to all the keyframes after it.
        if (inp.L3) { try { DuplicateTab(sections[fxTab].Title, false); } catch (Exception e) { LogOnce("dup1", e); } }
        if (inp.R3) { try { DuplicateTab(sections[fxTab].Title, true); } catch (Exception e) { LogOnce("dup2", e); } }

        // ---- Mouse
        var rowsNow = sections[fxTab].Rows;
        if (HankModPack.Pointer.Position(out var mx, out var my))
        {
            if (HankModPack.Pointer.Down(0) && fxView.HitTest(mx, my) is { } hit)
            {
                if (!fxInTab)
                {
                    if (hit.Row >= 1 && hit.Row <= tabs) { fxTab = fxSel = hit.Row - 1; fxInTab = true; fxSel = 0; }
                }
                else if (hit.Row == 0)
                {
                    if (hit.Fx < 0.33f) { fxTab = ((fxTab - 1) % tabs + tabs) % tabs; fxSel = 0; }
                    else if (hit.Fx > 0.67f) { fxTab = (fxTab + 1) % tabs; fxSel = 0; }
                    else { fxInTab = false; fxSel = fxTab; }
                    return;
                }
                else
                {
                    var idx = hit.Row - 1;
                    if (idx >= 0 && idx < rowsNow.Count)
                    {
                        fxSel = idx; var r = rowsNow[idx];
                        if (r.SetTo != null) { fxDragRow = idx; r.SetTo(hit.Bar); }
                        else if (r.Next != null) r.Next();
                        else r.Adjust?.Invoke(hit.Fx < 0.5f ? -1 : 1, 1);
                    }
                }
            }
            else if (HankModPack.Pointer.Held(0) && fxDragRow >= 0 && fxInTab && fxDragRow < rowsNow.Count)
            {
                if (fxView.HitTest(mx, my) is { } drag) rowsNow[fxDragRow].SetTo?.Invoke(drag.Bar);
            }
            else if (!HankModPack.Pointer.Held(0)) fxDragRow = -1;
        }

        // ---- Drawing
        var list = new List<HankModPack.QuickMenuOverlay.Row>();
        int count;
        if (!fxInTab)
        {
            list.Add(new(HankModPack.PadBtn.A, "", "", false, false, HasIcon: false, Small: true));
            for (var i = 0; i < tabs; i++)
                list.Add(new(HankModPack.PadBtn.A, sections[i].Title, "", false, i == fxSel, HasIcon: false, Value: InKey(sections[i].Title) ? "●" : "·"));
            count = tabs;
            list.Add(new(HankModPack.PadBtn.A, "", "", false, false, HasIcon: false, Small: true, Legend: new[] { (HankModPack.PadBtn.DpadDown, "choose"), (HankModPack.PadBtn.A, "open"), (HankModPack.PadBtn.B, "close") }));
            list.Add(new(HankModPack.PadBtn.A, "", "", false, false, HasIcon: false, Small: true, Legend: new[] { (HankModPack.PadBtn.X, "add keyframe"), (HankModPack.PadBtn.Y, "delete keyframe") }));
            list.Add(new(HankModPack.PadBtn.A, "", "", false, false, HasIcon: false, Small: true, Legend: new[] { (HankModPack.PadBtn.L3, "duplicate"), (HankModPack.PadBtn.R3, "duplicate all") }));
        }
        else
        {
            list.Add(new(HankModPack.PadBtn.A, $"◄   {sections[fxTab].Title}   ►", InKey(sections[fxTab].Title) ? "KEY" : "", true, false, HasIcon: false));
            for (var i = 0; i < rowsNow.Count; i++)
                list.Add(new(HankModPack.PadBtn.A, rowsNow[i].L + (rowsNow[i].Mod ? "  •" : ""), "", false, i == fxSel, HasIcon: false, Value: rowsNow[i].V, Bar: rowsNow[i].Bar));
            count = rowsNow.Count;
            list.Add(new(HankModPack.PadBtn.A, "", "", false, false, HasIcon: false, Small: true, Legend: new[] { (HankModPack.PadBtn.DpadDown, "choose / change"), (HankModPack.PadBtn.A, "next"), (HankModPack.PadBtn.B, "back") }));
            list.Add(new(HankModPack.PadBtn.A, "", "", false, false, HasIcon: false, Small: true, Legend: new[] { (HankModPack.PadBtn.X, "add keyframe"), (HankModPack.PadBtn.Y, "delete keyframe") }));
            list.Add(new(HankModPack.PadBtn.A, "", "", false, false, HasIcon: false, Small: true, Legend: new[] { (HankModPack.PadBtn.L3, "duplicate"), (HankModPack.PadBtn.R3, "duplicate all") }));
            list.Add(new(HankModPack.PadBtn.A, "", "", false, false, HasIcon: false, Small: true, Legend: new[] { (HankModPack.PadBtn.LB, ""), (HankModPack.PadBtn.RB, "tab"), (HankModPack.PadBtn.RT, "hold: more") }));
        }
        var style = HankModPack.QuickMenuMod.Instance?.IconStyle == "PlayStation" ? HankModPack.PadStyle.PlayStation : HankModPack.PadStyle.Xbox;
        var percent = Math.Min(55f, 82000f / (66 + 14 + 62 * (1 + count) + 180));   // small: the game stays visible
        fxView.Show("REPLAY EFFECTS", list, style, percent, 100, HankModPack.HudThemes.WithWindow(HankModPack.HankHudMod.Instance?.CurrentLook), 560);
        fxWinSig = "open";
    }

    void TimelineStrip()
    {
        if (stripBroken) return;
        if (!inReplay || !Enabled || !StripOn) { if (strip.Count > 0) ClearStrip(); return; }
        var rec = Recorder.Instance.Raw;
        var handle = rec.trimStartHandle;
        if (handle.Ptr == 0 || handle.bar == 0 || handle.circle == 0) return;
        if (!ReadBar(handle.bar)) return;
        ReadFullFrames(new TrimHandleRef(rec.trimEndHandle.Ptr == 0 ? 0 : rec.trimEndHandle.circle));
        var barTr = Unity.Transform(handle.bar);
        if (barTr != stripParent) ClearStrip();
        stripParent = barTr; stripSource = handle.circle;

        var total = Math.Max(TotalFrames(), 1);
        var sel = Selected();
        var sig = string.Join(";", keys.Select(k => $"{k.Frame}:{TabMask(k.D)}")) + $"|{sel?.Frame}|{total}|{fullFrames:0}|{StripY}|{screenH:0}|{gCenterX:0}|{gCenterY:0}|{gWidthPx:0}|{gScale:0.###}";
        if (sig == stripSig && strip.Count > 0)
        {
            MoveHead(total);
            return;
        }
        // Dragging the game's trim handles changes the length every frame: rebuilding the strip that often crashed the game. At most ~3 times a second.
        if (strip.Count > 0 && Environment.TickCount64 < nextStripBuild)
        {
            MoveHead(total);
            return;
        }
        nextStripBuild = Environment.TickCount64 + 350;
        foreach (var m in strip) { try { if (!Unity.IsNull(m)) Unity.Destroy(Unity.Call(m, "get_gameObject")); } catch { } }
        strip.Clear(); stripHead = 0;
        stripSig = sig;

        // Children of the game's bar (so they show with it), but placed in screen space below it, never on top of it.
        var stripPx = StripHeightPx;
        var y = StripCenterY();
        Log($"TLFX: FX timeline: screen height {screenH:0}, game bar centre=({gCenterX:0},{gCenterY:0}) size={gWidthPx:0}x{gHeightPx:0} scale={gScale:0.##}; strip centre y={y:0} height={stripPx:0}px");
        var back = new Color(0.30f, 0.30f, 0.30f, 1f);
        try { back = Unity.Call<Color>(handle.bar, "get_color"); back.A = 1f; } catch { }
        MakeMarker(barTr, new Vector3(gCenterX, y, gZ), back, stripPx / gScale, gWidthPx / gScale, true);
        foreach (var k in keys)
        {
            var f = Frac(k.Frame);
            var isSel = ReferenceEquals(k, sel);
            var mask = TabMask(k.D);
            var slices = new List<Color>();
            for (var t = 0; t < TabColors.Length; t++) if ((mask & (1 << t)) != 0) slices.Add(TabColors[t].C);
            if (slices.Count == 0) slices.Add(new Color(0.6f, 0.6f, 0.6f, 1f));
            var dPx = stripPx * 0.9f;
            var pos = new Vector3(XAt(f), y, gZ);
            // Round icon: an outline (white when this keyframe is under the playhead), then one pie slice per tab used.
            MakeMarker(barTr, pos, isSel ? new Color(1f, 1f, 1f, 1f) : new Color(0.1f, 0.1f, 0.1f, 1f), (dPx + 4) / gScale, (dPx + 4) / gScale);
            for (var i = 0; i < slices.Count; i++)
            {
                var img = MakeMarker(barTr, pos, slices[i], dPx / gScale, dPx / gScale);
                if (img != 0) MakePie(img, slices.Count, i);
            }
        }
        stripHead = MakeMarker(barTr, new Vector3(XAt(0), y, gZ), new Color(1f, 0.62f, 0.1f, 1f), (stripPx * 1.3f) / gScale, Math.Max(stripPx * 0.14f, 3f) / gScale, true);
        MoveHead(total);
    }

    /// <summary>Keeps the playhead of the FX timeline on the game's playhead.</summary>
    void MoveHead(int total)
    {
        if (stripHead == 0 || Unity.IsNull(stripHead)) return;
        var f = Frac(curFrame);
        var p = new Vector3(XAt(f), StripCenterY(), gZ);
        Unity.Call(Unity.Transform(stripHead), "set_position", (nint)(&p));
    }
}
