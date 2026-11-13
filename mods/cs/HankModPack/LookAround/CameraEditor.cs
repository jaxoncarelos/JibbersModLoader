using System.Diagnostics;
using System.Text.Json.Nodes;
using Jibbers.Sdk;
using Jibbers.Sdk.Interop;

namespace HankModPack;

/// <summary>
/// The camera editor: D-pad down held + the editor button (View / Share) opens a window on the left with one tab per play camera (POV, Follow cam, Drone)
/// and their options. Driven by the pad: L1 / R1 change the tab, D-pad up / down the option, left / right change it (hold to go faster), A switches
/// the camera on or off / toggles, Y resets the option, B or View / Share closes. Changes are live, and the tab you are on is the camera you see in game.
/// </summary>
public sealed unsafe partial class LookAroundMod
{

    static readonly string[] EditorCameras = { "POV", "Follow cam", "Drone" };
    const int EditorWindow = 40;
    const int Hdr = 6;     // rows above the options: camera on / off, preset, save, save as new, rename, delete

    bool editorOpen, editorInit;
    int edTab, edSel, edTop, edRepeats;
    PadButton edPrev;
    long edNextRepeat;
    bool edMuted;
    // Not exactly 0: the game (and the camera code) never divides by a zero time step, yet the rider does not move.
    const float FrozenScale = 0.0001f;
    float edTimeScale = 1f;
    bool edFrozen;

    static float GetTimeScale() => Il2Cpp.Unbox<float>(Unity.CallStatic(Unity.Class("UnityEngine.Time"), "get_timeScale"));
    static void SetTimeScale(float v) => Unity.CallStatic(Unity.Class("UnityEngine.Time"), "set_timeScale", (nint)(&v));

    /// <summary>Stops the game while the editor is open (the rider would carry on skiing with no one in control); the view stays as it is.</summary>
    void Freeze()
    {
        if (!EditorFreeze) return;
        try
        {
            var cur = GetTimeScale();
            if (!edFrozen) { edTimeScale = cur; edFrozen = true; Log("camera editor: game stopped"); }
            if (cur > 0.0002f || cur < 0.00005f) SetTimeScale(FrozenScale);
        }
        catch (Exception e) { Once("editor-freeze", e); }
    }

    void Unfreeze()
    {
        if (!edFrozen) return;
        edFrozen = false;
        try { if (Math.Abs(GetTimeScale() - FrozenScale) < 0.00001f) SetTimeScale(edTimeScale); Log("camera editor: game running again"); }
        catch (Exception e) { Once("editor-unfreeze", e); }
    }

    void OpenEditor()
    {
        if (PlayCamerasMod.Instance is not { } pc) return;
        Log("camera editor: open");
        menuOpen = false; lingerEnd = 0; ReleaseSticky();
        overlay.Hide();
        Stop("editor");
        needRelease = true;
        editorOpen = true; editorInit = false;
        PlayCamerasMod.EditorHold = true;
        Freeze();
        edTab = Math.Max(0, Array.IndexOf(EditorCameras, pc.CurrentName));
        edSel = 0; edTop = 0;
        // With the game stopped the controls are off (the rider cannot be steered anyway); when it keeps running they stay on, so the rider can still be skied.
        // Always muted: the D-pad / buttons used in the editor must not reach the game (D-pad up opens the game's spawn selector).
        { try { mute.Mute(); edMuted = true; } catch (Exception e) { Once("editor-mute", e); } }
        if (pc.CurrentName != EditorCameras[edTab]) pc.Select(EditorCameras[edTab]);
    }

    void CloseEditor()
    {
        if (!editorOpen) return;
        editorOpen = false;
        PlayCamerasMod.EditorHold = false;
        try { PlayCamerasMod.Instance?.CommitLive(); } catch { }
        Unfreeze();
        overlay.Hide();
        if (edMuted) { try { mute.Restore(); } catch { } edMuted = false; }
        needRelease = true;
        // The stance must not change when the buttons are let go: keep guarding for a short moment.
        Log("camera editor: closed");
    }

