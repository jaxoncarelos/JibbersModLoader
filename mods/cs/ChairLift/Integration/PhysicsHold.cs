using Jibbers.Interop.Mujoco;
using Jibbers.Sdk;
using Jibbers.Sdk.Runtime;
using Jibbers.Sdk.Symbols;
using System.Runtime.InteropServices;

namespace ChairLift.Integration;

/// <summary>A free joint of the rider or its gear: where its numbers live in MuJoCo's state.</summary>
internal readonly record struct FreeBody(nint Transform, int Qpos, int Dof, int BodyId);

/// <summary>
/// Holding the rider on the seat in the physics engine (MuJoCo), without touching the pose.
/// The rider's <c>daw()</c> puts the whole body back in its default stance, so it's only used to
/// sit down; after that the free joints of the rider and its gear (skis and poles are separate
/// bodies held on by constraints) are moved to the seat and given the chair's speed every physics
/// step, through MuJoCo's own <c>mj_getState</c>/<c>mj_setState</c>, and every other joint is left
/// to the rider's posing. While seated, the chair's collisions are off (the rider overlaps it).
///
/// The game rebuilds its physics scene now and then (a map change, a player joining); everything
/// here is tied to the scene's model and checked against its sizes, and dropped after a rebuild.
/// Main thread only.
/// </summary>
internal sealed unsafe class PhysicsHold
{
    const uint QposBit = 1u << 1, QvelBit = 1u << 2;
    static readonly FieldRef SceneModel = new("physics.scene.model");
    static readonly FieldRef SceneData = new("physics.scene.data");
    static readonly RawClassRef Model = new("Mujoco.Runtime.dll", "Mujoco", "MujocoLib/mjModel_");

    delegate* unmanaged<nint, uint, int> stateSize;
    delegate* unmanaged<nint, nint, double*, uint, void> getState;
    delegate* unmanaged<nint, nint, double*, uint, void> setState;
    double[] buffer = [];

    /// <summary>The bodies held now, the root one, the scene they belong to, and the root's upright orientation.</summary>
    List<FreeBody> bodies = new();
    int root;
    nint scene;
    double[] upright = [1, 0, 0, 0];

    /// <summary>Collisions switched off: the scene, and (geom, contype, conaffinity) to put back.</summary>
    nint savedScene;
    readonly List<(int Geom, int Contype, int Conaffinity)> saved = new();

    public bool Holding => bodies.Count > 0;
    public bool CollisionsOff => saved.Count > 0;

    bool Available()
    {
        if (stateSize != null)
            return true;
        if (!GameNativeLibrary.TryLoad("pcore", out var library))
            return false;
        // Resolve every export before publishing a usable API; keep the library loaded.
        var size = NativeLibrary.GetExport(library, "mj_stateSize");
        var get = NativeLibrary.GetExport(library, "mj_getState");
        var set = NativeLibrary.GetExport(library, "mj_setState");
        getState = (delegate* unmanaged<nint, nint, double*, uint, void>)get;
        setState = (delegate* unmanaged<nint, nint, double*, uint, void>)set;
        stateSize = (delegate* unmanaged<nint, uint, int>)size;
        return true;
    }

    /// <summary>The running physics scene object (0 if none).</summary>
    public static nint Scene()
    {
        if (!MjScene.get_InstanceExists())
            return 0;
        var s = MjScene.get_Instance().Ptr;
        return s != 0 && !Unity.IsNull(s) ? s : 0;
    }

    /// <summary>The scene's compiled model (its identity: it changes when the game rebuilds it).</summary>
    public static nint ModelOf(nint scene) => scene == 0 ? 0 : SceneModel.Get<nint>(scene);

