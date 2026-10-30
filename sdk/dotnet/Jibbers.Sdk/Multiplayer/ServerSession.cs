using System.Text;
using System.Text.Json.Nodes;

namespace Jibbers.Sdk.Multiplayer;

/// <summary>What <see cref="ServerSession"/> needs from the party and the game (faked in tests).</summary>
internal interface IServerSessionHost
{
    SteamId Me { get; }
    bool InParty { get; }
    IReadOnlyList<PartyMember> Members { get; }
    /// <summary>Sends to one party member on the play-together channel; false if it couldn't be sent now (Steam's buffer is full). Any thread.</summary>
    bool Send(SteamId to, byte[] payload, bool reliable);
    /// <summary>Asks the game to connect to a server (does not wait).</summary>
    void Connect(string address, int port, string password);
    void Announce(string line);
    /// <summary>A line for the log file only (what happened, for working out a failed join).</summary>
    void Log(string line);
    /// <summary>Latency-sensitive traffic is flowing (or stopped): poll for messages faster.</summary>
    void Fast(bool on);
    void CreateParty();
    void OpenInviteOverlay();
    string PanelKey { get; }
    /// <summary>Whether to go along when the party leader brings everyone to their server.</summary>
    bool FollowLeader { get; }
}

/// <summary>
/// Plays the game's own multiplayer over Steam, so friends don't need Hamachi or port forwarding.
/// The player hosting a server tells their party; a member who joins gets a loopback "server"
/// (<see cref="ClientRelay"/>) that the game connects to, and every datagram travels to the host's
/// <see cref="HostRelay"/> and the real server through Steam's relays. The game's networking is
/// untouched: it sees an ordinary connection to 127.0.0.1.
/// </summary>
/// <remarks>
/// Payloads on the channel: byte 0 is <see cref="Data"/> (a datagram follows),
/// <see cref="Control"/> (UTF-8 JSON follows: hosting, stopped, join, welcome, bye) or
/// <see cref="Stream"/> (a frame of a TCP connection: see <see cref="StreamTunnel"/>).
/// The party leader can bring everyone to the server they're on ("pull"): a server reached
/// directly (a public one) by its address, or one hosted in the party by who hosts it.
/// Only party members can reach the channel (the SDK drops everything else).
/// </remarks>
internal sealed class ServerSession : IDisposable
{
    internal const byte Data = 0, Control = 1, Stream = 2;
    const double AnnounceEvery = 2, HostForgetAfter = 7, AskTimeout = 8, ConnectTimeout = 25;
    /// <summary>
    /// How long the game must report its client (or server) stopped before the tunnel closes. It
    /// can read stopped for a moment while it connects or logs in, and closing the tunnel then
    /// is what ends the connection.
    /// </summary>
    const double StoppedFor = 3;

    enum Phase { None, Asking, Connecting, Connected }

    readonly IServerSessionHost host;
    readonly Func<double> clock;
    readonly object gate = new();
    readonly Dictionary<SteamId, double> hosts = new();
    readonly HashSet<SteamId> toldAbout = new();
    ServerInfo game = ServerInfo.None;
    HostRelay? relay;
    ClientRelay? client;
    Phase phase;
    SteamId target;
    double phaseAt, nextAnnounce;
    double? clientStoppedAt, serverStoppedAt;
    bool autoJoin, inviteWhenReady, fast;

    public ServerSession(IServerSessionHost host, Func<double> clock)
    {
        this.host = host;
        this.clock = clock;
    }

    /// <summary>Something the panel shows changed.</summary>
    public event Action? Changed;

    /// <summary>Party members hosting a server you could join.</summary>
    public IReadOnlyList<SteamId> Hosts
    {
        get { lock (gate) return hosts.Keys.ToList(); }
    }

    public bool Hosting
    {
        get { lock (gate) return game.Hosting; }
    }

