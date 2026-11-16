using Jibbers.Interop.Game;
using Jibbers.Sdk.Interop;

namespace Jibbers.Sdk.Multiplayer;

// Read-only access to the game's player manager (NetPlayers): who is on the current server, by FishNet
// client id and the name the server gave them. The game never sends Steam ids (its only Steam
// call is GetPersonaName; a roster entry is a name, a host/player role and, server side, an IP),
// so Play Together players find each other through a Steam lobby instead (ServerPresenceDiscovery).
// This never sends anything through FishNet or changes another player's state.
internal static unsafe class CurrentServerPlayers
{
    static readonly SceneObjectCache<NetPlayers> manager = new(() =>
    {
        var current = NetPlayers.xig;
        if (!current.IsNull && !Unity.IsNull(current.Ptr)) return current;
        foreach (var candidate in NetPlayers.Live()) if (!candidate.IsDead()) return candidate;
        return null;
    });
    static Il2CppClass int32;
    public static int RosterCount { get; private set; }
    public static void Reset() => manager.Invalidate();

    /// <summary>The players on the current server. Main thread.</summary>
    public static ServerRoster Read()
    {
        RosterCount = 0;
        if (manager.Get() is not { } current) return ServerRoster.Empty;
        // xii: client id -> player, xij: client id -> account details (rq, sent by the server).
        var ids = new SortedSet<int>();
        Keys(current.xii, ids);
        Keys(current.xij, ids);
        if (ids.Count is 0 or > 256) return ServerRoster.Empty;
        var details = current.xij;
        var detailType = details == 0 ? default : Il2Cpp.ClassOf(details);
        var get = detailType.IsNull ? default : detailType.Method("get_Item", 1);
        var contains = detailType.IsNull ? default : detailType.Method("ContainsKey", 1);
        var players = new List<RosterEntry>();
        foreach (var key in ids)
        {
            var id = key;
            string? name = null;
            if (!contains.IsNull && contains.Invoke<bool>(details, (nint)(&id)))
            {
                var detail = new rq(get.Invoke(details, (nint)(&id)));
                if (!detail.IsNull) name = detail.xaw; // the name the server gave this player
            }
            if (string.IsNullOrWhiteSpace(name)) name = current.Name(id);
            if (!string.IsNullOrWhiteSpace(name)) players.Add(new(id, name));
        }
        RosterCount = players.Count;
        return new(players, GameServer.LocalClientId());
    }

    static void Keys(nint dictionary, SortedSet<int> into)
    {
        if (dictionary == 0) return;
        var type = Il2Cpp.ClassOf(dictionary);
        var count = type.Method("get_Count", 0).Invoke<int>(dictionary);
        if (count is <= 0 or > 256) return;
        using var keys = new GameRef(type.Method("get_Keys", 0).Invoke(dictionary));
        if (int32.IsNull) int32 = Il2Cpp.Class("mscorlib.dll", "System", "Int32");
        using var array = new GameRef(Il2Cpp.NewArray(int32, count));
        var start = 0;
        Il2Cpp.ClassOf(keys.Target).Method("CopyTo", 2).Invoke(keys.Target, array.Target, (nint)(&start));
        foreach (var key in Il2Cpp.ArrayValues<int>(array.Target)) into.Add(key);
    }
}