    /// <summary>
    /// Starts holding the free bodies within <paramref name="radius"/> metres of <paramref name="seat"/>
    /// (the rider just placed there, and its gear); the root is the one nearest <paramref name="riderCentre"/>.
    /// </summary>
    public bool Begin(Vector3 seat, Vector3 riderCentre, float radius)
    {
        End();
        var s = Scene();
        var model = ModelOf(s);
        if (model == 0 || !Available())
            return false;
        var nq = stateSize(model, QposBit);
        var nv = stateSize(model, QvelBit);
        foreach (var joint in MjFreeJoint.Live())
        {
            if (joint.IsNull || Unity.IsNull(joint.Ptr))
                continue;
            var tr = Unity.Transform(joint.Ptr);
            if (Vec.Distance(Unity.Position(tr), seat) > radius)
                continue;
            var j = new MjBaseJoint(joint.Ptr);
            int qpos = j.get_QposAddress(), dof = j.get_DofAddress();
            // A free joint is 7 numbers of position and 6 of speed.
            if (qpos < 0 || dof < 0 || qpos + 7 > nq || dof + 6 > nv)
                continue;
            var parent = j.get_ParentBody();
            var body = parent.IsNull ? -1 : new MjComponent(parent.Ptr).get_MujocoId();
            bodies.Add(new FreeBody(tr, qpos, dof, body));
        }
        if (bodies.Count == 0)
            return false;
        root = 0;
        for (var i = 1; i < bodies.Count; i++)
            if (Vec.Distance(Unity.Position(bodies[i].Transform), riderCentre) < Vec.Distance(Unity.Position(bodies[root].Transform), riderCentre))
                root = i;
        scene = model;
        // The rider was just stood upright facing uphill: keep the root that way, or it slowly
        // rolls over and tilts the first-person and phone cameras.
        if (Read(s, out var state, out _))
            upright = [state[bodies[root].Qpos + 3], state[bodies[root].Qpos + 4], state[bodies[root].Qpos + 5], state[bodies[root].Qpos + 6]];
        return true;
    }

    /// <summary>Lets go of the bodies.</summary>
    public void End()
    {
        bodies.Clear();
        scene = 0;
    }

    /// <summary>Whether the held bodies belong to the running scene (false after a rebuild).</summary>
    public bool StillValid() => Holding && ModelOf(Scene()) == scene;

    bool Read(nint s, out double[] state, out int nq)
    {
        state = buffer;
        nq = 0;
        var model = ModelOf(s);
        var data = s == 0 ? 0 : SceneData.Get<nint>(s);
        if (model == 0 || data == 0 || !Available())
            return false;
        nq = stateSize(model, QposBit);
        var length = stateSize(model, QposBit | QvelBit);
        if (length <= 0 || length > 1_000_000)
            return false;
        if (buffer.Length != length)
            buffer = new double[length];
        state = buffer;
        fixed (double* p = buffer)
            getState(model, data, p, QposBit | QvelBit);
        return true;
    }

    /// <summary>
    /// Puts the held bodies so the root is at <paramref name="seat"/> (Unity), upright, moving at
    /// <paramref name="velocity"/>, not spinning. Writes nothing unless the scene is the one the
    /// bodies were found in and the rider is near the seat. Returns whether it held them.
    /// </summary>
    public bool Hold(nint s, Vector3 seat, Vector3 velocity)
    {
        if (!Holding || ModelOf(s) != scene || !Read(s, out var state, out var nq))
            return false;
        var r = bodies[root];
        // Unity -> MuJoCo axes, with the physics plugin's own conversion.
        var target = MjEngineTool.MjVector3(seat);
        var speed = MjEngineTool.MjVector3(velocity);
        double dx = target.X - state[r.Qpos], dy = target.Y - state[r.Qpos + 1], dz = target.Z - state[r.Qpos + 2];
        if (!double.IsFinite(dx + dy + dz) || dx * dx + dy * dy + dz * dz > 9)
            return false; // knocked far off (a respawn, a glitch): the caller decides
        var n = Math.Sqrt(upright.Sum(v => v * v));
        if (n > 0.5 && double.IsFinite(n))
            for (var i = 0; i < 4; i++)
                state[r.Qpos + 3 + i] = upright[i] / n;
        foreach (var b in bodies)
        {
            state[b.Qpos] += dx;
            state[b.Qpos + 1] += dy;
            state[b.Qpos + 2] += dz;
            var v = nq + b.Dof;
            state[v] = speed.X;
            state[v + 1] = speed.Y;
            state[v + 2] = speed.Z;
            state[v + 3] = state[v + 4] = state[v + 5] = 0;
        }
        var model = ModelOf(s);
        var data = SceneData.Get<nint>(s);
        fixed (double* p = state)
            setState(model, data, p, QposBit | QvelBit);
        return true;
    }

