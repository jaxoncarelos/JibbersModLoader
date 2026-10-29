using System.Runtime.InteropServices;
using System.Text;

namespace Jibbers.Sdk.Multiplayer;

/// <summary>
/// Steam through the game's own steam_api (flat C API). The game has already started Steam and
/// dispatches its callbacks itself, so results are polled (ISteamUtils call results, member lists)
/// rather than received as callbacks. Messages are ISteamNetworkingMessages, relayed by Valve: no
/// ports, and peers never see each other's IP address.
/// </summary>
internal sealed unsafe class SteamFlat : ISteamBackend
{
    /// <summary>The SDK's message channel (SLVSH uses 9, Voice Chat 7).</summary>
    internal const int Channel = 0x4A42;
    const int LobbyPublic = 2, DistanceWorldwide = 3, FriendFlagImmediate = 0x04;
    const int SendUnreliable = 1 | 32, SendReliable = 8 | 32; // NoNagle / Reliable, with AutoRestartBrokenSession
    const int IdentitySteamId = 16, IdentitySize = 136;
    const int LobbyCreatedId = 513, LobbyEnterId = 504;

    readonly nint user, mm, utils, msgs, friends;
    readonly delegate* unmanaged<nint, ulong> getSteamId;
    readonly delegate* unmanaged<nint, uint> getAppId;
    readonly delegate* unmanaged<nint, byte*> personaName;
    readonly delegate* unmanaged<nint, ulong, byte*> friendName;
    readonly delegate* unmanaged<nint, int, int> friendCount;
    readonly delegate* unmanaged<nint, int, int, ulong> friendByIndex;
    readonly delegate* unmanaged<nint, ulong, int> friendState;
    readonly delegate* unmanaged<nint, ulong, byte*, byte> friendGame;
    readonly delegate* unmanaged<nint, ulong, void> inviteDialog;
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
    readonly delegate* unmanaged<nint, ulong, ulong> lobbyOwner;
    readonly delegate* unmanaged<nint, ulong, byte*, byte*> getLobbyData;
    readonly delegate* unmanaged<nint, ulong, byte*, byte*, byte> setLobbyData;
    readonly delegate* unmanaged<nint, ulong, int> lobbyDataCount;
    readonly delegate* unmanaged<nint, ulong, byte> requestLobbyData;
    readonly delegate* unmanaged<nint, ulong, byte, byte> setJoinable;
    readonly delegate* unmanaged<nint, ulong, ulong, byte*, byte*> getMemberData;
    readonly delegate* unmanaged<nint, ulong, byte*, byte*, void> setMemberData;
    readonly delegate* unmanaged<nint, ulong, ulong, byte> inviteUser;
    readonly delegate* unmanaged<nint, ulong, byte*, byte> callCompleted;
    readonly delegate* unmanaged<nint, ulong, void*, int, int, byte*, byte> callResult;
    readonly delegate* unmanaged<nint, byte*, void*, uint, int, int, int> sendToUser;
    readonly delegate* unmanaged<nint, int, nint*, int, int> receive;
    readonly delegate* unmanaged<nint, byte*, byte> acceptSession;
    readonly delegate* unmanaged<nint, byte*, byte> closeSession;
    readonly delegate* unmanaged<nint, byte*, void*, void*, int> connectionInfo;