    /// <summary>One line for the Party panel, or "".</summary>
    public string Status
    {
        get
        {
            lock (gate)
            {
                if (phase is Phase.Asking or Phase.Connecting) return $"Joining {NameOf(target)}'s server…";
                if (phase == Phase.Connected) return $"Playing on {NameOf(target)}'s server";
                if (game.Hosting)
                {
                    var n = relay?.Players.Count ?? 0;
                    return n == 0 ? "Hosting your server" : $"Hosting · {n} joined through Steam";
                }
                return "";
            }
        }
    }

    /// <summary>The next host to announce themselves is joined (you accepted their Steam invite).</summary>
    public void AutoJoinNext()
    {
        lock (gate) autoJoin = true;
    }

    /// <summary>Rows for the Party panel.</summary>
    public List<PanelAction> PanelActions()
    {
        var list = new List<PanelAction>();
        lock (gate)
        {
            if (phase == Phase.None && !game.Hosting)
                foreach (var id in hosts.Keys)
                    list.Add(new PanelAction($"Join {NameOf(id)}'s server", "Play in their world", () => Join(id)));
            // In a party, its own "Invite friends" does this: invited friends join the party and then the server.
            if (game.Hosting && !host.InParty)
                list.Add(new PanelAction("Invite friends to your server", "Makes a party first", InviteToServer));
            if (host.InParty && IsLeader && host.Members.Count > 1 && Where() is not null)
                list.Add(new PanelAction("Bring everyone to this server", "The party joins you here", BringParty));
        }
        return list;
    }

    bool IsLeader => host.Members.Any(m => m.IsLocal && m.IsHost);

    /// <summary>The server you're on, as a pull message (null when you aren't on one others can join).</summary>
    JsonObject? Where()
    {
        if (game.Hosting) return new JsonObject { ["t"] = "pull", ["via"] = host.Me.Value.ToString() };
        if (phase == Phase.Connected) return new JsonObject { ["t"] = "pull", ["via"] = target.Value.ToString() };
        if (game.Client && game.Address.Length > 0 && !IsLoopback(game.Address) && game.Port > 0)
            return new JsonObject { ["t"] = "pull", ["addr"] = game.Address, ["port"] = game.Port, ["pw"] = game.Password };
        return null;
    }

    static bool IsLoopback(string address) => address is "localhost" || (System.Net.IPAddress.TryParse(address, out var ip) && System.Net.IPAddress.IsLoopback(ip));

    /// <summary>The party leader brings everyone to the server they're on: server hopping, or a public server to a private one.</summary>
    public void BringParty()
    {
        JsonObject? where;
        lock (gate) where = Where();
        if (where is null)
        {
            host.Announce("Join or host a server first");
            return;
        }
        var others = host.Members.Where(m => !m.IsLocal).ToList();
        foreach (var m in others) SendControl(m.Id, (JsonObject)where.DeepClone());
        var place = where["via"]?.GetValue<string>() is { } via ? $"{(via == host.Me.Value.ToString() ? "your" : NameOf(new SteamId(ulong.Parse(via))) + "'s")} server" : $"{where["addr"]}:{where["port"]}";
        host.Log($"bringing the party ({others.Count}) to {place}");
        host.Announce("Bringing the party to your server");
    }

    public void InviteToServer()
    {
        if (host.InParty) { host.OpenInviteOverlay(); return; }
        lock (gate) inviteWhenReady = true;
        host.CreateParty();
    }

    /// <summary>Joins a party member's server through Steam.</summary>
    public void Join(SteamId server)
    {
        lock (gate)
        {
            if (game.Hosting || phase != Phase.None || server == host.Me) return;
            target = server;
            phase = Phase.Asking;
            phaseAt = clock();
            autoJoin = false;
        }
        SendControl(server, new JsonObject { ["t"] = "join" });
        Changed?.Invoke();
    }

