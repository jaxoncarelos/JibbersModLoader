using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;

namespace Jibbers.Sdk.Multiplayer;

/// <summary>
/// Play with other people who have your mod, over Steam. Players gather in a <b>party</b> (a Steam
/// lobby the SDK keeps for them, shared by every multiplayer mod): one player makes it, others join
/// with its 6-character code or from their Steam friends list. Your mod then talks to the other
/// members through its own <see cref="PartyChannel"/>.
/// </summary>
/// <remarks>
/// Messages go peer to peer through Valve's relays (no ports, no IP addresses shared), only between
/// party members. Party messages are separate from the game's own networking (the SDK's built-in
/// Play together uses the party to tunnel game servers, see docs/guides/multiplayer.md).
/// Players get a Party panel (Mods tab > Party, default key F7) to make, join and leave parties, so
/// your mod usually only needs <see cref="Channel"/>. Everything here is safe from any thread.
/// </remarks>
/// <example>
/// <code>
/// channel = Party.Channel(this);
/// channel.Received += message => Log($"{message.From.Name} says {message.Text}");
/// channel.Broadcast("hello");
/// </code>
/// </example>
public static class Party
{
    static readonly object gate = new();
    static readonly Dictionary<string, PartyChannel> channels = new(StringComparer.Ordinal);
    static PartyEngine? engine;
    /// <summary>Requests made before Steam was ready; run once it is.</summary>
    static readonly List<Action<PartyEngine>> early = new();
    static Thread? thread;
    static volatile bool stopping;
    static volatile PartySnapshot current = PartySnapshot.Unavailable;

    /// <summary>Where the local player is (none, joining, in a party...).</summary>
    public static PartyState State => current.State;
    /// <summary>The party's join code ("K7Q2MX"), or "" outside a party.</summary>
    public static string Code => current.Code;
    /// <summary>Everyone in the party, host first; empty outside a party.</summary>
    public static IReadOnlyList<PartyMember> Members => current.Members;
    /// <summary>You, while in a party.</summary>
    public static PartyMember? Local => current.Local;
    /// <summary>The party's host (the Steam lobby owner; it moves on if they leave). Good for running a game's rules.</summary>
    public static PartyMember? Host => current.Host;
    /// <summary>True while you are the host.</summary>
    public static bool IsHost => current.Host?.IsLocal == true;
    /// <summary>True while the host stops new players from joining.</summary>
    public static bool Locked => current.Locked;
    /// <summary>Online Steam friends, those in a joinable party first.</summary>
    public static IReadOnlyList<FriendParty> Friends => current.Friends;
    /// <summary>The last problem or progress line ("No party found with code ..."), or "".</summary>
    public static string Status => current.Status;
    /// <summary>The key that opens the Party panel ("F7" unless the player rebound it), for your own hints.</summary>
    public static string PanelKey => PanelKeySource?.Invoke() ?? "F7";
    internal static Func<string>? PanelKeySource;

    /// <summary>The party changed: state, members, host, lock, friends. Called on the SDK's party thread.</summary>
    public static event Action? Changed;
    /// <summary>A member joined (<c>true</c>) or left (<c>false</c>). Called on the SDK's party thread.</summary>
    public static event Action<PartyMember, bool>? MemberChanged;
    /// <summary>A short line for the player ("Ben joined the party"); the Party panel shows these as toasts.</summary>
    public static event Action<string>? Notice;

    /// <summary>True once any mod has used the party (the SDK then shows the Party panel).</summary>
    internal static bool Used { get; private set; }

    /// <summary>Makes a new party (you become its host). Ignored while making, joining or in one.</summary>
    public static void Create() => Do(e => e.Create());
    /// <summary>Joins the party with this code (leaving the current one). Spaces and dashes are ignored.</summary>
    public static void Join(string code) => Do(e => e.JoinCode(code ?? ""));
    /// <summary>Joins the party a Steam friend is in (see <see cref="Friends"/>).</summary>
    public static void JoinFriend(SteamId friend) => Do(e => e.JoinFriend(friend));
    /// <summary>Leaves the party.</summary>
    public static void Leave() => Do(e => e.Leave());
    /// <summary>Host only: stops (or allows again) new players joining.</summary>
    public static void SetLocked(bool locked) => Do(e => e.SetLocked(locked));
    /// <summary>Sends a Steam invite to the party to a friend.</summary>
    public static void Invite(SteamId friend) => Do(e => e.Invite(friend));
    /// <summary>Opens the Steam overlay's invite dialog for the party.</summary>
    public static void OpenInviteOverlay() => Do(e => e.OpenInviteOverlay());

    /// <summary>Joins the party behind a Steam lobby (an accepted Steam invite or "Join game").</summary>
    internal static void JoinLobby(SteamId lobby) => Do(e => e.JoinLobby(lobby));

    /// <summary>Shows a short line to the player, like the party's own notices.</summary>
    internal static void Announce(string line) => Raise(Notice, line);