    SteamFlat(nint lib)
    {
        nint Get(string name) => NativeLibrary.TryGetExport(lib, name, out var f) ? f : throw new EntryPointNotFoundException(name);
        user = ((delegate* unmanaged<nint>)Get("SteamAPI_SteamUser_v023"))();
        mm = ((delegate* unmanaged<nint>)Get("SteamAPI_SteamMatchmaking_v009"))();
        utils = ((delegate* unmanaged<nint>)Get("SteamAPI_SteamUtils_v010"))();
        msgs = ((delegate* unmanaged<nint>)Get("SteamAPI_SteamNetworkingMessages_SteamAPI_v002"))();
        friends = ((delegate* unmanaged<nint>)Get("SteamAPI_SteamFriends_v018"))();
        if (user == 0 || mm == 0 || utils == 0 || msgs == 0 || friends == 0)
            throw new InvalidOperationException("Steam is not running");
        getSteamId = (delegate* unmanaged<nint, ulong>)Get("SteamAPI_ISteamUser_GetSteamID");
        getAppId = (delegate* unmanaged<nint, uint>)Get("SteamAPI_ISteamUtils_GetAppID");
        personaName = (delegate* unmanaged<nint, byte*>)Get("SteamAPI_ISteamFriends_GetPersonaName");
        friendName = (delegate* unmanaged<nint, ulong, byte*>)Get("SteamAPI_ISteamFriends_GetFriendPersonaName");
        friendCount = (delegate* unmanaged<nint, int, int>)Get("SteamAPI_ISteamFriends_GetFriendCount");
        friendByIndex = (delegate* unmanaged<nint, int, int, ulong>)Get("SteamAPI_ISteamFriends_GetFriendByIndex");
        friendState = (delegate* unmanaged<nint, ulong, int>)Get("SteamAPI_ISteamFriends_GetFriendPersonaState");
        friendGame = (delegate* unmanaged<nint, ulong, byte*, byte>)Get("SteamAPI_ISteamFriends_GetFriendGamePlayed");
        inviteDialog = (delegate* unmanaged<nint, ulong, void>)Get("SteamAPI_ISteamFriends_ActivateGameOverlayInviteDialog");
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
        lobbyOwner = (delegate* unmanaged<nint, ulong, ulong>)Get("SteamAPI_ISteamMatchmaking_GetLobbyOwner");
        getLobbyData = (delegate* unmanaged<nint, ulong, byte*, byte*>)Get("SteamAPI_ISteamMatchmaking_GetLobbyData");
        setLobbyData = (delegate* unmanaged<nint, ulong, byte*, byte*, byte>)Get("SteamAPI_ISteamMatchmaking_SetLobbyData");
        lobbyDataCount = (delegate* unmanaged<nint, ulong, int>)Get("SteamAPI_ISteamMatchmaking_GetLobbyDataCount");
        requestLobbyData = (delegate* unmanaged<nint, ulong, byte>)Get("SteamAPI_ISteamMatchmaking_RequestLobbyData");
        setJoinable = (delegate* unmanaged<nint, ulong, byte, byte>)Get("SteamAPI_ISteamMatchmaking_SetLobbyJoinable");
        getMemberData = (delegate* unmanaged<nint, ulong, ulong, byte*, byte*>)Get("SteamAPI_ISteamMatchmaking_GetLobbyMemberData");
        setMemberData = (delegate* unmanaged<nint, ulong, byte*, byte*, void>)Get("SteamAPI_ISteamMatchmaking_SetLobbyMemberData");
        inviteUser = (delegate* unmanaged<nint, ulong, ulong, byte>)Get("SteamAPI_ISteamMatchmaking_InviteUserToLobby");
        callCompleted = (delegate* unmanaged<nint, ulong, byte*, byte>)Get("SteamAPI_ISteamUtils_IsAPICallCompleted");
        callResult = (delegate* unmanaged<nint, ulong, void*, int, int, byte*, byte>)Get("SteamAPI_ISteamUtils_GetAPICallResult");
        sendToUser = (delegate* unmanaged<nint, byte*, void*, uint, int, int, int>)Get("SteamAPI_ISteamNetworkingMessages_SendMessageToUser");
        receive = (delegate* unmanaged<nint, int, nint*, int, int>)Get("SteamAPI_ISteamNetworkingMessages_ReceiveMessagesOnChannel");
        acceptSession = (delegate* unmanaged<nint, byte*, byte>)Get("SteamAPI_ISteamNetworkingMessages_AcceptSessionWithUser");
        closeSession = (delegate* unmanaged<nint, byte*, byte>)Get("SteamAPI_ISteamNetworkingMessages_CloseSessionWithUser");
        connectionInfo = (delegate* unmanaged<nint, byte*, void*, void*, int>)Get("SteamAPI_ISteamNetworkingMessages_GetSessionConnectionInfo");
        // Warm up Valve's relay network now, so the first message to a peer is not slow.
        if (NativeLibrary.TryGetExport(lib, "SteamAPI_SteamNetworkingUtils_SteamAPI_v004", out var nu) && NativeLibrary.TryGetExport(lib, "SteamAPI_ISteamNetworkingUtils_InitRelayNetworkAccess", out var relay))
        {
            var utilsNet = ((delegate* unmanaged<nint>)nu)();
            if (utilsNet != 0) ((delegate* unmanaged<nint, void>)relay)(utilsNet);
        }
    }

