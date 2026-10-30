using System.IO.Compression;
using System.Runtime.InteropServices;
using Jibbers.Sdk;
using Jibbers.Sdk.Game.Physics;
using Jibbers.Sdk.Game.Player;
using Jibbers.Interop.Mujoco;
using Jibbers.Sdk.Runtime;
using Jibbers.Sdk.Symbols;

namespace Slvsh.Integration;

/// <summary>
/// Where a line begins, and how fast: the physics state of your rider (MuJoCo's integration state:
/// every body's position, orientation and velocity) a few seconds before the setter's trick. While
/// you set, it keeps a short history; a matcher's game loads the setter's snapshot at the start of
/// their turn, so they start at the same place and speed. Same calls as the Rewind mod.
/// Other players aren't simulated on your PC, so the state is only ever your own rider's.
/// </summary>
internal sealed unsafe class LineStart(Func<double> clock, Action<string> log)
{
    const uint IntegrationState = ((1u << 13) - 1) & ~1u; // everything but time
    /// <summary>Snapshots larger than this aren't sent (a party message holds 64 KB).</summary>
    public const int MaxDoubles = 6000;
    const double Every = 0.1, Keep = 15;
    static readonly FieldRef SceneModel = new("physics.scene.model");
    static readonly FieldRef SceneData = new("physics.scene.data");

    delegate* unmanaged<nint, uint, int> stateSize;
    delegate* unmanaged<nint, nint, double*, uint, void> getState;
    delegate* unmanaged<nint, nint, double*, uint, void> setState;
    delegate* unmanaged<nint, nint, void> forward;
    readonly object gate = new();
    readonly List<(double At, double[] State)> history = new();
    double[]? pending;
    double lastCapture;
    bool hooked, failed;
    volatile bool recording;

    // Your spawn: your rider's state just after the game respawned you, and the spawn point it was at.
    double[]? spawnState;
    SpawnSpot? spawnSpot;
    List<SpawnMove.FreeJoint> freeJoints = new();
    int spawnNq;
    bool captureSpawn;
    SpawnSpot? captureAt;
    // Frozen: this state is put back every physics step. Placing: put once, then you ride.
    double[]? frozen;
    bool freezing;
    // Where the freeze holds you: your spawn (null), or a spot to start your turn from, maybe switch.
    SpawnSpot? holdAt;
    bool holdSwitch;
    (bool Pending, SpawnSpot? At, bool Switch) place;

    /// <summary>
    /// The game just respawned you at <paramref name="at"/> (the spawn point it used): save your rider's
    /// state at the next physics step, as where you start your turns.
    /// </summary>
    public void SpawnedAt(SpawnSpot? at)
    {
        lock (gate) { captureSpawn = true; captureAt = at; }
    }

    /// <summary>Holds your rider still at your spawn (or where it is, before the game has spawned you) until <see cref="Place"/> or <see cref="Release"/>.</summary>
    public void Freeze() => Hold(null, false);

    /// <summary>
    /// Holds your rider still where your turn starts: your spawn, or <paramref name="at"/> (someone
    /// else's spawn point), switch if <paramref name="switched"/>. <see cref="Release"/> lets you go from there.
    /// </summary>
    public void Hold(SpawnSpot? at, bool switched)
    {
        lock (gate)
        {
            if (freezing && holdAt == at && holdSwitch == switched) return;
            (freezing, frozen, holdAt, holdSwitch, place) = (true, null, at, switched, (false, null, false));
        }
    }

    /// <summary>
    /// Puts your rider at your spawn, or at <paramref name="at"/> (someone else's spawn point), and lets
    /// you ride. With <paramref name="switched"/>, you face the other way but move the same way: riding switch.
    /// </summary>
    public void Place(SpawnSpot? at, bool switched = false)
    {
        lock (gate) { freezing = false; frozen = null; place = (true, at, switched); }
    }

    /// <summary>Lets go without moving you.</summary>
    public void Release()
    {
        lock (gate) { freezing = false; frozen = null; place = (false, null, false); }
    }

