using Jibbers.Interop.Game;
using Jibbers.Sdk.Interop;

namespace Jibbers.Sdk.Multiplayer;

// Read-only access to the same account-details dictionary used by SLVSH's rider matching.
// This never advertises data through FishNet or changes another player's state.
internal static unsafe class CurrentServerPlayers
{
    static readonly SceneObjectCache<qk> manager = new(() =>
    {
        var current = qk.vsz;
        if (!current.IsNull && !Unity.IsNull(current.Ptr)) return current;
        foreach (var candidate in qk.Live()) if (!candidate.IsDead()) return candidate;
        return null;
    });
    static Il2CppClass int32;
    public static int RosterCount { get; private set; }
    public static int SteamCount { get; private set; }
    public static void Reset() => manager.Invalidate();
    public static IReadOnlyList<ServerPlayer> Read()
    {
        RosterCount = SteamCount = 0;
        if (manager.Get() is not { } current || current.vtc == 0 || current.vtb == 0) return [];
        var details = current.vtc;
        var players = current.vtb;
        var type = Il2Cpp.ClassOf(players);
        var count = type.Method("get_Count", 0).Invoke<int>(players);
        if (count is <= 0 or > 128) return [];
        RosterCount = count;
        using var keys = new GameRef(type.Method("get_Keys", 0).Invoke(players));
        if (int32.IsNull) int32 = Il2Cpp.Class("mscorlib.dll", "System", "Int32");
        using var array = new GameRef(Il2Cpp.NewArray(int32, count));
        var start = 0;
        Il2Cpp.ClassOf(keys.Target).Method("CopyTo", 2).Invoke(keys.Target, array.Target, (nint)(&start));
        var detailType = Il2Cpp.ClassOf(details);
        var get = detailType.Method("get_Item", 1);
        var contains = detailType.Method("ContainsKey", 1);
        var result = new List<ServerPlayer>();
        foreach (var key in Il2Cpp.ArrayValues<int>(array.Target))
        {
            var id = key;
            if (!contains.Invoke<bool>(details, (nint)(&id))) continue;
            var detail = new pp(get.Invoke(details, (nint)(&id)));
            if (detail.IsNull) continue;
            // Display names are deliberately excluded as identity candidates.
            foreach (var account in new[] { detail.vnx, detail.vny, detail.vnz })
                if (TryAccount(account, out var steam))
                { result.Add(new(steam, current.Name(id) ?? "Player")); break; }
        }
        var playersWithSteam = result.DistinctBy(player => player.Id).Take(64).ToArray();
        SteamCount = playersWithSteam.Length;
        return playersWithSteam;
    }
    internal static bool TryAccount(string? account, out SteamId id)
    {
        if (account is not null && account.StartsWith("steam:", StringComparison.OrdinalIgnoreCase)) account = account[6..];
        return SteamId.TryParse(account, out id) && ServerPresenceDiscovery.ValidAccount(id);
    }
}
