// Reshred (C#): hold X to rewind the rider through the last 6 seconds, with the exact pose, speed and
// game state they had then; let go to carry on from there. Port of the Rust Reshred.
//
// Physics is MuJoCo (Mujoco.MjScene: Model/Data = mjModel*/mjData*). Before every physics step
// (pre-hook on MjScene.FixedUpdate) the full integration state (mj_getState) goes into a ring buffer,
// with the plain int/bool state of the ragdoll controller's objects and the replay clip's counters.
// While the button is held, each physics step writes an older snapshot back (mj_setState +
// mj_forward) instead of recording; the game's own sync moves the Unity transforms. Restoring the
// clip's counters drops the rewound stretch from the replay recorder.

using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using Jibbers.Sdk;
using Jibbers.Sdk.Game.Env;
using Jibbers.Sdk.Game.Physics;
using Jibbers.Sdk.Game.Replay;
using Jibbers.Sdk.Interop;
using Jibbers.Sdk.Symbols;

namespace Jibbers.Sdk.Features.Rewind;

public sealed unsafe class RewindController : ModFeature
{
    public override string Id => "reshred";
    public override string Name => "Rewind";
    public override string Author => "Jaxon";
    public override string Description => "Hold X to rewind through the last 6 seconds, then let go to continue from that moment. Rewound time is cut from replay clips.";

    public override IEnumerable<Setting> Settings => new[]
    {
        Setting.KeyBinding("key", "Keyboard key", (int)Key.X).WithTooltip("Key to hold (X by default)."),
        Setting.Toggle("controller", "Controller X", true).WithTooltip("Also rewind when holding X (Xbox) / Square (PlayStation)."),
        Setting.Slider("holdTime", "Hold time", 0.2, 0.1, 2, 0.1).WithTooltip("Seconds to hold the button before the rewind starts."),
        Setting.Slider("speed", "Rewind speed", 2, 0.5, 6, 0.5).WithTooltip("How fast time runs backwards while held (1 = real time)."),
        Setting.Toggle("indicator", "Indicator", true).WithTooltip("Show how far back you are while rewinding."),
    };

    /// <summary>mjtState bits: everything mj_step integrates except the simulation time.</summary>
    const uint StateSig = ((1u << 13) - 1) & ~1u;

    // ---- MuJoCo -----------------------------------------------------------------------------

    delegate* unmanaged<nint, uint, int> mjStateSize;
    delegate* unmanaged<nint, nint, double*, uint, void> mjGetState;
    delegate* unmanaged<nint, nint, double*, uint, void> mjSetState;
    delegate* unmanaged<nint, nint, void> mjForward;

    bool LoadMujoco()
    {
        if (mjStateSize != null)
        {
            return true;
        }
        if (!NativeLibrary.TryLoad("pcore.dll", out var lib))
        {
            return false;
        }
        mjStateSize = (delegate* unmanaged<nint, uint, int>)NativeLibrary.GetExport(lib, "mj_stateSize");
        mjGetState = (delegate* unmanaged<nint, nint, double*, uint, void>)NativeLibrary.GetExport(lib, "mj_getState");
        mjSetState = (delegate* unmanaged<nint, nint, double*, uint, void>)NativeLibrary.GetExport(lib, "mj_setState");
        mjForward = (delegate* unmanaged<nint, nint, void>)NativeLibrary.GetExport(lib, "mj_forward");
        return true;
    }

    static readonly FieldRef model = new("physics.scene.model"), data = new("physics.scene.data");

    // ---- game state saved with each frame ---------------------------------------------------

    enum Kind
    {
        /// <summary>The replay clip: its int and double fields (frame counters, timestamps).</summary>
        Clip,
        /// <summary>Ragdoll controller and its logic objects: obfuscated int and bool fields.</summary>
        State,
    }

    /// <summary>Fields of k and its parents up to the first class with a namespace (the game's own classes have none).</summary>
    static IEnumerable<Il2CppField> ChainFields(Il2CppClass k)
    {
        for (var c = k; !c.IsNull && c.Namespace.Length == 0; c = c.Parent)
        {
            foreach (var f in c.Fields)
            {
                yield return f;
            }
        }
    }