    /// <summary>Where the root body is now (Unity).</summary>
    public Vector3? RootPosition() => Holding && !Unity.IsNull(bodies[root].Transform) ? Unity.Position(bodies[root].Transform) : null;

    // ---- the chair's collisions ----

    static int ModelInt(nint model, string field) => *(int*)(model + Model.Class.Field(field).Offset - 16);
    static int* ModelArray(nint model, string field) => *(int**)(model + Model.Class.Field(field).Offset - 16);

    /// <summary>
    /// Switches off collisions for the chair (every shape under its object: in the physics they're
    /// part of the static world, like the ground, so only the scene hierarchy tells them apart) and
    /// for anyone else sitting on it (moving bodies near the seat that aren't the held rider and
    /// gear). The ground, the stations and the rider keep colliding, so nothing falls through the
    /// floor. Returns how many shapes it switched off.
    /// </summary>
    public int ChairCollisionsOff(nint chair, Vector3 seat, float radius)
    {
        RestoreCollisions();
        var model = ModelOf(Scene());
        if (model == 0)
            return 0;
        var ngeom = ModelInt(model, "ngeom");
        var nbody = ModelInt(model, "nbody");
        var contype = ModelArray(model, "geom_contype");
        var conaffinity = ModelArray(model, "geom_conaffinity");
        var bodyOf = ModelArray(model, "geom_bodyid");
        var rootOf = ModelArray(model, "body_rootid");
        if (contype == null || conaffinity == null || bodyOf == null || rootOf == null)
            return 0;
        int Root(int b) => b >= 0 && b < nbody ? rootOf[b] : -1;
        var mine = bodies.Select(b => Root(b.BodyId)).Where(r => r > 0).ToHashSet();
        var near = new List<(int Id, float Distance, bool OnChair, int Body)>();
        foreach (var g in MjGeom.Live())
        {
            if (g.IsNull || Unity.IsNull(g.Ptr))
                continue;
            var tr = Unity.Transform(g.Ptr);
            var d = Vec.Distance(Unity.Position(tr), seat);
            if (d > radius)
                continue;
            var id = new MjComponent(g.Ptr).get_MujocoId();
            if (id < 0 || id >= ngeom)
                continue;
            near.Add((id, d, GameLifts.IsChildOf(tr, chair), bodyOf[id]));
        }
        // If the chair's colliders aren't under its object, the static shapes centred right by
        // the seat are the chair's (the ground and stations are big shapes centred far away).
        var chairFound = near.Any(n => n.OnChair);
        savedScene = model;
        foreach (var (id, distance, onChair, body) in near)
        {
            var mineOrWorld = body <= 0 || mine.Contains(Root(body));
            var chairShape = onChair || (!chairFound && body <= 0 && distance < 1.8f);
            if (!chairShape && mineOrWorld)
                continue;
            saved.Add((id, contype[id], conaffinity[id]));
            contype[id] = 0;
            conaffinity[id] = 0;
        }
        return saved.Count;
    }

    /// <summary>Puts back what <see cref="ChairCollisionsOff"/> switched off (a rebuilt scene has them on anyway).</summary>
    public void RestoreCollisions()
    {
        if (saved.Count == 0)
            return;
        var model = ModelOf(Scene());
        if (model != 0 && model == savedScene)
        {
            var ngeom = ModelInt(model, "ngeom");
            var contype = ModelArray(model, "geom_contype");
            var conaffinity = ModelArray(model, "geom_conaffinity");
            if (contype != null && conaffinity != null)
                foreach (var (id, ct, ca) in saved)
                    if (id < ngeom)
                    {
                        contype[id] = ct;
                        conaffinity[id] = ca;
                    }
        }
        saved.Clear();
        savedScene = 0;
    }
}