    [DllImport("kernel32", CharSet = CharSet.Unicode)]
    static extern nint GetModuleHandleW(string name);

    /// <summary>The game's Steam, or null while steam_api is not loaded or Steam is not running.</summary>
    public static SteamFlat? TryOpen(out string why)
    {
        why = "";
        try
        {
            var lib = OperatingSystem.IsWindows() ? GetModuleHandleW("steam_api64.dll") : 0;
            if (lib == 0 && !Runtime.GameNativeLibrary.TryLoad(OperatingSystem.IsWindows() ? "steam_api64" : "steam_api", out lib))
            {
                why = "the game has not loaded Steam";
                return null;
            }
            return new SteamFlat(lib);
        }
        catch (Exception e) when (e is EntryPointNotFoundException or InvalidOperationException or DllNotFoundException)
        {
            why = e.Message;
            return null;
        }
    }

    static string Str(byte* p) => p == null ? "" : Marshal.PtrToStringUTF8((nint)p) ?? "";
    static byte[] Z(string s) => Encoding.UTF8.GetBytes(s + "\0");
    static void Identity(byte* id, SteamId who)
    {
        new Span<byte>(id, IdentitySize).Clear();
        *(int*)id = IdentitySteamId;
        *(int*)(id + 4) = 8;
        *(ulong*)(id + 8) = who.Value;
    }

    public SteamId LocalId => new(getSteamId(user));
    public uint AppId => getAppId(utils);
    public string LocalName => Str(personaName(friends));
    public string NameOf(SteamId id) => Str(friendName(friends, id.Value));

    public IReadOnlyList<SteamId> OnlineFriends()
    {
        var n = friendCount(friends, FriendFlagImmediate);
        var list = new List<SteamId>();
        for (var i = 0; i < n && i < 1000; i++)
        {
            var id = friendByIndex(friends, i, FriendFlagImmediate);
            if (id != 0 && friendState(friends, id) != 0) list.Add(new SteamId(id));
        }
        return list;
    }

    public SteamId FriendLobby(SteamId friend)
    {
        // FriendGameInfo_t: CGameID (app id in the low 24 bits), IP, ports, then the lobby at 16.
        var info = stackalloc byte[32];
        if (friendGame(friends, friend.Value, info) == 0) return SteamId.None;
        return (uint)(*(ulong*)info & 0xFFFFFF) == AppId ? new SteamId(*(ulong*)(info + 16)) : SteamId.None;
    }

    public ulong CreateLobby(int maxMembers) => createLobby(mm, LobbyPublic, maxMembers);

    public CallStatus LobbyCreated(ulong call, out SteamId lobby)
    {
        // LobbyCreated_t { EResult; uint64 lobby }: 8-byte packing on Windows, 4 elsewhere.
        var at = OperatingSystem.IsWindows() ? 8 : 4;
        var buf = stackalloc byte[16];
        var status = Result(call, buf, at + 8, LobbyCreatedId);
        lobby = status == CallStatus.Done && *(int*)buf == 1 ? new SteamId(*(ulong*)(buf + at)) : SteamId.None;
        return status == CallStatus.Done && !lobby.IsValid ? CallStatus.Failed : status;
    }

    public ulong JoinLobby(SteamId lobby) => joinLobby(mm, lobby.Value);

    public CallStatus LobbyEntered(ulong call, out bool entered)
    {
        // LobbyEnter_t { uint64 lobby; uint32 permissions; bool locked; uint32 response }.
        var buf = stackalloc byte[24];
        var status = Result(call, buf, OperatingSystem.IsWindows() ? 24 : 20, LobbyEnterId);
        entered = status == CallStatus.Done && *(uint*)(buf + 16) == 1;
        return status;
    }

    CallStatus Result(ulong call, byte* buf, int size, int id)
    {
        byte failed = 0;
        if (callCompleted(utils, call, &failed) == 0) return failed != 0 ? CallStatus.Failed : CallStatus.Pending;
        return callResult(utils, call, buf, size, id, &failed) != 0 && failed == 0 ? CallStatus.Done : CallStatus.Failed;
    }

    public void LeaveLobby(SteamId lobby) => leaveLobby(mm, lobby.Value);

    public ulong FindLobbies(string? key, string? value)
    {
        if (key is not null && value is not null)
            fixed (byte* k = Z(key), v = Z(value)) addStringFilter(mm, k, v, 0);
        addDistanceFilter(mm, DistanceWorldwide);
        addCountFilter(mm, 50);
        return requestLobbyList(mm);
    }