    /// <summary>Keep a history of your rider (while you set).</summary>
    public bool Recording
    {
        get => recording;
        set
        {
            if (value == recording) return;
            recording = value;
            if (!value) lock (gate) history.Clear();
        }
    }

    /// <summary>Hooks the physics step. Main thread (OnLoad).</summary>
    public void Start()
    {
        if (hooked) return;
        hooked = true;
        Scene.FixedUpdateMethod.Before((scene, _) => Step(scene));
    }

    /// <summary>Loads a line at the next physics step.</summary>
    public void Apply(double[] state)
    {
        lock (gate) pending = state;
    }

    /// <summary>The last snapshot at or before <paramref name="target"/> and not before <paramref name="notBefore"/>; else the first after it; null if none.</summary>
    public double[]? Pick(double target, double notBefore)
    {
        lock (gate)
        {
            var usable = history.Where(h => h.At >= notBefore).ToList();
            return (usable.LastOrDefault(h => h.At <= target).State ?? usable.FirstOrDefault().State)?.ToArray();
        }
    }

    void Step(nint scene)
    {
        double[]? apply;
        bool capture, freeze;
        (bool Pending, SpawnSpot? At, bool Switch) placing;
        lock (gate) { apply = pending; pending = null; capture = captureSpawn; freeze = freezing; placing = place; }
        if (!recording && apply is null && !capture && !freeze && !placing.Pending) return;
        if (Scene.IsPaused)
        {
            if (apply is not null) lock (gate) pending ??= apply;
            return;
        }
        if (!Open(scene, out var model, out var data, out var length)) return;
        if (capture) CaptureSpawn(model, data, length);
        if (placing.Pending)
        {
            lock (gate) place = (false, null, false);
            if (SpawnFor(placing.At, length, placing.Switch) is { } start)
            {
                fixed (double* s = start) setState(model, data, s, IntegrationState);
                forward(model, data);
                lock (gate) history.Clear();
            }
        }
        else if (freeze)
        {
            double[]? hold;
            lock (gate) hold = frozen;
            if (hold is null || hold.Length != length)
            {
                // First frozen step: your spawn if the game has spawned you, else right here.
                SpawnSpot? at;
                bool switched;
                lock (gate) (at, switched) = (holdAt, holdSwitch);
                hold = SpawnFor(at, length, switched);
                if (hold is null)
                {
                    hold = new double[length];
                    fixed (double* s = hold) getState(model, data, s, IntegrationState);
                }
                lock (gate) frozen = hold;
            }
            fixed (double* s = hold) setState(model, data, s, IntegrationState);
            forward(model, data);
            return;
        }
        if (apply is not null)
        {
            if (apply.Length == length)
            {
                fixed (double* s = apply) setState(model, data, s, IntegrationState);
                forward(model, data);
                lock (gate) history.Clear();
            }
            else log($"SLVSH: can't start on that line: its physics state has {apply.Length} values, yours {length}");
        }
        var now = clock();
        if (!recording || now - lastCapture < Every || length > MaxDoubles) return;
        lastCapture = now;
        var state = new double[length];
        fixed (double* s = state) getState(model, data, s, IntegrationState);
        lock (gate)
        {
            history.Add((now, state));
            while (history.Count > 0 && now - history[0].At > Keep) history.RemoveAt(0);
        }
    }

    void CaptureSpawn(nint model, nint data, int length)
    {
        SpawnSpot? at;
        lock (gate) { captureSpawn = false; at = captureAt; }
        var state = new double[length];
        fixed (double* s = state) getState(model, data, s, IntegrationState);
        var joints = new List<SpawnMove.FreeJoint>();
        try
        {
            if (RagdollSpawner.First() is { } spawner && !spawner.Rider.IsNull)
                foreach (var j in Unity.GetComponentsInChildren(Unity.Transform(spawner.Rider.Ptr), MjFreeJoint.Class.Class, includeInactive: true))
                    joints.Add(new SpawnMove.FreeJoint(Unity.Call<int>(j, "get_QposAddress"), Unity.Call<int>(j, "get_DofAddress")));
        }
        catch (Exception e) when (e is InvalidOperationException or MissingMethodException or Jibbers.Sdk.Interop.Il2CppException)
        {
            log($"SLVSH: couldn't find your rider's joints: {e.Message}");
        }
        lock (gate)
        {
            spawnState = state;
            spawnSpot = at;
            freeJoints = joints;
            spawnNq = *(int*)model; // mjModel starts with nq
        }
    }

