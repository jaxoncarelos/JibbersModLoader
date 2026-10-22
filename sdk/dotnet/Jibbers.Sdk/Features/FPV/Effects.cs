using System.Globalization;
using System.Runtime.InteropServices;
using Jibbers.Sdk;
using Jibbers.Sdk.Interop;

namespace Jibbers.Sdk.Features.Fpv;

internal readonly record struct Osd(Mode Mode, float Power, float Speed, float? Altitude, float FlightTime,
    string? Locked, bool Disarmed, bool Paused, bool Gamepad);

internal sealed unsafe class Effects : IDisposable
{
    GameRef? root, motor, wind, crash;
    readonly List<GameRef> texts = new();
    float wait, motorPitch = 0.4f, motorLevel;
    public static string[] Text(Osd o)
    {
        const string box = "<mark=#00000080 padding=\"14,14,6,6\">";
        var power = float.IsFinite(o.Power) ? Math.Clamp(o.Power, 0, 1) : 0;
        var bars = (int)MathF.Round(power * 10);
        var secs = (int)MathF.Max(o.FlightTime, 0);
        // Rider names are untrusted text, not TMP markup.
        var name = o.Locked?.Replace("<", "〈").Replace(">", "〉");
        var top = new List<string>();
        if (o.Paused) top.Add("PAUSED");
        if (name is not null) top.Add("<color=#7fe0ff>LOCK  " + name + "</color>");
        if (o.FlightTime < 8) top.Add(o.Gamepad ? "L3+R3 land   LB mode   RB lock (hold: release)   Y return"
            : "F9 land   F10 mode   Middle mouse lock   Home return");
        var altitude = o.Altitude?.ToString("F1", CultureInfo.InvariantCulture) ?? "---";
        return new[]
        {
            $"{box}{o.Mode.ToString().ToUpperInvariant()}{(o.Disarmed ? "  <color=#ff5050>DISARMED</color>" : "")}\nTHR [{new string('|', bars)}{new string('.', 10 - bars)}] {power * 100:F0}%</mark>",
            FormattableString.Invariant($"{box}{o.Speed * 3.6f:F0} km/h\nALT {altitude} m\n{secs / 60:00}:{secs % 60:00}</mark>"),
            top.Count > 0 ? box + string.Join("\n", top) + "</mark>" : "",
            o.Mode == Mode.Cine ? "" : "<alpha=#B0>+",
        };
    }
    public Effects(bool hud, bool sound, Action<string> log)
    {
        root = NewObject("JibbersFPV");
        if (hud)
        {
            try { BuildHud(); } catch (Exception e) { log("no OSD: " + e.Message); foreach (var t in texts) { Unity.Destroy(Unity.Call(t.Target, "get_gameObject")); t.Dispose(); } texts.Clear(); }
        }
        if (sound)
        {
            try { BuildSound(); }
            catch (Exception e)
            {
                log("no sound: " + e.Message);
                motor?.Dispose(); wind?.Dispose(); crash?.Dispose(); motor = wind = crash = null;
                // Destroy partially initialized AudioSources so play-on-awake cannot keep sounding.
                foreach (var source in Unity.GetComponentsInChildren(root.Target, Unity.Class("UnityEngine.AudioSource"), true))
                    Unity.Destroy(Unity.Call(source, "get_gameObject"));
            }
        }
    }
    static GameRef NewObject(string name)
    {
        var k = Unity.Class("UnityEngine.GameObject");
        var obj = k.New();
        var reference = new GameRef(obj);
        try { k.Method(".ctor", "System.String").Invoke(obj, Il2Cpp.String(name)); return reference; }
        catch { reference.Dispose(); throw; }
    }
    static nint Add(nint go, string type) => Il2Cpp.ClassOf(go).Method("AddComponent", "System.Type").Invoke(go, Unity.Class(type).SystemType);
    static void Float(nint obj, string name, float value) => Unity.Call(obj, name, (nint)(&value));
    static void Int(nint obj, string name, int value) => Unity.Call(obj, name, (nint)(&value));
    static void Bool(nint obj, string name, bool value) { var b = (byte)(value ? 1 : 0); Unity.Call(obj, name, (nint)(&b)); }
    static void Vec(nint obj, string name, Vector2 value) => Unity.Call(obj, name, (nint)(&value));
    void BuildHud()
    {
        var go = root!.Target;
        var canvas = Add(go, "UnityEngine.Canvas"); Int(canvas, "set_renderMode", 0); Int(canvas, "set_sortingOrder", 900);
        var scaler = Add(go, "UnityEngine.UI.CanvasScaler"); Int(scaler, "set_uiScaleMode", 1);
        Vec(scaler, "set_referenceResolution", new(1920, 1080)); Float(scaler, "set_matchWidthOrHeight", 0.5f);
        var font = Unity.FindObjects(Unity.Class("TMPro.TMP_Text")).Where(t => !Unity.IsNull(t))
            .Select(t => Unity.Call(t, "get_font")).FirstOrDefault(f => !Unity.IsNull(f));
        var blocks = new[] { (new Vector2(0, 0), new Vector2(40, 40), "BottomLeft", 30f),
            (new Vector2(1, 0), new Vector2(-40, 40), "BottomRight", 30f),
            (new Vector2(0.5f, 1), new Vector2(0, -40), "Top", 26f),
            (new Vector2(0.5f, 0.5f), new Vector2(0, 0), "Center", 44f) };
        foreach (var (anchor, offset, alignment, size) in blocks)
        {
            using var child = NewObject("FPV OSD");
            var text = Add(child.Target, "TMPro.TextMeshProUGUI");
            var rt = Unity.Call(text, "get_rectTransform");
            var keep = (byte)0;
            Il2Cpp.ClassOf(rt).Method("SetParent", "UnityEngine.Transform", "System.Boolean").Invoke(rt, Unity.Transform(go), (nint)(&keep));
            texts.Add(new GameRef(text));
            if (font != 0) Unity.Call(text, "set_font", font);
            Float(text, "set_fontSize", size); Bool(text, "set_richText", true); Bool(text, "set_raycastTarget", false);
            var align = Unity.Class("TMPro.TextAlignmentOptions").Field(alignment).Get<int>(0);
            Int(text, "set_alignment", align);
            if (!Il2Cpp.ClassOf(text).Method("set_textWrappingMode", 1).IsNull) Int(text, "set_textWrappingMode", 0);
            else Bool(text, "set_enableWordWrapping", false);
            var color = new Color(1, 1, 1, 1); Unity.Call(text, "set_color", (nint)(&color));
            Vec(rt, "set_anchorMin", anchor); Vec(rt, "set_anchorMax", anchor); Vec(rt, "set_pivot", anchor);
            Vec(rt, "set_anchoredPosition", offset); Vec(rt, "set_sizeDelta", new(900, 200));
        }
    }
    [StructLayout(LayoutKind.Sequential)] struct SpanWrapper { public char* Begin; public int Length; }
    static nint Unmarshal(nint handle)
    {
        if (handle == 0) return 0;
        var boxed = Il2Cpp.RequireClass(Unity.Core, "UnityEngine.Bindings.Unmarshal").Method("FromIntPtrUnsafe", 1).Invoke(0, (nint)(&handle));
        var k = Il2Cpp.ClassOf(boxed); var data = (nint)Il2CppApi.object_unbox(boxed);
        try { return k.Method("get_Target", 0).Invoke(data); }
        finally { k.Method("Free", 0).Invoke(data); }
    }
    void BuildSound()
    {
        var path = Path.Combine(Bootstrap.ModsDir, "jibbers_fpv.bundle");
        if (!File.Exists(path)) throw new FileNotFoundException("jibbers_fpv.bundle missing from JibbersMods");
        var load = (delegate* unmanaged<SpanWrapper*, uint, ulong, nint>)Il2Cpp.ICall("UnityEngine.AssetBundle::LoadFromFile_Internal_Injected");
        if (load == null) throw new InvalidOperationException("AssetBundle loading is unavailable");
        nint bundle;
        fixed (char* chars = path) { var span = new SpanWrapper { Begin = chars, Length = path.Length }; bundle = Unmarshal(load(&span, 0, 0)); }
        if (Unity.IsNull(bundle)) throw new InvalidOperationException("Could not load FPV audio bundle");
        using var bundleRef = new GameRef(bundle);
        nint audio;
        try
        {
            var prefab = Il2Cpp.ClassOf(bundle).Method("LoadAsset", "System.String", "System.Type").Invoke(bundle, Il2Cpp.String("JibbersFPVAudio"), Unity.Class("UnityEngine.GameObject").SystemType);
            if (Unity.IsNull(prefab)) throw new InvalidOperationException("JibbersFPVAudio prefab missing");
            audio = Unity.Class("UnityEngine.Object").Method("Instantiate", "UnityEngine.Object", "UnityEngine.Transform").Invoke(0, prefab, Unity.Transform(root!.Target));
        }
        finally
        {
            var unload = (delegate* unmanaged<nint, byte, void>)Il2Cpp.ICall("UnityEngine.AssetBundle::Unload_Injected");
            var native = Il2Cpp.ClassOf(bundle).Field("m_CachedPtr").Get<nint>(bundle);
            if (unload != null && native != 0) unload(native, 0);
        }
        var tr = Unity.Transform(audio);
        GameRef Source(string name)
        {
            var child = Unity.Call(tr, "Find", Il2Cpp.String(name));
            if (Unity.IsNull(child)) throw new InvalidOperationException("FPV audio source missing: " + name);
            var source = Unity.GetComponent(child, Unity.Class("UnityEngine.AudioSource"));
            if (Unity.IsNull(source)) throw new InvalidOperationException("No AudioSource on " + name);
            return new(source);
        }
        motor = Source("Motor"); wind = Source("Wind"); crash = Source("Crash");
        Float(motor.Target, "set_volume", 0); Float(wind.Target, "set_volume", 0);
        Unity.Call(motor.Target, "Play"); Unity.Call(wind.Target, "Play");
    }
    public void Update(float dt, Osd o, float volume, bool crashed)
    {
        if (motor is not null && wind is not null && crash is not null)
        {
            var (pitch, level) = Synth.MotorVoice(o.Power, !o.Disarmed);
            var k = DroneMath.Blend(dt, o.Disarmed ? 0.35f : 0.06f);
            motorPitch += (pitch - motorPitch) * k; motorLevel += (level - motorLevel) * k;
            var mute = o.Paused ? 0 : Math.Clamp(volume, 0, 1);
            Float(motor.Target, "set_pitch", motorPitch); Float(motor.Target, "set_volume", motorLevel * mute * 0.6f);
            var (wp, wl) = Synth.WindVoice(o.Speed);
            Float(wind.Target, "set_pitch", wp); Float(wind.Target, "set_volume", wl * mute * 0.5f);
            if (crashed && mute > 0) { Float(crash.Target, "set_volume", mute); Unity.Call(crash.Target, "Play"); }
        }
        wait -= dt;
        if (wait > 0 || texts.Count == 0) return;
        wait = 1 / 15f;
        var blocks = Text(o);
        for (var i = 0; i < texts.Count; i++) Unity.Call(texts[i].Target, "set_text", Il2Cpp.String(blocks[i]));
    }
    public void Dispose()
    {
        try { if (root is not null) Unity.Destroy(root.Target); }
        finally
        {
            foreach (var t in texts) t.Dispose(); texts.Clear();
            motor?.Dispose(); wind?.Dispose(); crash?.Dispose(); root?.Dispose();
            motor = wind = crash = root = null;
        }
    }
}
