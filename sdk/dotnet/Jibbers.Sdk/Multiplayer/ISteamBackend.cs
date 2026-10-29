namespace Jibbers.Sdk.Multiplayer;

/// <summary>What an asynchronous Steam call has done so far.</summary>
internal enum CallStatus { Pending, Failed, Done }

/// <summary>
/// The Steam calls the party needs, so its logic can be tested without Steam (see <see cref="SteamFlat"/>
/// for the real one). Matchmaking members are called from the party thread only; Send may be called
/// from any thread (ISteamNetworkingMessages is thread safe).
/// </summary>
internal interface ISteamBackend
{
    SteamId LocalId { get; }
    uint AppId { get; }
    string LocalName { get; }
    string NameOf(SteamId id);
    /// <summary>Friends who are online.</summary>
    IReadOnlyList<SteamId> OnlineFriends();
    /// <summary>The lobby a friend is in while playing this game, else <see cref="SteamId.None"/>.</summary>
    SteamId FriendLobby(SteamId friend);

    /// <summary>Starts creating a public lobby; returns the call handle.</summary>
    ulong CreateLobby(int maxMembers);
    CallStatus LobbyCreated(ulong call, out SteamId lobby);
    ulong JoinLobby(SteamId lobby);
    CallStatus LobbyEntered(ulong call, out bool entered);
    void LeaveLobby(SteamId lobby);
    /// <summary>Starts a worldwide lobby search, filtered on one lobby data key when <paramref name="key"/> is set.</summary>
    ulong FindLobbies(string? key, string? value);
    bool CallDone(ulong call);
    /// <summary>The lobbies the last finished search found.</summary>
    IReadOnlyList<SteamId> FoundLobbies();
    IReadOnlyList<SteamId> Members(SteamId lobby);
    SteamId Owner(SteamId lobby);
    string LobbyData(SteamId lobby, string key);
    /// <summary>How many data keys a lobby has (a lobby nobody has written to has none).</summary>
    int LobbyDataCount(SteamId lobby);
    /// <summary>Asks Steam for the data of a lobby you are not in; <see cref="LobbyData"/> answers once it arrives.</summary>
    void RequestLobbyData(SteamId lobby);
    void SetLobbyData(SteamId lobby, string key, string value);
    void SetJoinable(SteamId lobby, bool joinable);
    string MemberData(SteamId lobby, SteamId member, string key);
    void SetMemberData(SteamId lobby, string key, string value);
    bool InviteToLobby(SteamId lobby, SteamId friend);
    void OpenInviteOverlay(SteamId lobby);
    /// <summary>Sets (or with null clears) one of the player's Steam rich presence keys, which friends see.</summary>
    void SetRichPresence(string key, string? value);

    bool Send(SteamId to, ReadOnlySpan<byte> data, bool reliable);
    /// <summary>Appends waiting messages to <paramref name="into"/>; returns how many.</summary>
    int Receive(List<(SteamId From, byte[] Data)> into, int max);
    /// <summary>Accepts a pending message session from a party member (no-op if none is pending).</summary>
    void Accept(SteamId peer);
    void Close(SteamId peer);
    /// <summary>Round-trip time to a peer in milliseconds, or -1 if unknown.</summary>
    int Ping(SteamId peer);
}