    /// <summary>Your saved spawn state, moved to <paramref name="at"/> when given; null if there's none that fits.</summary>
    double[]? SpawnFor(SpawnSpot? at, int length, bool switched = false)
    {
        double[]? state;
        SpawnSpot? mine;
        List<SpawnMove.FreeJoint> joints;
        int nq;
        lock (gate) { state = spawnState; mine = spawnSpot; joints = freeJoints; nq = spawnNq; }
        if (state is null || state.Length != length) return null;
        var target = at ?? mine;
        // Riding switch: turned around on the spot, still moving the way the spawn sends you.
        if (switched && target is not null) target = target with { Rotation = target.Rotation with { Y = target.Rotation.Y + 180 } };
        if (target is null || mine is null || target == mine) return state;
        at = target;
        if (joints.Count == 0)
        {
            log("SLVSH: can't move you to the setter's spawn: your rider has no free joints");
            return state;
        }
        return SpawnMove.Move(state, nq, joints, mine, at) ?? state;
    }

    bool Open(nint scene, out nint model, out nint data, out int length)
    {
        (model, data, length) = (0, 0, 0);
        if (failed) return false;
        if (stateSize == null)
        {
            try
            {
                if (!GameNativeLibrary.TryLoad("pcore", out var library)) return false;
                stateSize = (delegate* unmanaged<nint, uint, int>)NativeLibrary.GetExport(library, "mj_stateSize");
                getState = (delegate* unmanaged<nint, nint, double*, uint, void>)NativeLibrary.GetExport(library, "mj_getState");
                setState = (delegate* unmanaged<nint, nint, double*, uint, void>)NativeLibrary.GetExport(library, "mj_setState");
                forward = (delegate* unmanaged<nint, nint, void>)NativeLibrary.GetExport(library, "mj_forward");
            }
            catch (EntryPointNotFoundException e)
            {
                failed = true;
                log($"SLVSH: line starts unavailable: {e.Message}");
                return false;
            }
        }
        model = SceneModel.Get<nint>(scene);
        data = SceneData.Get<nint>(scene);
        if (model == 0 || data == 0) return false;
        length = stateSize(model, IntegrationState);
        return length > 0;
    }

    /// <summary>A snapshot as text for a message: compressed doubles, base64.</summary>
    public static string Pack(double[] state)
    {
        var bytes = MemoryMarshal.AsBytes(state.AsSpan()).ToArray();
        using var output = new MemoryStream();
        using (var deflate = new DeflateStream(output, CompressionLevel.Optimal)) deflate.Write(bytes);
        return Convert.ToBase64String(output.ToArray());
    }

    /// <summary>Reads <see cref="Pack"/>'s text; null if malformed, oversized or not finite (it came over the network).</summary>
    public static double[]? Unpack(string text)
    {
        try
        {
            using var input = new DeflateStream(new MemoryStream(Convert.FromBase64String(text)), CompressionMode.Decompress);
            using var output = new MemoryStream();
            var buffer = new byte[8192];
            int n;
            while ((n = input.Read(buffer)) > 0)
            {
                output.Write(buffer, 0, n);
                if (output.Length > MaxDoubles * 8L) return null;
            }
            if (output.Length == 0 || output.Length % 8 != 0) return null;
            var state = MemoryMarshal.Cast<byte, double>(output.ToArray()).ToArray();
            return state.All(double.IsFinite) ? state : null;
        }
        catch (Exception e) when (e is FormatException or InvalidDataException)
        {
            return null;
        }
    }
}