    List<Setting> EditorSettings(string camera) =>
        PlayCamerasMod.Instance!.Settings.Where(s => s.Section == camera && s.Kind is SettingKind.Slider or SettingKind.Toggle or SettingKind.Choice).ToList();

    void EditorTick(Pads pad, long now)
    {
        var pc = PlayCamerasMod.Instance;
        if (pc is null || PlayCamerasMod.UiOpen) { CloseEditor(); return; }
        Freeze();     // the game may set its speed back
        if (!editorInit) { edPrev = pad.Buttons; editorInit = true; }
        var edges = pad.Buttons & ~edPrev;
        edPrev = pad.Buttons;
        bool Hit(PadButton b) => (edges & b) != 0;
        bool Held(PadButton b) => (pad.Buttons & b) != 0;
        if (edNaming) { NamingTick(pc, EditorCameras[edTab], edges); return; }

        var cam = EditorCameras[edTab];
        var settings = EditorSettings(cam);
        var count = settings.Count + Hdr;

        if (Hit(PadButton.B) || Hit(PadButton.Back)) { CloseEditor(); return; }
        if (Hit(PadButton.LeftShoulder) || Hit(PadButton.RightShoulder))
        {
            edTab = (edTab + (Hit(PadButton.RightShoulder) ? 1 : EditorCameras.Length - 1)) % EditorCameras.Length;
            edSel = 0; edTop = 0;
            pc.CommitLive();
            pc.Select(EditorCameras[edTab]);     // the tab you open is the camera you see
            cam = EditorCameras[edTab]; settings = EditorSettings(cam); count = settings.Count + Hdr;
            edNaming = false; edConfirmDel = false;
            Log("camera editor: tab " + cam);
        }
        if (Hit(PadButton.DpadDown)) edSel = (edSel + 1) % count;
        if (Hit(PadButton.DpadUp)) edSel = (edSel + count - 1) % count;
        if (edSel < edTop) edTop = edSel;
        if (edSel >= edTop + EditorWindow) edTop = edSel - EditorWindow + 1;

        if (edSel != edLastSel) { edLastSel = edSel; edConfirmDel = false; }
        var sel = edSel < Hdr ? null : settings[edSel - Hdr];
        var dir = 0;
        if (Hit(PadButton.DpadLeft)) { dir = -1; edRepeats = 0; edNextRepeat = now + Stopwatch.Frequency * 35 / 100; }
        else if (Hit(PadButton.DpadRight)) { dir = 1; edRepeats = 0; edNextRepeat = now + Stopwatch.Frequency * 35 / 100; }
        else if ((Held(PadButton.DpadLeft) || Held(PadButton.DpadRight)) && now >= edNextRepeat)
        {
            dir = Held(PadButton.DpadRight) ? 1 : -1;
            edRepeats++;
            edNextRepeat = now + Stopwatch.Frequency * 9 / 100;
        }
        var mult = 1 + edRepeats / 12;

        if (edSel == 0)
        {
            if (Hit(PadButton.A) || dir != 0 && edRepeats == 0) pc.Select(pc.CurrentName == cam ? "Game camera" : cam);
        }
        else if (edSel < Hdr) HeaderAction(pc, cam, settings, edSel, dir, Hit(PadButton.A));
        else if (sel is not null)
        {
            if (dir != 0) Adjust(pc, sel, dir, mult);
            else if (Hit(PadButton.A) && sel.Kind != SettingKind.Slider) Adjust(pc, sel, 1, 1);
            if (Hit(PadButton.Y)) Reset(pc, sel);
        }

        // Draw
        var rows = new List<QuickMenuOverlay.Row>();
        var window = Enumerable.Range(0, count).Skip(edTop).Take(EditorWindow);
        foreach (var i in window)
        {
            var hl = i == edSel;
            if (i == 0)
                rows.Add(new(PadBtn.A, "Use this camera", "", false, hl, HasIcon: false, Value: Wrap(pc.CurrentName == cam ? "ON" : "OFF", hl)));
            else if (i < Hdr)
            {
                var cur = CurPreset(cam);
                var (label, val) = i switch
                {
                    1 => ("Preset", Wrap(cur == "" ? (pc.PresetNames(cam).Count == 0 ? "none saved" : "(none)") : cur, hl)),
                    2 => ("Save preset", cur == "" ? "A: new" : "A: overwrite"),
                    3 => ("Save as a new preset", "A"),
                    4 => ("Rename preset", cur == "" ? "-" : "A"),
                    _ => ("Delete preset", cur == "" ? "-" : edConfirmDel && hl ? "A again" : "A"),
                };
                rows.Add(new(PadBtn.A, label, "", false, hl, HasIcon: false, Value: val));
            }
            else
            {
                var s = settings[i - Hdr];
                var bar = s.Kind == SettingKind.Slider && s.Max > s.Min ? (float)((pc.LiveDouble(s.Key) - s.Min) / (s.Max - s.Min)) : -1f;
                rows.Add(new(PadBtn.A, s.Label, "", false, hl, HasIcon: false, Value: Wrap(ValueText(pc, s), hl), Bar: bar));
            }
        }
        rows.Add(new(PadBtn.A, "", "", false, false, HasIcon: false, Small: true, Legend: new[] { (PadBtn.LB, ""), (PadBtn.RB, "camera"), (PadBtn.DpadDown, "select / change") }));
        rows.Add(new(PadBtn.A, "", "", false, false, HasIcon: false, Small: true, Legend: new[] { (PadBtn.A, "on / off"), (PadBtn.Y, "reset"), (PadBtn.B, "close") }));
        var more = count > EditorWindow ? $"   {edSel + 1}/{count}" : "";
        overlay.Show($"{cam.ToUpperInvariant()}  ({edTab + 1}/{EditorCameras.Length}){more}", rows, CurrentStyle(), Math.Min(MenuScale, 82000f / (66 + 14 + 62 * Math.Min(count, EditorWindow) + 80)), MenuOpacity, HankHudMod.Instance?.CurrentLook, 560);   // smaller text: every setting fits without scrolling
    }

