using Jibbers.Interop.Game;
using Jibbers.Sdk;
using Jibbers.Sdk.Interop;

namespace Slvsh.Integration;

/// <summary>Another player's rider in this session, as the game shows it: <paramref name="Body"/> is what moves with them.</summary>
internal sealed record GameRider(int Id, string Name, IReadOnlyList<string> Info, nint Body);

/// <summary>
/// The other players in your session, from the game's own player manager (<c>mc</c>): a dictionary
/// of player id to player, each with the puppet (<c>nr</c>) that draws their rider from network
/// snapshots, and a dictionary of player id to their details (names and account strings). Other
/// players' riders are puppets, not ragdolls. Main thread only.
/// </summary>
internal static unsafe class GamePlayers
{
    static Il2CppClass int32, transform;
    static readonly Dictionary<nint, GameRef> bodies = new();

    public static List<GameRider> Others()
    {
        var list = new List<GameRider>();
        var manager = mc.rev;
        if (manager.IsNull || Unity.IsNull(manager.Ptr)) manager = mc.Live().FirstOrDefault(m => !m.IsNull && !Unity.IsNull(m.Ptr));
        if (manager.IsNull) return list;
        var players = manager.rex;
        var details = manager.rey;
        if (players == 0) return list;
        foreach (var id in Keys(players))
        {
            var player = new mc_lz(Get(players, id));
            if (player.IsNull) continue;
            var puppet = player.rdh;
            if (puppet.IsNull) continue; // you have no puppet: you are your ragdoll
            var root = puppet._roj_k__BackingField.Ptr;
            if (root == 0 || Unity.IsNull(root)) continue;
            string name;
            try { name = manager.Name(id) ?? ""; }
            catch (Exception e) when (e is InvalidOperationException or MissingMethodException or Il2CppException) { name = ""; }
            var info = new List<string>();
            if (details != 0 && Contains(details, id) && new lg(Get(details, id)) is { IsNull: false } d)
                info.AddRange(new[] { d.rak, d.ram, d.ran, d.rao, d.raj.ToString(), d.ral.ToString() }.Where(s => !string.IsNullOrEmpty(s)));
            list.Add(new GameRider(id, name, info, Body(puppet, root)));
        }
        return list;
    }

    /// <summary>
    /// The part of a puppet that moves with the rider: its hips (or pelvis) when the rig has them,
    /// else a ski, else its root (which may stay put while the bones move). Cached per puppet.
    /// </summary>
    static nint Body(nr puppet, nint root)
    {
        if (bodies.TryGetValue(root, out var known) && !Unity.IsNull(known.Target)) return known.Target;
        if (bodies.Count > 64) { foreach (var r in bodies.Values) r.Dispose(); bodies.Clear(); }
        if (transform.IsNull) transform = Unity.Class("UnityEngine.Transform");
        var body = Unity.GetComponentsInChildren(root, transform, includeInactive: true)
            .FirstOrDefault(t => Unity.Name(t).ToLowerInvariant() is var n && (n.Contains("hips") || n.Contains("pelvis")));
        if (body == 0 && !puppet._rol_k__BackingField.IsNull) body = Unity.Transform(puppet._rol_k__BackingField.Ptr);
        if (body == 0 || Unity.IsNull(body)) body = root;
        bodies[root] = new GameRef(body);
        return body;
    }

    /// <summary>The keys of a <c>Dictionary&lt;int, T&gt;</c>, through its own methods (no guessing at its layout).</summary>
    static List<int> Keys(nint dictionary)
    {
        var type = Il2Cpp.ClassOf(dictionary);
        var count = type.Method("get_Count", 0).Invoke<int>(dictionary);
        if (count <= 0) return new List<int>();
        var keys = type.Method("get_Keys", 0).Invoke(dictionary);
        if (int32.IsNull) int32 = Il2Cpp.Class("mscorlib.dll", "System", "Int32");
        var array = Il2Cpp.NewArray(int32, count);
        var start = 0;
        Il2Cpp.ClassOf(keys).Method("CopyTo", 2).Invoke(keys, array, (nint)(&start));
        return Il2Cpp.ArrayValues<int>(array).ToList();
    }

    static bool Contains(nint dictionary, int key) => Il2Cpp.ClassOf(dictionary).Method("ContainsKey", 1).Invoke<bool>(dictionary, (nint)(&key));

    static nint Get(nint dictionary, int key) => Il2Cpp.ClassOf(dictionary).Method("get_Item", 1).Invoke(dictionary, (nint)(&key));
}