    /// <summary>
    /// While above zero the party thread checks for messages every millisecond instead of every
    /// 15, for latency-sensitive traffic (the server tunnel). Increment, and decrement when done.
    /// </summary>
    internal static int FastPoll;

    /// <summary>
    /// Your mod's message channel. Other members see that you run the mod (<see cref="PartyMember.HasMod"/>),
    /// and only their copy of the same mod receives what you send. The channel closes when your mod unloads.
    /// </summary>
    public static PartyChannel Channel(JibbersMod mod)
    {
        ArgumentNullException.ThrowIfNull(mod);
        PartyChannel channel;
        lock (gate)
        {
            if (!channels.TryGetValue(mod.Id, out channel!))
                channels[mod.Id] = channel = new PartyChannel(mod.Id, mod.Version, Remove);
            Used = true;
        }
        AdvertiseMods();
        return channel;
    }

    static void Remove(PartyChannel channel)
    {
        lock (gate)
            if (channels.TryGetValue(channel.ModId, out var c) && ReferenceEquals(c, channel)) channels.Remove(channel.ModId);
        AdvertiseMods();
    }

    internal static void RemoveOwner(string mod)
    {
        PartyChannel? channel;
        lock (gate) channels.TryGetValue(mod, out channel);
        channel?.Dispose();
    }

    static void AdvertiseMods()
    {
        Dictionary<string, string> mods;
        lock (gate) mods = channels.Values.ToDictionary(c => c.ModId, c => c.ModVersion, StringComparer.Ordinal);
        Do(e => e.SetLocalMods(mods));
    }

    internal static bool Send(SteamId to, string mod, byte[] payload, Delivery delivery) =>
        Engine() is { } e && e.Send(to, mod, payload, delivery == Delivery.Reliable);

    /// <summary>Runs a request now, or once Steam is ready.</summary>
    static void Do(Action<PartyEngine> request)
    {
        if (Engine() is { } e) { request(e); return; }
        lock (gate)
        {
            if (engine is null) { if (Runtime.Native.Ready && early.Count < 32) early.Add(request); return; }
        }
        request(engine);
    }

    /// <summary>Starts the party thread the first time anything needs it; null outside the game.</summary>
    static PartyEngine? Engine()
    {
        if (!Runtime.Native.Ready) return null;
        lock (gate)
        {
            if (thread is null)
            {
                Used = true;
                thread = new Thread(Run) { IsBackground = true, Name = "Jibbers Party" };
                thread.Start();
            }
            return engine;
        }
    }

    /// <summary>Leaves the party when the game closes.</summary>
    internal static void Shutdown()
    {
        stopping = true;
        thread?.Join(1000);
        engine?.Leave();
        engine?.Tick();
    }

    static void Run()
    {
        // Steam comes up with the game; wait for it rather than failing.
        string lastWhy = "";
        ISteamBackend? steam = null;
        while (!stopping && steam is null)
        {
            steam = SteamFlat.TryOpen(out var why);
            if (steam is null)
            {
                if (why != lastWhy) Log.Info("party", $"waiting for Steam: {why}");
                lastWhy = why;
                Thread.Sleep(3000);
            }
        }
        if (steam is null) return;
        var clock = Stopwatch.StartNew();
        var e = new PartyEngine(steam, () => clock.Elapsed.TotalSeconds, line => Log.Info("party", line));
        e.Deliver = Deliver;
        e.Changed = snapshot => { current = snapshot; Raise(Changed); };
        e.Membership = (member, joined) => Raise(MemberChanged, member, joined);
        e.Notice = line => Raise(Notice, line);
        Action<PartyEngine>[] waiting;
        lock (gate)
        {
            engine = e;
            waiting = early.ToArray();
            early.Clear();
        }
        Log.Info("party", $"Steam ready (you are {steam.LocalName})");
        AdvertiseMods();
        foreach (var request in waiting) request(e);
        current = e.Snapshot;
        Raise(Changed);
        var failures = 0;
        while (!stopping)
        {
            try
            {
                e.Tick();
                failures = 0;
            }
            catch (Exception ex)
            {
                if (failures++ < 3) Log.Error("party", ex);
            }
            Thread.Sleep(Volatile.Read(ref FastPoll) > 0 ? 1 : e.Snapshot.State == PartyState.InParty ? 15 : 100);
        }
    }

    static void Deliver(PartyMember from, string mod, ReadOnlyMemory<byte> payload)
    {
        PartyChannel? channel;
        lock (gate) channels.TryGetValue(mod, out channel);
        channel?.Dispatch(new PartyMessage(from, payload));
    }

    static void Raise(Action? handler)
    {
        if (handler is null) return;
        foreach (var h in handler.GetInvocationList().Cast<Action>())
            try { h(); } catch (Exception e) { Log.Error("party: Changed handler", e); }
    }

