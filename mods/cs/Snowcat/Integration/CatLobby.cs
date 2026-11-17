using System.Runtime.InteropServices;
using System.Text;

namespace Snowcat.Integration;

/// <summary>
/// Everyone running Snowcat, wherever they are: one Steam lobby (found by its "jibbers_snowcat" tag,
/// made by the first player if there isn't one) that every Snowcat player joins, so no party and no
/// server roster is needed to find each other. Its members are only candidates: players on other
/// servers are in it too, so the mod keeps to the ones whose rider is actually near on your server.
/// The game dispatches Steam's callbacks itself, so every step is polled, with time limits. Main thread.
/// </summary>
internal sealed unsafe class CatLobby : IDisposable
{
    const string Tag = "jibbers_snowcat", Protocol = "2";
    const int LobbyPublic = 2, DistanceWorldwide = 3, MaxMembers = 250, LobbyCreatedId = 513, LobbyEnterId = 504;
    readonly nint mm, utils;
    readonly ulong me;
    readonly Action<string> log;
    readonly delegate* unmanaged<nint, int, int, ulong> createLobby;
    readonly delegate* unmanaged<nint, ulong, ulong> joinLobby;
    readonly delegate* unmanaged<nint, ulong, void> leaveLobby;
    readonly delegate* unmanaged<nint, ulong> requestLobbyList;
    readonly delegate* unmanaged<nint, byte*, byte*, int, void> addStringFilter;
    readonly delegate* unmanaged<nint, int, void> addDistanceFilter;
    readonly delegate* unmanaged<nint, int, void> addCountFilter;
    readonly delegate* unmanaged<nint, int, ulong> lobbyByIndex;
    readonly delegate* unmanaged<nint, ulong, int> numMembers;
    readonly delegate* unmanaged<nint, ulong, int, ulong> memberByIndex;
    readonly delegate* unmanaged<nint, ulong, byte*, byte*, byte> setLobbyData;
    readonly delegate* unmanaged<nint, ulong, byte*, byte> callCompleted;
    readonly delegate* unmanaged<nint, ulong, void*, int, int, byte*, byte> callResult;

    enum Step { Idle, Searching, Joining, Creating, In }
    Step step;
    ulong call, lobby, joining;
    DateTime deadline, nextTry, nextMerge, nextMembers;
    HashSet<ulong> members = new();
    int missing;

    CatLobby(nint lib, Action<string> log)
    {
        this.log = log;
        nint Get(string name) => NativeLibrary.TryGetExport(lib, name, out var f) ? f : throw new EntryPointNotFoundException(name);
        var user = ((delegate* unmanaged<nint>)Get("SteamAPI_SteamUser_v023"))();
        mm = ((delegate* unmanaged<nint>)Get("SteamAPI_SteamMatchmaking_v009"))();
        utils = ((delegate* unmanaged<nint>)Get("SteamAPI_SteamUtils_v010"))();
        if (user == 0 || mm == 0 || utils == 0) throw new InvalidOperationException("Steam isn't running");
        me = ((delegate* unmanaged<nint, ulong>)Get("SteamAPI_ISteamUser_GetSteamID"))(user);
        createLobby = (delegate* unmanaged<nint, int, int, ulong>)Get("SteamAPI_ISteamMatchmaking_CreateLobby");
        joinLobby = (delegate* unmanaged<nint, ulong, ulong>)Get("SteamAPI_ISteamMatchmaking_JoinLobby");
        leaveLobby = (delegate* unmanaged<nint, ulong, void>)Get("SteamAPI_ISteamMatchmaking_LeaveLobby");
        requestLobbyList = (delegate* unmanaged<nint, ulong>)Get("SteamAPI_ISteamMatchmaking_RequestLobbyList");
        addStringFilter = (delegate* unmanaged<nint, byte*, byte*, int, void>)Get("SteamAPI_ISteamMatchmaking_AddRequestLobbyListStringFilter");
        addDistanceFilter = (delegate* unmanaged<nint, int, void>)Get("SteamAPI_ISteamMatchmaking_AddRequestLobbyListDistanceFilter");
        addCountFilter = (delegate* unmanaged<nint, int, void>)Get("SteamAPI_ISteamMatchmaking_AddRequestLobbyListResultCountFilter");
        lobbyByIndex = (delegate* unmanaged<nint, int, ulong>)Get("SteamAPI_ISteamMatchmaking_GetLobbyByIndex");
        numMembers = (delegate* unmanaged<nint, ulong, int>)Get("SteamAPI_ISteamMatchmaking_GetNumLobbyMembers");
        memberByIndex = (delegate* unmanaged<nint, ulong, int, ulong>)Get("SteamAPI_ISteamMatchmaking_GetLobbyMemberByIndex");
        setLobbyData = (delegate* unmanaged<nint, ulong, byte*, byte*, byte>)Get("SteamAPI_ISteamMatchmaking_SetLobbyData");
        callCompleted = (delegate* unmanaged<nint, ulong, byte*, byte>)Get("SteamAPI_ISteamUtils_IsAPICallCompleted");
        callResult = (delegate* unmanaged<nint, ulong, void*, int, int, byte*, byte>)Get("SteamAPI_ISteamUtils_GetAPICallResult");
    }