    /// <summary>The game's networking state, a couple of times a second.</summary>
    public void Update(ServerInfo now)
    {
        var lines = new List<string>();
        var announce = false;
        SteamId joinNow = SteamId.None;
        var openOverlay = false;
        var changed = false;
        lock (gate)
        {
            var t = clock();
            var was = game;
            game = now;
            if (was.Hosting != now.Hosting || was.Client != now.Client || (now.Hosting && was.Port != now.Port))
                host.Log($"game: server {(now.Hosting ? $"on port {now.Port}" : "off")}, client {(now.Client ? "started" : "stopped")}");
            serverStoppedAt = now.Hosting ? null : serverStoppedAt ?? t;
            if (now.Hosting && relay?.ServerPort != now.Port)
            {
                if (relay is not null) host.Log($"server port changed from {relay.ServerPort} to {now.Port}: tunnels reopened");
                relay?.Dispose();
                relay = new HostRelay(now.Port, (peer, data) => host.Send(peer, Frame(data), reliable: false), (peer, frame) => host.Send(peer, frame, reliable: true), host.Log);
                host.Log($"hosting on port {now.Port}{(string.IsNullOrEmpty(now.Password) ? "" : " (with a password)")}: party members can join through Steam");
                lines.Add(host.InParty ? "Hosting: your party can join from the Party panel" : $"Hosting: press {host.PanelKey} to invite friends over Steam");
                nextAnnounce = 0;
                changed = true;
            }
            else if (!now.Hosting && relay is not null && t - serverStoppedAt >= StoppedFor)
            {
                host.Log($"server stopped for {StoppedFor:0} s: closing {relay.Players.Count} tunnel(s)");
                relay.Dispose();
                relay = null;
                foreach (var m in host.Members.Where(m => !m.IsLocal)) SendControl(m.Id, new JsonObject { ["t"] = "stopped" });
                changed = true;
            }
            if (now.Hosting && host.InParty && t >= nextAnnounce)
            {
                nextAnnounce = t + AnnounceEvery;
                announce = true;
            }
            relay?.DropIdle(TimeSpan.FromSeconds(60));

            foreach (var (id, seen) in hosts.ToList())
                if (t - seen > HostForgetAfter || host.Members.All(m => m.Id != id)) { hosts.Remove(id); toldAbout.Remove(id); changed = true; }

            clientStoppedAt = now.Client ? null : clientStoppedAt ?? t;
            switch (phase)
            {
                case Phase.Asking when t - phaseAt > AskTimeout:
                    host.Log($"{NameOf(target)} didn't answer the join request in {AskTimeout:0} s");
                    lines.Add($"{NameOf(target)}'s server didn't answer");
                    EndClient();
                    changed = true;
                    break;
                // Connected only once the game's own traffic has gone through the tunnel both ways:
                // its client can already read started (from singleplayer) before it reconnects.
                case Phase.Connecting when now.Client && client is { GameSeen: true } c && c.Traffic.FromHost > 0:
                    phase = Phase.Connected;
                    host.Log($"the game's client started: joined {NameOf(target)}'s server ({TrafficText()})");
                    lines.Add($"Joined {NameOf(target)}'s server");
                    changed = true;
                    break;
                case Phase.Connecting when t - phaseAt > ConnectTimeout:
                    host.Log($"the game didn't connect within {ConnectTimeout:0} s ({TrafficText()})");
                    lines.Add($"Couldn't connect to {NameOf(target)}'s server");
                    EndClient();
                    changed = true;
                    break;
                case Phase.Connected when !now.Client && t - clientStoppedAt >= StoppedFor:
                    host.Log($"the game's client stopped for {StoppedFor:0} s: leaving {NameOf(target)}'s server ({TrafficText()})");
                    lines.Add($"Left {NameOf(target)}'s server");
                    SendControl(target, new JsonObject { ["t"] = "bye" });
                    EndClient();
                    changed = true;
                    break;
            }

            if (autoJoin && phase == Phase.None && !now.Hosting && hosts.Count > 0) joinNow = hosts.Keys.First();
            if (inviteWhenReady && host.InParty) { inviteWhenReady = false; openOverlay = true; }
            if (was.Hosting != now.Hosting || was.Client != now.Client) changed = true;
            UpdateFast();
        }
        if (announce)
            foreach (var m in host.Members.Where(m => !m.IsLocal)) SendControl(m.Id, new JsonObject { ["t"] = "hosting" });
        foreach (var line in lines) host.Announce(line);
        if (openOverlay) host.OpenInviteOverlay();
        if (joinNow.IsValid) Join(joinNow);
        if (changed) Changed?.Invoke();
    }