    readonly Dictionary<(nint, Kind), (int Offset, int Size)[]> layouts = new();

    (int Offset, int Size)[] Layout(nint obj, Kind kind)
    {
        var k = Il2Cpp.ClassOf(obj);
        if (layouts.TryGetValue((k.Ptr, kind), out var l))
        {
            return l;
        }
        var list = new List<(int Offset, int Size)>();
        foreach (var f in ChainFields(k))
        {
            if (f.IsStatic)
            {
                continue;
            }
            var size = (kind, f.TypeName) switch
            {
                (Kind.Clip, "System.Int32") => 4,
                (Kind.Clip, "System.Double") => 8,
                (Kind.State, "System.Int32") when Shape.IsObfuscated(f.Name) => 4,
                (Kind.State, "System.Boolean") when Shape.IsObfuscated(f.Name) => 1,
                _ => 0,
            };
            if (size > 0)
            {
                list.Add((f.Offset, size));
            }
        }
        return layouts[(k.Ptr, kind)] = list.ToArray();
    }

    Il2CppField[]? ragdollRefs;

    /// <summary>The objects whose state is saved now: the clip, the ragdoll controller and its obfuscated logic objects.</summary>
    List<(nint Obj, Kind Kind)> Targets()
    {
        var output = new List<(nint, Kind)>();
        var rec = Recorder.Instance;
        if (rec.IsNull)
        {
            return output;
        }
        var clip = rec.Clip;
        if (!clip.IsNull)
        {
            output.Add((clip.Ptr, Kind.Clip));
        }
        var rider = rec.Ragdoll;
        if (!rider.IsNull)
        {
            output.Add((rider.Ptr, Kind.State));
            ragdollRefs ??= ChainFields(Il2Cpp.ClassOf(rider.Ptr)).Where(f => !f.IsStatic && !f.TypeClass.IsNull && !f.TypeClass.IsValueType && Shape.IsObfuscated(f.TypeName)).ToArray();
            foreach (var f in ragdollRefs)
            {
                var o = f.GetObject(rider.Ptr);
                if (o != 0 && !output.Any(t => t.Item1 == o))
                {
                    output.Add((o, Kind.State));
                }
            }
        }
        return output;
    }

    sealed record Slot(nint Obj, (int Offset, int Size)[] Layout, byte[] Bytes);

    sealed class Frame
    {
        public double[] State = Array.Empty<double>();
        public List<Slot> Slots = new();
    }

    sealed class History
    {
        public nint Model, Data, Rider, Clip;
        public int Size, Cap;
        public readonly LinkedList<Frame> Frames = new();
        public float ScrubAcc;
    }

    History? history;
    readonly object historyGate = new();
    volatile bool scrubbing;
    float fixedDeltaTime = 0.003f;

    // ---- the physics step -------------------------------------------------------------------

    void Record(nint scene)
    {
        if (!LoadMujoco() || Scene.IsPaused)
        {
            return;
        }
        if (!Monitor.TryEnter(historyGate))
        {
            return;
        }
        try
        {
            var m = model.Get<nint>(scene);
            var d = data.Get<nint>(scene);
            if (m == 0 || d == 0)
            {
                return;
            }
            var size = mjStateSize(m, StateSig);
            if (size <= 0 || size > 1_000_000)
            {
                return;
            }
            var live = Targets();
            var rider = live.FirstOrDefault(t => t.Kind == Kind.State).Obj;
            var clip = live.FirstOrDefault(t => t.Kind == Kind.Clip).Obj;
            // A new or rebuilt scene, rider or clip invalidates the history.
            var h = history;
            if (h is null || h.Model != m || h.Data != d || h.Size != size || h.Rider != rider || h.Clip != clip)
            {
                h = history = new History { Model = m, Data = d, Rider = rider, Clip = clip, Size = size, Cap = Rewind.Capacity(fixedDeltaTime) };
            }
            if (scrubbing)
            {
                // Hold to scrub: each physics step goes back through the history and shows that moment.
                var k = Rewind.ScrubSteps(ref h.ScrubAcc, (float)GetDouble("speed"));
                if (Rewind.ScrubTarget(h.Frames.Count, k) is { } i)
                {
                    while (h.Frames.Count > i + 1)
                    {
                        h.Frames.RemoveLast();
                    }
                    try
                    {
                        RestoreFrame(m, d, h.Size, h.Frames.Last!.Value, live);
                    }
                    catch (Exception e)
                    {
                        Log("scrub: " + e.Message);
                    }
                }
                return;
            }
            h.ScrubAcc = 0;
            Frame frame;
            if (h.Frames.Count >= h.Cap)
            {
                frame = h.Frames.First!.Value;
                h.Frames.RemoveFirst();
            }
            else
            {
                frame = new Frame { State = new double[size] };
            }
            fixed (double* s = frame.State)
            {
                mjGetState(m, d, s, StateSig);
            }
            frame.Slots.Clear();
            foreach (var (obj, kind) in live)
            {
                var lay = Layout(obj, kind);
                var bytes = new byte[lay.Sum(x => x.Size)];
                var at = 0;
                foreach (var (off, n) in lay)
                {
                    new ReadOnlySpan<byte>((void*)(obj + off), n).CopyTo(bytes.AsSpan(at));
                    at += n;
                }
                frame.Slots.Add(new Slot(obj, lay, bytes));
            }
            h.Frames.AddLast(frame);
        }
        finally
        {
            Monitor.Exit(historyGate);
        }
    }