    [DllImport("kernel32", CharSet = CharSet.Unicode)]
    static extern nint GetModuleHandleW(string name);

    public static CatLobby? Open(Action<string> log, out string why)
    {
        why = "";
        try
        {
            var lib = GetModuleHandleW("steam_api64.dll");
            if (lib == 0) { why = "the game hasn't loaded Steam"; return null; }
            return new CatLobby(lib, log);
        }
        catch (Exception e) when (e is EntryPointNotFoundException or InvalidOperationException)
        {
            why = e.Message;
            return null;
        }
    }

    /// <summary>The other Snowcat players in the lobby (anywhere in the game, on any server).</summary>
    public IReadOnlyCollection<ulong> Members => members;
    public bool Joined => step == Step.In;

    static byte[] Z(string s) => Encoding.UTF8.GetBytes(s + "\0");

    /// <summary>True when the call is done; <paramref name="ok"/> whether it worked.</summary>
    bool Done(ulong c, out bool ok)
    {
        byte failed = 0;
        ok = false;
        if (callCompleted(utils, c, &failed) == 0) return failed != 0;
        ok = failed == 0;
        return true;
    }

    bool Result(ulong c, byte* buf, int size, int id)
    {
        byte failed = 0;
        return callResult(utils, c, buf, size, id, &failed) != 0 && failed == 0;
    }

    void Search()
    {
        fixed (byte* k = Z(Tag), v = Z(Protocol)) addStringFilter(mm, k, v, 0);
        addDistanceFilter(mm, DistanceWorldwide);
        addCountFilter(mm, 20);
        call = requestLobbyList(mm);
        deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
    }

    /// <summary>The Snowcat lobbies the last search found, fullest first.</summary>
    List<(ulong Id, int Count)> Found()
    {
        var list = new List<(ulong, int)>();
        for (var i = 0; i < 20; i++)
        {
            var l = lobbyByIndex(mm, i);
            if (l == 0) break;
            var n = numMembers(mm, l);
            if (n < MaxMembers) list.Add((l, n));
        }
        return list.OrderByDescending(l => l.Item2).ThenBy(l => l.Item1).ToList();
    }

    bool InLobby(ulong l)
    {
        var n = numMembers(mm, l);
        for (var i = 0; i < n; i++) if (memberByIndex(mm, l, i) == me) return true;
        return false;
    }

    void Join(ulong l)
    {
        joining = l;
        call = joinLobby(mm, l);
        step = Step.Joining;
        deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
    }

    void Fail(string what, bool soon = false)
    {
        log($"Snowcat lobby: {what}; trying again soon");
        step = Step.Idle;
        nextTry = DateTime.UtcNow + TimeSpan.FromSeconds(soon ? 3 : 15);
    }