    /// <summary>A message on the play-together channel (the party thread).</summary>
    public void Receive(PartyMember from, ReadOnlySpan<byte> payload)
    {
        if (payload.Length == 0 || from.IsLocal) return;
        if (payload[0] == Data)
        {
            HostRelay? r;
            ClientRelay? c;
            lock (gate) { r = relay; c = client; }
            if (c is not null && c.Host == from.Id) c.FromHost(payload[1..]);
            else r?.FromPeer(from.Id, payload[1..]);
            lock (gate) UpdateFast();
            return;
        }
        if (payload[0] == Stream)
        {
            if (!StreamTunnel.TryParse(payload, out var id, out var op, out var bytes)) return;
            HostRelay? r;
            ClientRelay? c;
            lock (gate) { r = relay; c = client; }
            if (c is not null && c.Host == from.Id) c.FromHostStream(id, op, bytes);
            else r?.FromPeerStream(from.Id, id, op, bytes);
            return;
        }
        if (payload[0] != Control) return;
        JsonNode? json;
        try { json = JsonNode.Parse(payload[1..]); }
        catch (System.Text.Json.JsonException) { return; }
        var kind = json?["t"]?.GetValue<string>();
        if (kind != "hosting") host.Log($"{from.Name}: {kind}");
        string? line = null;
        var changed = false;
        var leaveTunnel = false;
        SteamId pullTo = SteamId.None;
        (string Address, int Port, string Password)? connectTo = null;
        lock (gate)
        {
            switch (kind)
            {
                case "pull" when !from.IsHost:
                    break; // only the party leader brings the party
                case "pull" when game.Hosting:
                    line = $"{from.Name} wants the party on their server: stop hosting yours to join them";
                    break;
                case "pull" when !host.FollowLeader:
                    line = $"{from.Name} brought the party to their server (following the leader is off in Play together's settings)";
                    break;
                case "pull":
                    if (json?["via"]?.GetValue<string>() is { } via)
                    {
                        if (!ulong.TryParse(via, out var v) || new SteamId(v) == host.Me) break;
                        var server = new SteamId(v);
                        if (phase != Phase.None && target == server) break; // there already
                        leaveTunnel = phase != Phase.None;
                        pullTo = server;
                    }
                    else
                    {
                        var address = json?["addr"]?.GetValue<string>() ?? "";
                        var port = json?["port"]?.GetValue<int>() ?? 0;
                        var pw = json?["pw"]?.GetValue<string>() ?? "";
                        if (address.Length is 0 or > 255 || port is <= 0 or > 65535 || pw.Length > 256 || IsLoopback(address)) break;
                        if (game.Client && phase == Phase.None && game.Address == address && game.Port == port) break; // there already
                        leaveTunnel = phase != Phase.None;
                        connectTo = (address, port, pw);
                    }
                    line = $"Following {from.Name} to their server";
                    break;
                case "hosting":
                    changed = !hosts.ContainsKey(from.Id);
                    hosts[from.Id] = clock();
                    if (toldAbout.Add(from.Id) && phase == Phase.None && !game.Hosting && !autoJoin) line = $"{from.Name} is hosting: press {host.PanelKey} to join";
                    break;
                case "stopped":
                    changed = hosts.Remove(from.Id);
                    toldAbout.Remove(from.Id);
                    break;
                case "join" when game.Hosting:
                    SendControl(from.Id, new JsonObject { ["t"] = "welcome", ["pw"] = game.Password });
                    host.Log($"{from.Name} is joining: sent the welcome (server port {game.Port})");
                    line = $"{from.Name} is joining your server";
                    break;
                case "join":
                    host.Log($"{from.Name} asked to join, but you aren't hosting");
                    break;
                case "welcome" when phase == Phase.Asking && from.Id == target:
                    var password = json?["pw"]?.GetValue<string>() ?? "";
                    if (password.Length > 256) break;
                    client = new ClientRelay(target, (to, data) => host.Send(to, Frame(data), reliable: false), reliable: (to, frame) => host.Send(to, frame, reliable: true), log: host.Log);
                    phase = Phase.Connecting;
                    phaseAt = clock();
                    host.Log($"welcomed by {from.Name}: the game connects to 127.0.0.1:{client.Port}{(password.Length > 0 ? " with the server's password" : "")}");
                    host.Connect("127.0.0.1", client.Port, password);
                    changed = true;
                    break;
                case "bye":
                    if (relay?.Traffic(from.Id) is { } traffic) host.Log($"{from.Name} left: closing their tunnel ({traffic.ToServer} packets to the server, {traffic.ToPlayer} back)");
                    relay?.Drop(from.Id);
                    changed = true;
                    break;
            }
            UpdateFast();
        }
        if (line is not null) host.Announce(line);
        if (leaveTunnel)
            lock (gate)
            {
                host.Log($"leaving {NameOf(target)}'s server to follow {from.Name}");
                SendControl(target, new JsonObject { ["t"] = "bye" });
                EndClient();
                UpdateFast();
                changed = true;
            }
        if (pullTo.IsValid) Join(pullTo);
        if (connectTo is { } to)
        {
            host.Log($"following {from.Name} to {to.Address}:{to.Port}");
            host.Connect(to.Address, to.Port, to.Password);
        }
        if (changed) Changed?.Invoke();
    }

    /// <summary>A member left the party: forget their server, close their tunnel.</summary>
    public void MemberLeft(SteamId id)
    {
        string? line = null;
        lock (gate)
        {
            hosts.Remove(id);
            toldAbout.Remove(id);
            if (relay?.Traffic(id) is { } traffic) host.Log($"{NameOf(id)} left the party: closing their tunnel ({traffic.ToServer} packets to the server, {traffic.ToPlayer} back)");
            relay?.Drop(id);
            if (phase != Phase.None && target == id)
            {
                line = $"{NameOf(id)} left the party";
                EndClient();
            }
            UpdateFast();
        }
        if (line is not null) host.Announce(line);
        Changed?.Invoke();
    }

    string TrafficText() => client is { } c ? $"{c.Traffic.ToHost} packets sent, {c.Traffic.FromHost} received" : "no tunnel";

    void EndClient()
    {
        client?.Dispose();
        client = null;
        phase = Phase.None;
        target = SteamId.None;
    }

    void UpdateFast()
    {
        var want = client is not null || relay?.Players.Count > 0;
        if (want == fast) return;
        fast = want;
        host.Fast(want);
    }

    string NameOf(SteamId id) => host.Members.FirstOrDefault(m => m.Id == id)?.Name ?? "your friend";

    void SendControl(SteamId to, JsonObject json)
    {
        var text = Encoding.UTF8.GetBytes(json.ToJsonString());
        var payload = new byte[text.Length + 1];
        payload[0] = Control;
        text.CopyTo(payload, 1);
        host.Send(to, payload, reliable: true);
    }

    static byte[] Frame(byte[] datagram)
    {
        var payload = new byte[datagram.Length + 1];
        payload[0] = Data;
        datagram.CopyTo(payload, 1);
        return payload;
    }

    public void Dispose()
    {
        lock (gate)
        {
            relay?.Dispose();
            relay = null;
            EndClient();
            if (fast) { fast = false; host.Fast(false); }
        }
    }
}