    int RestoreFrame(nint m, nint d, int size, Frame frame, List<(nint Obj, Kind Kind)> live)
    {
        if (frame.State.Length != size || mjStateSize(m, StateSig) != size)
        {
            throw new InvalidOperationException("physics state size changed; wait for fresh history");
        }
        fixed (double* s = frame.State)
        {
            mjSetState(m, d, s, StateSig);
        }
        mjForward(m, d);
        var restored = 0;
        // Only objects that are still the live ones: an old pointer may belong to a freed object.
        foreach (var slot in frame.Slots)
        {
            if (!live.Any(t => t.Obj == slot.Obj))
            {
                continue;
            }
            var at = 0;
            foreach (var (off, n) in slot.Layout)
            {
                slot.Bytes.AsSpan(at, n).CopyTo(new Span<byte>((void*)(slot.Obj + off), n));
                at += n;
            }
            restored++;
        }
        return restored;
    }

    int HistoryLen()
    {
        if (!Monitor.TryEnter(historyGate))
        {
            return 0;
        }
        try
        {
            return history?.Frames.Count ?? 0;
        }
        finally
        {
            Monitor.Exit(historyGate);
        }
    }

    // ---- input and indicator ----------------------------------------------------------------

    [StructLayout(LayoutKind.Sequential)]
    struct XInputState
    {
        public uint Packet;
        public ushort Buttons;
        public byte Lt, Rt;
        public short Lx, Ly, Rx, Ry;
    }

    static delegate* unmanaged<uint, XInputState*, uint> xinput;
    readonly bool[] padConnected = new bool[4];
    uint padFrame;

    bool PadX()
    {
        if (xinput == null)
        {
            foreach (var dll in new[] { "xinput1_4.dll", "xinput1_3.dll", "xinput9_1_0.dll" })
            {
                if (NativeLibrary.TryLoad(dll, out var lib) && NativeLibrary.TryGetExport(lib, "XInputGetState", out var f))
                {
                    xinput = (delegate* unmanaged<uint, XInputState*, uint>)f;
                    break;
                }
            }
            if (xinput == null)
            {
                return false;
            }
        }
        // Pads that are not connected are only polled now and then: asking is slow on some systems.
        padFrame++;
        var probe = padFrame == 1 || padFrame % 180 == 0;
        var down = false;
        for (uint i = 0; i < 4; i++)
        {
            if (!padConnected[i] && !probe)
            {
                continue;
            }
            XInputState st;
            var ok = xinput(i, &st) == 0;
            padConnected[i] = ok;
            down |= ok && (st.Buttons & 0x4000) != 0;
        }
        return down;
    }

    readonly RewiredReservation rewired = new();
    float held;
    bool holding, wasDown, ignore;
    int startLen;
    DateTime? last;
    GameRef? label;
    bool toldLabel;
    string? inputError;