    /// <summary>Every frame: finds or makes the lobby, keeps the member list fresh, and merges into a fuller lobby if two got made.</summary>
    public void Tick()
    {
        var now = DateTime.UtcNow;
        switch (step)
        {
            case Step.Idle:
                if (now < nextTry) return;
                Search();
                step = Step.Searching;
                return;
            case Step.Searching:
                if (!Done(call, out var searched)) { if (now > deadline) Fail("no answer from Steam"); return; }
                var found = searched ? Found() : new();
                // A lobby we made or joined whose answer Steam didn't hand back: we're in it all the same.
                var mine = found.Where(f => InLobby(f.Id)).Select(f => f.Id).ToList();
                if (lobby == 0 && mine.Count > 0)
                {
                    Entered(mine[0], made: false);
                    foreach (var stray in mine.Skip(1)) leaveLobby(mm, stray);
                    return;
                }
                foreach (var stray in mine.Where(id => id != lobby)) leaveLobby(mm, stray);
                if (lobby != 0)
                {
                    // Merging: only into a fuller lobby than ours (ours is in the list too).
                    step = Step.In;
                    var ours = numMembers(mm, lobby);
                    var better = found.FirstOrDefault(f => f.Id != lobby && (f.Count > ours || (f.Count == ours && f.Id < lobby)));
                    if (better.Id != 0)
                    {
                        log($"Snowcat lobby: moving into a fuller one ({better.Count} players)");
                        leaveLobby(mm, lobby);
                        lobby = 0;
                        members.Clear();
                        Join(better.Id);
                    }
                    return;
                }
                if (found.Count > 0) Join(found[0].Id);
                else
                {
                    call = createLobby(mm, LobbyPublic, MaxMembers);
                    step = Step.Creating;
                    deadline = now + TimeSpan.FromSeconds(10);
                }
                return;
            case Step.Joining:
                if (InLobby(joining)) { Entered(joining, made: false); return; }
                if (Done(call, out var joined))
                {
                    var buf = stackalloc byte[24];
                    // LobbyEnter_t { uint64 lobby; uint32 permissions; bool locked; uint32 response (1 = ok) }.
                    if (joined && Result(call, buf, 24, LobbyEnterId) && *(uint*)(buf + 16) == 1) { Entered(joining, made: false); return; }
                }
                if (now > deadline) Fail("couldn't join");
                return;
            case Step.Creating:
                if (Done(call, out var made))
                {
                    // LobbyCreated_t { EResult; uint64 lobby } (8-byte packing on Windows).
                    var buf = stackalloc byte[16];
                    if (made && Result(call, buf, 16, LobbyCreatedId) && *(int*)buf == 1 && *(ulong*)(buf + 8) != 0)
                    {
                        var l = *(ulong*)(buf + 8);
                        fixed (byte* k = Z(Tag), v = Z(Protocol)) setLobbyData(mm, l, k, v);
                        Entered(l, made: true);
                        return;
                    }
                    // Made, maybe, without Steam saying so: look again soon (a search finds a lobby we're in).
                    if (now > deadline || !made) Fail($"couldn't read the new lobby (result {*(int*)buf})", soon: true);
                    return;
                }
                if (now > deadline) Fail("no answer from Steam", soon: true);
                return;
            case Step.In:
                if (now >= nextMembers)
                {
                    nextMembers = now + TimeSpan.FromSeconds(2);
                    var n = numMembers(mm, lobby);
                    var list = new HashSet<ulong>();
                    var stillIn = false;
                    for (var i = 0; i < n; i++)
                    {
                        var m = memberByIndex(mm, lobby, i);
                        if (m == me) stillIn = true;
                        else if (m != 0) list.Add(m);
                    }
                    members = list;
                    // Steam can be a moment behind after joining: only give up after a few misses in a row.
                    missing = stillIn ? 0 : missing + 1;
                    if (missing >= 5) { leaveLobby(mm, lobby); lobby = 0; missing = 0; members.Clear(); Fail("dropped out of it"); return; }
                }
                // Two players making a lobby at once makes two: every so often look for a fuller one.
                if (now >= nextMerge)
                {
                    nextMerge = now + TimeSpan.FromSeconds(members.Count == 0 ? 30 : 120);
                    Search();
                    step = Step.Searching;
                }
                return;
        }
    }

    void Entered(ulong l, bool made)
    {
        lobby = l;
        step = Step.In;
        nextMembers = DateTime.MinValue;
        nextMerge = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        log(made ? "Snowcat lobby: made it (other Snowcat players join it to see your snowcat)" : $"Snowcat lobby: joined ({Math.Max(0, numMembers(mm, l) - 1)} other Snowcat players online)");
    }

    public void Dispose()
    {
        if (lobby != 0) leaveLobby(mm, lobby);
        lobby = 0;
        members.Clear();
        step = Step.Idle;
    }
}