    static void Raise<T>(Action<T>? handler, T a)
    {
        if (handler is null) return;
        foreach (var h in handler.GetInvocationList().Cast<Action<T>>())
            try { h(a); } catch (Exception e) { Log.Error("party: handler", e); }
    }

    static void Raise<T, U>(Action<T, U>? handler, T a, U b)
    {
        if (handler is null) return;
        foreach (var h in handler.GetInvocationList().Cast<Action<T, U>>())
            try { h(a, b); } catch (Exception e) { Log.Error("party: handler", e); }
    }
}

/// <summary>
/// One mod's messages in the party (from <see cref="Party.Channel"/>). Sending is safe from any
/// thread; <see cref="Received"/> runs on the SDK's party thread, one message at a time, in order
/// per sender (for reliable messages). Wrap game calls in <see cref="MainThread.Run(Action)"/>.
/// </summary>
public sealed class PartyChannel : IDisposable
{
    readonly Action<PartyChannel> remove;
    readonly object gate = new();
    readonly List<(string? Owner, Action<PartyMessage> Handler)> handlers = new();
    bool disposed;

    internal PartyChannel(string modId, string version, Action<PartyChannel> remove)
    {
        ModId = modId;
        ModVersion = version;
        this.remove = remove;
    }

    /// <summary>The mod this channel belongs to.</summary>
    public string ModId { get; }
    internal string ModVersion { get; }

    /// <summary>A member sent your mod a message. Called on the party thread; skipped while your mod is turned off.</summary>
    public event Action<PartyMessage> Received
    {
        add { lock (gate) handlers.Add((ModContext.CurrentId ?? ModId, value)); }
        remove { lock (gate) handlers.RemoveAll(h => h.Handler == value); }
    }

    /// <summary>The other party members who run this mod.</summary>
    public IReadOnlyList<PartyMember> Peers => Party.Members.Where(m => !m.IsLocal && m.HasMod(ModId)).ToList();
    /// <summary>Everyone in the party who runs this mod, you included.</summary>
    public IReadOnlyList<PartyMember> Players => Party.Members.Where(m => m.IsLocal || m.HasMod(ModId)).ToList();

    /// <summary>Sends bytes (at most 64 KB) to one member; sending to yourself delivers to your own <see cref="Received"/>. False if they are not in the party.</summary>
    public bool Send(SteamId to, ReadOnlySpan<byte> data, Delivery delivery = Delivery.Reliable)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (data.Length > Wire.MaxPayload) throw new ArgumentException($"messages are limited to {Wire.MaxPayload / 1024} KB", nameof(data));
        return Party.Send(to, ModId, data.ToArray(), delivery);
    }
    /// <summary>Sends UTF-8 text to one member.</summary>
    public bool Send(SteamId to, string text, Delivery delivery = Delivery.Reliable) => Send(to, Encoding.UTF8.GetBytes(text), delivery);
    /// <summary>Sends JSON to one member (read it with <see cref="PartyMessage.Json"/>).</summary>
    public bool Send(SteamId to, JsonNode json, Delivery delivery = Delivery.Reliable) => Send(to, json.ToJsonString(), delivery);

    /// <summary>Sends to every other member who runs this mod (and to yourself with <paramref name="includeSelf"/>). Returns how many were sent.</summary>
    public int Broadcast(ReadOnlySpan<byte> data, Delivery delivery = Delivery.Reliable, bool includeSelf = false)
    {
        var sent = 0;
        foreach (var m in includeSelf ? Players : Peers)
            if (Send(m.Id, data, delivery)) sent++;
        return sent;
    }
    /// <summary>Broadcasts UTF-8 text.</summary>
    public int Broadcast(string text, Delivery delivery = Delivery.Reliable, bool includeSelf = false) => Broadcast(Encoding.UTF8.GetBytes(text), delivery, includeSelf);
    /// <summary>Broadcasts JSON.</summary>
    public int Broadcast(JsonNode json, Delivery delivery = Delivery.Reliable, bool includeSelf = false) => Broadcast(json.ToJsonString(), delivery, includeSelf);

    /// <summary>Sends to the party host (yourself, if you are the host). False outside a party.</summary>
    public bool SendToHost(JsonNode json, Delivery delivery = Delivery.Reliable) => Party.Host is { } host && Send(host.Id, json, delivery);

    internal void Dispatch(PartyMessage message)
    {
        (string? Owner, Action<PartyMessage> Handler)[] list;
        lock (gate)
        {
            if (disposed) return;
            list = handlers.ToArray();
        }
        foreach (var (owner, handler) in list)
        {
            if (!ModContext.Enabled(owner)) continue;
            try { ModContext.Run(owner, () => handler(message)); }
            catch (Exception e) { Log.Error($"{ModId}: party message from {message.From.Name}", e); }
        }
    }

    /// <summary>Closes the channel: no more messages either way, and the party stops listing your mod. Also done when your mod unloads.</summary>
    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            handlers.Clear();
        }
        remove(this);
    }
}