    nint Label()
    {
        if (label?.Target is { } live && !Unity.IsNull(live))
        {
            return live;
        }
        label?.Dispose();
        label = null;
        var ui = Unity.Find("UI");
        var canvas = Unity.Call(Unity.Transform(ui), "Find", Il2Cpp.String("MainCanvas"));
        var tpl = canvas == 0 ? 0 : Unity.Call(canvas, "Find", Il2Cpp.String("Respawn"));
        if (Unity.IsNull(tpl))
        {
            throw new InvalidOperationException("UI/MainCanvas/Respawn not found");
        }
        var c = Unity.Transform(Unity.CallStatic(Il2Cpp.RequireClass(Unity.Core, "UnityEngine.Object"), "Instantiate", Unity.Call(tpl, "get_gameObject"), canvas));
        try
        {
            Unity.Call(Unity.Call(c, "get_gameObject"), "set_name", Il2Cpp.String("Reshred_Label"));
            // Its ReactiveText would keep rewriting the text to the respawn state.
            var rt = Il2Cpp.Class("Assembly-CSharp.dll", "", "ReactiveText");
            if (!rt.IsNull && Unity.GetComponent(c, rt) is var reactive && reactive != 0)
            {
                Unity.CallStatic(Il2Cpp.RequireClass(Unity.Core, "UnityEngine.Object"), "DestroyImmediate", reactive);
            }
            // The template is parked above the screen until the game moves it in: give the clone its own place.
            var anchor = new Vector2(0.5f, 0.18f);
            Unity.Call(c, "set_anchorMin", (nint)(&anchor));
            Unity.Call(c, "set_anchorMax", (nint)(&anchor));
            var pivot = new Vector2(0.5f, 0.5f);
            Unity.Call(c, "set_pivot", (nint)(&pivot));
            var pos = new Vector3(0, 0, 0);
            Unity.Call(c, "set_anchoredPosition3D", (nint)(&pos));
            var size = new Vector2(720, 72);
            Unity.Call(c, "set_sizeDelta", (nint)(&size));
            var scale = new Vector3(1, 1, 1);
            Unity.Call(c, "set_localScale", (nint)(&scale));
            var tmp = Unity.GetComponent(c, Il2Cpp.RequireClass("Unity.TextMeshPro.dll", "TMPro.TMP_Text"));
            var white = new Color(1, 1, 1, 1);
            Unity.Call(tmp, "set_color", (nint)(&white));
            var fontSize = 30f;
            Unity.Call(tmp, "set_fontSize", (nint)(&fontSize));
            var center = 514; // TextAlignmentOptions.Center
            Unity.Call(tmp, "set_alignment", (nint)(&center));
            var no = false;
            Unity.Call(tmp, "set_raycastTarget", (nint)(&no));
            Unity.Call(c, "SetAsLastSibling");
        }
        catch
        {
            Unity.Destroy(Unity.Call(c, "get_gameObject"));
            throw;
        }
        label = new GameRef(c);
        return c;
    }

    void ShowLabel(string text)
    {
        try
        {
            var l = Label();
            var tmp = Unity.GetComponent(l, Il2Cpp.RequireClass("Unity.TextMeshPro.dll", "TMPro.TMP_Text"));
            Unity.Call(tmp, "set_text", Il2Cpp.String(text));
            var yes = true;
            Unity.Call(Unity.Call(l, "get_gameObject"), "SetActive", (nint)(&yes));
        }
        catch (Exception e)
        {
            // The rewind itself does not depend on the label: say it once.
            if (!toldLabel)
            {
                toldLabel = true;
                Log("no indicator: " + e.Message);
            }
        }
    }

    void HideLabel()
    {
        if (label?.Target is { } l && !Unity.IsNull(l))
        {
            var no = false;
            Unity.Call(Unity.Call(l, "get_gameObject"), "SetActive", (nint)(&no));
        }
    }

