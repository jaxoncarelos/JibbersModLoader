namespace Jibbers.Sdk.Multiplayer;

internal sealed record ServerPlayer(SteamId Id, string Name);
internal sealed record ServerInvite(SteamId From, string Name, SteamId Lobby, string Nonce, double Expires);
internal sealed record ServerPresence(IReadOnlyList<ServerPlayer> Players, ServerInvite? Invitation)
{
    public static readonly ServerPresence Empty = new([], null);
}

// All methods run on the party thread. Discovery only queries Steam accounts actually read
// from the current game roster. No public endpoint, password, or new global player directory.
internal sealed class ServerPresenceDiscovery(ISteamBackend steam, Func<double> clock,
    Func<PartySnapshot> party, Action create, Action<SteamId> join, Action<string> notice, Action changed)
{
    internal const string PresenceKey = "jb_play", InvitationKey = "jb_invite";
    readonly Dictionary<SteamId, ServerPlayer> roster = new();
    readonly Dictionary<SteamId, double> nextRequest = new();
    readonly HashSet<SteamId> associated = new();
    readonly Dictionary<string, double> seenInvites = new(StringComparer.Ordinal);
    readonly Dictionary<SteamId, double> nextInvitation = new();
    double lastRoster;
    bool online, advertised;
    double nextRead, clearOutgoing, pendingUntil, lastInvite = double.NegativeInfinity;
    SteamId pending;
    ServerPresence current = ServerPresence.Empty;
    public ServerPresence Snapshot => Volatile.Read(ref current);
    string Protocol => "1:" + steam.AppId;

    public void SetRoster(bool connected, IReadOnlyList<ServerPlayer> players)
    {
        online = connected;
        lastRoster = clock();
        roster.Clear();
        if (connected)
            foreach (var player in players.Take(64))
                if (ValidAccount(player.Id) && player.Id != steam.LocalId)
                {
                    roster.TryAdd(player.Id, player with { Name = CleanName(player.Name) });
                    if (associated.Add(player.Id)) steam.MarkPlayedWith(player.Id);
                }
        associated.RemoveWhere(id => !roster.ContainsKey(id));
        foreach (var gone in nextRequest.Keys.Where(id => !roster.ContainsKey(id)).ToArray()) nextRequest.Remove(gone);
        foreach (var gone in nextInvitation.Keys.Where(id => !roster.ContainsKey(id)).ToArray()) nextInvitation.Remove(gone);
        if (current.Invitation is { } invitation && !roster.ContainsKey(invitation.From)) Publish(current.Players, null);
        if (!connected)
        {
            pending = SteamId.None;
            if (advertised || clearOutgoing != 0)
            {
                steam.SetRichPresence(PresenceKey, null);
                steam.SetRichPresence(InvitationKey, null);
            }
            advertised = false; clearOutgoing = 0;
            Publish([], null);
        }
        nextRead = 0;
    }

    public void Tick()
    {
        var now = clock();
        if (!online) return;
        if (now - lastRoster > 8) { SetRoster(false, []); return; }
        if (!advertised) { steam.SetRichPresence(PresenceKey, Protocol); advertised = true; }
        if (clearOutgoing != 0 && now >= clearOutgoing) { steam.SetRichPresence(InvitationKey, null); clearOutgoing = 0; }
        var requests = 0;
        foreach (var id in roster.Keys)
            if (requests < 8 && (!nextRequest.TryGetValue(id, out var due) || now >= due))
            { steam.RequestPresence(id); nextRequest[id] = now + 10; requests++; }
        if (pending.IsValid && (now >= pendingUntil || !roster.ContainsKey(pending))) pending = SteamId.None;
        if (pending.IsValid && party() is { State: PartyState.InParty } snapshot)
        {
            var target = pending; pending = SteamId.None;
            if (snapshot.Locked || !Capable(target)) { notice("That player or party is no longer available."); }
            else
            {
                var nonce = Guid.NewGuid().ToString("N");
                steam.SetRichPresence(InvitationKey, $"1:{target}:{snapshot.Lobby}:{nonce}");
                clearOutgoing = now + 45;
                notice($"Invited {Name(target)}. They can accept in the Party panel.");
            }
        }
        if (now < nextRead) return;
        nextRead = now + 1;
        var peers = new List<ServerPlayer>();
        var incoming = current.Invitation is { } old && old.Expires > now && roster.ContainsKey(old.From) ? old : null;
        foreach (var player in roster.Values)
        {
            if (!Capable(player.Id)) continue;
            peers.Add(new(player.Id, Name(player.Id)));
            if (!TryInvitation(steam.RichPresence(player.Id, InvitationKey), steam.LocalId, out var lobby, out var nonce)) continue;
            var key = player.Id + ":" + nonce;
            if (seenInvites.ContainsKey(key) || (nextInvitation.TryGetValue(player.Id, out var next) && now < next)) continue;
            if (seenInvites.Count >= 256)
            {
                var oldest = seenInvites.MinBy(pair => pair.Value).Key;
                seenInvites.Remove(oldest);
            }
            seenInvites[key] = now;
            nextInvitation[player.Id] = now + 15;
            steam.RequestLobbyData(lobby);
            if (party().Lobby == lobby) continue;
            incoming = new(player.Id, Name(player.Id), lobby, nonce, now + 45);
            notice($"{Name(player.Id)} invited you to their party. Open {Party.PanelKey} to accept.");
        }
        Publish(peers.OrderBy(player => player.Name, StringComparer.OrdinalIgnoreCase).ToArray(), incoming);
    }

    bool Capable(SteamId id) => roster.ContainsKey(id) && steam.RichPresence(id, PresenceKey) == Protocol;
    string Name(SteamId id)
    {
        var name = steam.NameOf(id);
        return CleanName(string.IsNullOrWhiteSpace(name) ? roster.GetValueOrDefault(id)?.Name ?? "Player" : name);
    }
    public void Invite(SteamId target)
    {
        var now = clock();
        if (!online || !Capable(target) || now - lastInvite < 5 || party().Members.Any(member => member.Id == target)) return;
        if (party() is { Locked: true, State: PartyState.InParty }) { notice("Unlock the party before inviting another player."); return; }
        if (party().State is not (PartyState.None or PartyState.InParty)) return;
        lastInvite = now; pending = target; pendingUntil = now + 25;
        if (party().State == PartyState.None) create();
    }
    public void Accept()
    {
        if (current.Invitation is not { } invite) return;
        if (!online || clock() >= invite.Expires || !Capable(invite.From)) { Decline(); return; }
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
