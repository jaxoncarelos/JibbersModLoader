using System.Security.Cryptography;
using System.Text;

namespace Jibbers.Sdk.Multiplayer;

internal sealed record ServerPlayer(SteamId Id, string Name);
internal sealed record ServerInvite(SteamId From, string Name, SteamId Lobby, string Nonce, double Expires);
internal sealed record ServerPresence(IReadOnlyList<ServerPlayer> Players, ServerInvite? Invitation)
{
    public static readonly ServerPresence Empty = new([], null);
}

/// <summary>One player on the current game server: their FishNet client id and the name the server gave them.</summary>
internal sealed record RosterEntry(int ClientId, string Name);
/// <summary>The current game server's players, and this game's own client id (-1 if unknown).</summary>
internal sealed record ServerRoster(IReadOnlyList<RosterEntry> Players, int LocalClientId)
{
    public static readonly ServerRoster Empty = new([], -1);
}

/// <summary>
/// Finds the other Play Together players on the current game server, and carries party invites
/// between them, all inside the Party panel.
/// <para>
/// The game never tells a client anyone's Steam id, so players meet in an invisible Steam lobby
/// (found by searches, hidden from friends, allowed beside the party's lobby) named by a hash of
/// the server's longest-connected player: the same on every client of that server. Each member
/// writes its protocol, client id and server name as member data, and a member is shown only when
/// that client id and name are on this game's own roster right now, so a lobby shared by mistake
/// (or written to by someone off the server) shows nobody. Invitations are member data too.
/// </para>
/// All methods run on the party thread.
/// </summary>
internal sealed class ServerPresenceDiscovery(ISteamBackend steam, Func<double> clock,
    Func<PartySnapshot> party, Func<bool> partyBusy, Action create, Action<SteamId> join, Action<string> notice, Action changed)
{
    internal const string KeyServer = "jb_server", KeyPlay = "jb_play", KeyClient = "jb_cid", KeyName = "jb_name", KeyInvite = "jb_invite";
    internal const int MaxLobbyMembers = 250;
    const double CallTimeout = 15, RetryDelay = 10, MergeEvery = 30, RosterStale = 8, InviteLife = 45;

    enum Op { None, Searching, Creating, Joining }

    ServerRoster roster = ServerRoster.Empty;
    string wantedKey = "";
    double lastRoster;
    bool online;

    Op op;
    ulong call;
    double started, retryAt, nextMerge, nextRead;
    string opKey = "";
    SteamId lobby, joinTarget;
    string lobbyKey = "", advertised = "";

    readonly Dictionary<SteamId, ServerPlayer> visible = new();
    readonly HashSet<SteamId> associated = new();
    readonly Dictionary<string, double> seenInvites = new(StringComparer.Ordinal);
    readonly Dictionary<SteamId, double> nextInvitation = new();
    double clearOutgoing, pendingUntil, lastInvite = double.NegativeInfinity;
    SteamId pending;
    ServerPresence current = ServerPresence.Empty;

    public ServerPresence Snapshot => Volatile.Read(ref current);
    /// <summary>How many players are in this server's lobby (-1 when not in one), for diagnostics. Any thread.</summary>
    public int LobbyMembers => Volatile.Read(ref lobbyMembers);
    int lobbyMembers = -1;
    string Protocol => "2:" + steam.AppId;

    public void SetRoster(bool connected, ServerRoster players)
    {
        online = connected && players.Players.Count > 0;
        lastRoster = clock();
        roster = online ? players with { Players = players.Players.Take(256).ToArray() } : ServerRoster.Empty;
        wantedKey = online ? ServerKey(steam.AppId, roster.Players) : "";
        if (!online)
        {
            LeaveLobby();
            pending = SteamId.None;
        }
        nextRead = 0;
    }

    public void Tick()
    {
        var now = clock();
        if (online && now - lastRoster > RosterStale) SetRoster(false, ServerRoster.Empty);
        if (lobby.IsValid && lobbyKey != wantedKey) LeaveLobby();
        switch (op)
        {
            case Op.None:
                if (!online) break;
                if (!lobby.IsValid ? now >= retryAt : now >= nextMerge)
                    if (!partyBusy()) BeginSearch(now);
                break;
            case Op.Searching:
                if (!steam.CallDone(call) && now - started < CallTimeout) break;
                op = Op.None;
                if (opKey != wantedKey) break;
                // Everyone settles in the lowest-numbered lobby of the server, so two players who
                // made one at the same moment end up together on the next search.
                var best = steam.FoundLobbies().Where(l => l.IsValid && steam.LobbyData(l, KeyServer) == opKey).OrderBy(l => l.Value).FirstOrDefault();
                if (lobby.IsValid)
                {
                    nextMerge = now + MergeEvery;
                    if (best.IsValid && best.Value < lobby.Value) { LeaveLobby(); BeginJoin(best, opKey, now); }
                }
                else if (best.IsValid) BeginJoin(best, opKey, now);
                else { call = steam.CreateInvisibleLobby(MaxLobbyMembers); started = now; op = Op.Creating; }
                break;
            case Op.Creating:
                var made = SteamId.None;
                var result = steam.LobbyCreated(call, out made);
                if (result == CallStatus.Pending && now - started < CallTimeout) break;
                op = Op.None;
                if (result != CallStatus.Done || !made.IsValid) { retryAt = now + RetryDelay; break; }
                if (opKey != wantedKey) { steam.LeaveLobby(made); break; }
                steam.SetLobbyData(made, KeyServer, opKey);
                Entered(made, now);
                break;
            case Op.Joining:
                var answer = steam.LobbyEntered(call, out var entered);
                var inside = entered || steam.Members(joinTarget).Contains(steam.LocalId);
                if (!inside && answer != CallStatus.Done && now - started < CallTimeout) break;
                op = Op.None;
                if (!inside) { retryAt = now + RetryDelay; break; }
                if (opKey != wantedKey) { steam.LeaveLobby(joinTarget); break; }
                Entered(joinTarget, now);
                break;
        }
        if (lobby.IsValid) InLobby(now);
    }

    void BeginSearch(double now)
    {
        opKey = wantedKey;
        call = steam.FindLobbies(KeyServer, opKey);
        started = now;
        op = Op.Searching;
    }

    void BeginJoin(SteamId target, string key, double now)
    {
        opKey = key;
        joinTarget = target;
        call = steam.JoinLobby(target);
        started = now;
        op = Op.Joining;
    }

    void Entered(SteamId target, double now)
    {
        lobby = target;
        lobbyKey = opKey;
        advertised = "";
        nextMerge = now + MergeEvery;
        nextRead = 0;
    }

    void LeaveLobby()
    {
        if (op == Op.Joining && steam.Members(joinTarget).Contains(steam.LocalId)) steam.LeaveLobby(joinTarget);
        if (op != Op.Creating) op = Op.None; // a lobby still being made is left when it arrives (its key no longer matches)
        if (lobby.IsValid) steam.LeaveLobby(lobby);
        lobby = SteamId.None;
        lobbyKey = advertised = "";
        clearOutgoing = 0;
        visible.Clear();
        associated.Clear();
        Volatile.Write(ref lobbyMembers, -1);
        Publish([], null);
    }

    void InLobby(double now)
    {
        var mine = roster.Players.FirstOrDefault(p => p.ClientId == roster.LocalClientId);
        var name = mine?.Name ?? steam.LocalName;
        var advert = $"{Protocol}\n{roster.LocalClientId}\n{name}";
        if (advert != advertised)
        {
            steam.SetMemberData(lobby, KeyPlay, Protocol);
            steam.SetMemberData(lobby, KeyClient, roster.LocalClientId.ToString());
            steam.SetMemberData(lobby, KeyName, CleanName(name));
            advertised = advert;
        }
        if (clearOutgoing != 0 && now >= clearOutgoing) { steam.SetMemberData(lobby, KeyInvite, ""); clearOutgoing = 0; }
        if (pending.IsValid && (now >= pendingUntil || !visible.ContainsKey(pending))) pending = SteamId.None;
        if (pending.IsValid && party() is { State: PartyState.InParty } snapshot)
        {
            var target = pending; pending = SteamId.None;
            if (snapshot.Locked) notice("Unlock the party before inviting another player.");
            else
            {
                var nonce = Guid.NewGuid().ToString("N");
                steam.SetMemberData(lobby, KeyInvite, $"1:{target}:{snapshot.Lobby}:{nonce}");
                clearOutgoing = now + InviteLife;
                notice($"Invited {visible[target].Name}. They can accept in the Party panel.");
            }
        }
        if (now < nextRead) return;
        nextRead = now + 1;

        visible.Clear();
        var members = steam.Members(lobby);
        Volatile.Write(ref lobbyMembers, members.Count);
        foreach (var member in members)
        {
            if (member == steam.LocalId || !ValidAccount(member)) continue;
            if (steam.MemberData(lobby, member, KeyPlay) != Protocol) continue;
            if (Match(member) is not { } entry) continue;
            visible.TryAdd(member, new(member, CleanName(entry.Name)));
        }
        foreach (var id in visible.Keys) if (associated.Add(id)) steam.MarkPlayedWith(id);
        associated.RemoveWhere(id => !visible.ContainsKey(id));
        foreach (var gone in nextInvitation.Keys.Where(id => !visible.ContainsKey(id)).ToArray()) nextInvitation.Remove(gone);

        var incoming = current.Invitation is { } old && old.Expires > now && visible.ContainsKey(old.From) ? old : null;
        foreach (var player in visible.Values)
        {
            if (!TryInvitation(steam.MemberData(lobby, player.Id, KeyInvite), steam.LocalId, out var target, out var nonce)) continue;
            var key = player.Id + ":" + nonce;
            if (seenInvites.ContainsKey(key) || (nextInvitation.TryGetValue(player.Id, out var next) && now < next)) continue;
            if (seenInvites.Count >= 256) seenInvites.Remove(seenInvites.MinBy(pair => pair.Value).Key);
            seenInvites[key] = now;
            nextInvitation[player.Id] = now + 15;
            steam.RequestLobbyData(target);
            if (party().Lobby == target) continue;
            incoming = new(player.Id, player.Name, target, nonce, now + InviteLife);
            notice($"{player.Name} invited you to their party. Open {Party.PanelKey} to accept.");
        }
        Publish(visible.Values.OrderBy(player => player.Name, StringComparer.OrdinalIgnoreCase).ToArray(), incoming);
    }

    /// <summary>The roster entry a lobby member says it is, if this game sees that player on the server now.</summary>
    RosterEntry? Match(SteamId member)
    {
        var name = NameKey(steam.MemberData(lobby, member, KeyName));
        if (name.Length == 0) return null;
        if (int.TryParse(steam.MemberData(lobby, member, KeyClient), out var id) && id >= 0)
        {
            if (id == roster.LocalClientId) return null;
            var entry = roster.Players.FirstOrDefault(p => p.ClientId == id);
            return entry is not null && NameKey(entry.Name) == name ? entry : null;
        }
        // A client that couldn't read its own client id: match its name, only when unambiguous.
        var named = roster.Players.Where(p => p.ClientId != roster.LocalClientId && SameName(NameKey(p.Name), name)).Take(2).ToArray();
        return named.Length == 1 ? named[0] : null;
    }

    public void Invite(SteamId target)
    {
        var now = clock();
        if (!lobby.IsValid || !visible.ContainsKey(target) || now - lastInvite < 5 || party().Members.Any(member => member.Id == target)) return;
        if (party() is { Locked: true, State: PartyState.InParty }) { notice("Unlock the party before inviting another player."); return; }
        if (party().State is not (PartyState.None or PartyState.InParty)) return;
        lastInvite = now; pending = target; pendingUntil = now + 25;
        if (party().State == PartyState.None) create();
    }

    public void Accept()
    {
        if (current.Invitation is not { } invite) return;
        if (!lobby.IsValid || clock() >= invite.Expires || !visible.ContainsKey(invite.From)) { Decline(); return; }
        if (steam.LobbyData(invite.Lobby, PartyEngine.KeyParty) != "1" || steam.LobbyData(invite.Lobby, PartyEngine.KeyLocked) == "1")
        { notice("That invitation is no longer available. Ask for a new invite."); Decline(); return; }
        Decline(); join(invite.Lobby);
    }

    public void Decline() => Publish(current.Players, null);

    void Publish(IReadOnlyList<ServerPlayer> players, ServerInvite? invitation)
    {
        if (current.Invitation == invitation && current.Players.SequenceEqual(players)) return;
        Volatile.Write(ref current, new(players, invitation)); changed();
    }

    /// <summary>
    /// The lobby name for a server: a hash of its longest-connected player (lowest client id) and
    /// their name, which every client of the server sees alike. It changes only when that player leaves.
    /// </summary>
    internal static string ServerKey(uint appId, IReadOnlyList<RosterEntry> players)
    {
        if (players.Count == 0) return "";
        var anchor = players.MinBy(p => p.ClientId)!;
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"jibbers-play-server/1/{appId}/{anchor.ClientId}/{NameKey(anchor.Name)}"));
        return Convert.ToHexString(hash, 0, 16).ToLowerInvariant();
    }

    /// <summary>A name reduced to lower-case letters and digits, as the game tidies names it shows.</summary>
    internal static string NameKey(string? name)
    {
        if (string.IsNullOrEmpty(name)) return "";
        var text = name.Length > 128 ? name[..128] : name;
        try { text = text.Normalize(NormalizationForm.FormKC); } catch (ArgumentException) { }
        var key = new StringBuilder(text.Length);
        foreach (var c in text) if (char.IsLetterOrDigit(c)) key.Append(char.ToLowerInvariant(c));
        return key.ToString();
    }

    // The server may shorten a long name, so a long enough prefix counts as the same name.
    static bool SameName(string roster, string advertised) => roster == advertised
        || (roster.Length >= 12 && advertised.Length >= roster.Length && advertised.StartsWith(roster, StringComparison.Ordinal));

    internal static bool ValidAccount(SteamId id) => id.Value >> 56 == 1 && ((id.Value >> 52) & 15) == 1 && ((id.Value >> 32) & 0xfffff) == 1 && (uint)id.Value != 0;
    internal static bool ValidLobby(SteamId id) => id.Value >> 56 == 1 && ((id.Value >> 52) & 15) == 8 && ((id.Value >> 32) & 0x40000) != 0 && (uint)id.Value != 0;
    internal static bool TryInvitation(string text, SteamId me, out SteamId lobby, out string nonce)
    {
        lobby = SteamId.None; nonce = "";
        if (text.Length is < 1 or > 160) return false;
        var parts = text.Split(':');
        if (parts.Length != 4 || parts[0] != "1" || !SteamId.TryParse(parts[1], out var target) || target != me
            || !SteamId.TryParse(parts[2], out lobby) || !ValidLobby(lobby) || parts[3].Length != 32 || !parts[3].All(Uri.IsHexDigit)) return false;
        nonce = parts[3]; return true;
    }
    static string CleanName(string name) => new(name.Where(character => !char.IsControl(character) && character is not '<' and not '>').Take(64).ToArray());
}