    // ---------------------------------------------------------------- Presets
    bool edNaming, edConfirmDel;
    string edText = "";
    int edLastSel = -1;
    readonly HashSet<int> edKeys = new();
    readonly Dictionary<string, string> edPreset = new();

    string CurPreset(string cam) => edPreset.TryGetValue(cam, out var n) ? n : "";

    static string NewPresetName(List<string> names)
    {
        for (var i = 1; ; i++) { var n = "Preset " + i; if (!names.Contains(n)) return n; }
    }

    void HeaderAction(PlayCamerasMod pc, string cam, List<Setting> settings, int row, int dir, bool a)
    {
        try
        {
            var names = pc.PresetNames(cam);
            var cur = CurPreset(cam);
            switch (row)
            {
                case 1:
                    if (dir != 0 && names.Count > 0)
                    {
                        var i = names.IndexOf(cur);
                        i = i < 0 ? (dir > 0 ? 0 : names.Count - 1) : (i + dir + names.Count) % names.Count;
                        edPreset[cam] = names[i];
                        pc.ApplyPreset(cam, names[i], settings);
                    }
                    break;
                case 2:
                    if (a) { var n = cur != "" && names.Contains(cur) ? cur : NewPresetName(names); pc.SavePreset(cam, n, settings); edPreset[cam] = n; }
                    break;
                case 3:
                    if (a) { var n = NewPresetName(names); pc.SavePreset(cam, n, settings); edPreset[cam] = n; }
                    break;
                case 4:
                    if (a && cur != "") { edNaming = true; edText = cur; edKeys.Clear(); }
                    break;
                case 5:
                    if (a && cur != "")
                    {
                        if (edConfirmDel) { pc.DeletePreset(cam, cur); edPreset.Remove(cam); edConfirmDel = false; }
                        else edConfirmDel = true;
                    }
                    break;
            }
        }
        catch (Exception e) { Once("editor-preset", e); }
    }