    public bool CallDone(ulong call)
    {
        byte failed = 0;
        return callCompleted(utils, call, &failed) != 0 || failed != 0;
    }

    public IReadOnlyList<SteamId> FoundLobbies()
    {
        var list = new List<SteamId>();
        for (var i = 0; i < 50; i++)
        {
            var l = lobbyByIndex(mm, i);
            if (l == 0) break;
            list.Add(new SteamId(l));
        }
        return list;
    }

    public IReadOnlyList<SteamId> Members(SteamId lobby)
    {
        var n = numMembers(mm, lobby.Value);
        var list = new List<SteamId>(Math.Max(0, n));
        for (var i = 0; i < n; i++)
        {
            var m = memberByIndex(mm, lobby.Value, i);
            if (m != 0) list.Add(new SteamId(m));
        }
        return list;
    }

    public SteamId Owner(SteamId lobby) => new(lobbyOwner(mm, lobby.Value));
    public string LobbyData(SteamId lobby, string key) { fixed (byte* k = Z(key)) return Str(getLobbyData(mm, lobby.Value, k)); }
    public int LobbyDataCount(SteamId lobby) => lobbyDataCount(mm, lobby.Value);
    public void RequestLobbyData(SteamId lobby) => requestLobbyData(mm, lobby.Value);
    public void SetLobbyData(SteamId lobby, string key, string value) { fixed (byte* k = Z(key), v = Z(value)) setLobbyData(mm, lobby.Value, k, v); }
    public void SetJoinable(SteamId lobby, bool joinable) => setJoinable(mm, lobby.Value, joinable ? (byte)1 : (byte)0);
    public string MemberData(SteamId lobby, SteamId member, string key) { fixed (byte* k = Z(key)) return Str(getMemberData(mm, lobby.Value, member.Value, k)); }
    public void SetMemberData(SteamId lobby, string key, string value) { fixed (byte* k = Z(key), v = Z(value)) setMemberData(mm, lobby.Value, k, v); }
    public bool InviteToLobby(SteamId lobby, SteamId friend) => inviteUser(mm, lobby.Value, friend.Value) != 0;
    public void OpenInviteOverlay(SteamId lobby) => inviteDialog(friends, lobby.Value);

    public bool Send(SteamId to, ReadOnlySpan<byte> data, bool reliable)
    {
        var id = stackalloc byte[IdentitySize];
        Identity(id, to);
        fixed (byte* d = data)
            return sendToUser(msgs, id, d, (uint)data.Length, reliable ? SendReliable : SendUnreliable, Channel) == 1;
    }

    public int Receive(List<(SteamId From, byte[] Data)> into, int max)
    {
        // SteamNetworkingMessage_t: data, size, connection, peer identity (136 bytes at 16), ..., release at 184.
        var batch = stackalloc nint[32];
        var total = 0;
        while (total < max)
        {
            var n = receive(msgs, Channel, batch, 32);
            if (n <= 0) break;
            for (var i = 0; i < n; i++)
            {
                var m = (byte*)batch[i];
                var size = *(int*)(m + 8);
                if (*(int*)(m + 16) == IdentitySteamId && size > 0)
                {
                    into.Add((new SteamId(*(ulong*)(m + 24)), new ReadOnlySpan<byte>(*(byte**)m, size).ToArray()));
                    total++;
                }
                ((delegate* unmanaged<nint, void>)*(nint*)(m + 184))((nint)m);
            }
            if (n < 32) break;
        }
        return total;
    }

    public void Accept(SteamId peer)
    {
        var id = stackalloc byte[IdentitySize];
        Identity(id, peer);
        acceptSession(msgs, id);
    }

    public void Close(SteamId peer)
    {
        var id = stackalloc byte[IdentitySize];
        Identity(id, peer);
        closeSession(msgs, id);
    }

    public int Ping(SteamId peer)
    {
        // SteamNetConnectionRealTimeStatus_t starts { state, ping }; the buffer is larger than the struct.
        var id = stackalloc byte[IdentitySize];
        var status = stackalloc byte[256];
        Identity(id, peer);
        var state = connectionInfo(msgs, id, null, status);
        return state == 3 ? *(int*)(status + 4) : -1; // k_ESteamNetworkingConnectionState_Connected
    }
}