    /// <summary>Once per frame, before EnvironmentManager.Update.</summary>
    void Tick()
    {
        var key = GetKey("key");
        var controller = GetBool("controller");
        rewired.Update(Enabled, key, controller);
        if (rewired.LastError != inputError)
        {
            if (rewired.LastError is { } e)
            {
                Log("input reservation: " + e);
            }
            inputError = rewired.LastError;
        }
        var now = DateTime.UtcNow;
        var frameDt = last is { } t ? (float)Math.Min((now - t).TotalSeconds, 0.25) : 0;
        last = now;
        var down = Enabled && Input.GameFocused && ((key != 0 && Input.IsDown(key)) || (controller && (rewired.PadDown() || PadX())));
        if (!down)
        {
            if (holding)
            {
                EndScrub();
            }
            if (wasDown)
            {
                HideLabel();
            }
            wasDown = false;
            held = 0;
            return;
        }
        if (!wasDown)
        {
            wasDown = true;
            held = 0;
            ignore = Input.Typing;
        }
        if (ignore)
        {
            return;
        }
        var steps = HistoryLen();
        var ready = !Scene.IsPaused && (holding ? steps > 0 : steps * fixedDeltaTime > 0.3f);
        var indicator = GetBool("indicator");
        if (!ready)
        {
            held = 0;
            if (holding)
            {
                EndScrub();
            }
            if (indicator)
            {
                ShowLabel("Reshred waiting for active physics/history");
            }
            else
            {
                HideLabel();
            }
            return;
        }
        held += frameDt;
        var holdTime = (float)GetDouble("holdTime");
        if (!holding)
        {
            if (held >= holdTime)
            {
                holding = true;
                startLen = steps;
                scrubbing = true;
            }
            else
            {
                if (indicator)
                {
                    var pct = (int)Math.Min(held / holdTime * 100, 99);
                    ShowLabel($"Hold to rewind {pct}%{(inputError is null ? "" : " - input conflict (see log)")}");
                }
                else
                {
                    HideLabel();
                }
                return;
            }
        }
        if (indicator)
        {
            var back = Math.Max(0, startLen - steps) * fixedDeltaTime;
            var total = Math.Max(0, startLen - 1) * fixedDeltaTime;
            ShowLabel($"Rewinding -{back:0.0}s / -{total:0.0}s - {(steps <= 1 ? "start of the rewind - release to resume" : "release to resume")}");
        }
        else
        {
            HideLabel();
        }
    }

    void EndScrub()
    {
        scrubbing = false;
        holding = false;
        Log($"resumed {Math.Max(0, startLen - HistoryLen()) * fixedDeltaTime:0.00}s back");
    }

    // ---- lifecycle --------------------------------------------------------------------------

    public override void OnLoad()
    {
        Scene.FixedUpdateMethod.Before((scene, _) => Record(scene));
        Manager.UpdateMethod.Before((_, _) => Tick());
        MainThread.Later(() =>
        {
            var fixedDt = Il2Cpp.Unbox<float>(Unity.CallStatic(Il2Cpp.RequireClass(Unity.Core, "UnityEngine.Time"), "get_fixedDeltaTime"));
            fixedDeltaTime = fixedDt is >= 0.0005f and < 0.05f ? fixedDt : 0.003f;
            Log($"ready (dt {fixedDeltaTime:0.0000}s, MuJoCo {(LoadMujoco() ? "found" : "missing")})");
        });
    }

    public override void OnSettingChanged(string key, JsonNode value)
    {
        if (key == "enabled" && !value.GetValue<bool>())
        {
            MainThread.Later(() =>
            {
                scrubbing = false;
                rewired.Restore();
                HideLabel();
            });
        }
    }

    public override JsonNode? OnCommand(string cmd, JsonObject request) => cmd switch
    {
        "reshred.info" => new JsonObject
        {
            ["frames"] = HistoryLen(),
            ["span"] = HistoryLen() * fixedDeltaTime,
            ["dt"] = fixedDeltaTime,
            ["scrubbing"] = scrubbing,
            ["maskedBindings"] = rewired.MaskedCount,
            ["rawControllers"] = rewired.PadCount,
            ["inputError"] = inputError,
        },
        _ => null,
    };

    public override void OnUnload()
    {
        scrubbing = false;
        try
        {
            MainThread.Run(() =>
            {
                rewired.Restore();
                if (label?.Target is { } l && !Unity.IsNull(l))
                {
                    Unity.Destroy(Unity.Call(l, "get_gameObject"));
                }
                label?.Dispose();
            });
        }
        catch (Exception e)
        {
            Log("cleanup: " + e.Message);
        }
    }
}