    /// <summary>Renaming a preset: typed on the keyboard (letters, digits, space, - _), Enter / A confirms, Esc / B cancels.</summary>
    void NamingTick(PlayCamerasMod pc, string cam, PadButton edges)
    {
        bool Down(int vk) { try { return Input.IsDown((Key)vk); } catch { return false; } }
        bool Edge(int vk)
        {
            var d = Down(vk);
            var was = edKeys.Contains(vk);
            if (d) edKeys.Add(vk); else edKeys.Remove(vk);
            return d && !was;
        }
        var shift = Down(0x10);
        for (var vk = 0x30; vk <= 0x39; vk++) if (Edge(vk) && edText.Length < 24) edText += (char)vk;
        for (var vk = 0x41; vk <= 0x5A; vk++) if (Edge(vk) && edText.Length < 24) edText += shift ? (char)vk : char.ToLowerInvariant((char)vk);
        if (Edge(0x20) && edText.Length < 24) edText += ' ';
        if (Edge(0xBD) && edText.Length < 24) edText += shift ? '_' : '-';
        if (Edge(0x08) && edText.Length > 0) edText = edText[..^1];
        var ok = Edge(0x0D) || (edges & PadButton.A) != 0;
        var cancel = Edge(0x1B) || (edges & PadButton.B) != 0;
        if (ok)
        {
            var name = edText.Trim();
            var old = CurPreset(cam);
            if (name != "" && name != old && pc.RenamePreset(cam, old, name)) edPreset[cam] = name;
            edNaming = false;
        }
        else if (cancel) edNaming = false;
        if (!edNaming) return;
        var rows = new List<QuickMenuOverlay.Row>
        {
            new(PadBtn.A, "Name (type on the keyboard)", "", false, true, HasIcon: false, Value: edText + "_"),
            new(PadBtn.A, "Enter / A  confirm      Esc / B  cancel", "", false, false, HasIcon: false, Small: true),
        };
        overlay.Show("RENAME PRESET", rows, CurrentStyle(), MenuScale, MenuOpacity, HankHudMod.Instance?.CurrentLook, 560);
    }

    static string Wrap(string v, bool selected) => selected ? "< " + v + " >" : v;

    static string ValueText(PlayCamerasMod pc, Setting s) => s.Kind switch
    {
        SettingKind.Toggle => pc.LiveBool(s.Key) ? "ON" : "OFF",
        SettingKind.Choice => pc.LiveString(s.Key),
        _ => pc.LiveDouble(s.Key).ToString(s.Step >= 1 ? "0" : s.Step >= 0.1 ? "0.0" : "0.00", System.Globalization.CultureInfo.InvariantCulture),
    };

    void Adjust(PlayCamerasMod pc, Setting s, int dir, int mult)
    {
        try
        {
            switch (s.Kind)
            {
                case SettingKind.Toggle:
                    pc.SetLive(s.Key, !pc.LiveBool(s.Key));
                    break;
                case SettingKind.Choice:
                {
                    var o = s.Options.ToList();
                    if (o.Count == 0) break;
                    var i = Math.Max(0, o.IndexOf(pc.LiveString(s.Key)));
                    pc.SetLive(s.Key, o[(i + dir + o.Count) % o.Count]);
                    break;
                }
                default:
                {
                    var step = s.Step > 0 ? s.Step : 1;
                    var v = Math.Clamp(Math.Round((pc.LiveDouble(s.Key) + dir * step * mult) / step) * step, s.Min, s.Max);
                    pc.SetLive(s.Key, v);
                    break;
                }
            }
        }
        catch (Exception e) { Once("editor-set", e); }
    }

    void Reset(PlayCamerasMod pc, Setting s)
    {
        try
        {
            object v = s.Kind switch
            {
                SettingKind.Toggle => Convert.ToBoolean(s.Default),
                SettingKind.Choice => Convert.ToString(s.Default) ?? "",
                _ => (object)Convert.ToDouble(s.Default),
            };
            pc.SetLive(s.Key, v);
        }
        catch (Exception e) { Once("editor-reset", e); }
    }
}
