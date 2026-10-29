using System.Diagnostics;

namespace Jibbers.Sdk.Multiplayer;

/// <summary>
/// Built into the SDK for everyone: host a server (the console's <c>host</c>, or the game's own
/// host option) and your party can join it through Steam, or invite friends from the Party panel.
/// No Hamachi, no port forwarding, and nobody's IP address is shared. See <see cref="ServerSession"/>.
/// </summary>
internal sealed class PlayTogether : JibbersMod, IServerSessionHost
{
    public override string Id => "jibbers_play";
    public override string Name => "Play together";
    public override string Author => "Jibbers SDK";
    public override string Description => "Host a server and your Steam friends can join it from the Party panel: no Hamachi or port forwarding.";

    [Toggle("Join a friend's server when you accept their invite", Key = "auto_join", Tooltip = "Accepting a Steam invite (or Join game) takes you straight into their server.")]
    public bool AutoJoin { get; private set; }

    /// <summary>The running session, for the Party panel.</summary>
    internal static ServerSession? Current { get; private set; }

    PartyChannel? channel;
    ServerSession? session;
    Thread? ticker;
    volatile bool stopping;
    bool fastHeld;
    string lastError = "";

    public override void OnLoad()
    {
        var clock = Stopwatch.StartNew();
        session = new ServerSession(this, () => clock.Elapsed.TotalSeconds);
        channel = Party.Channel(this);
        channel.Received += message => session.Receive(message.From, message.Data.Span);
        Party.MemberChanged += OnMember;
        SteamInvites.Requested += OnInvite;
        SteamInvites.Install();
        Current = session;
        ticker = new Thread(Tick) { IsBackground = true, Name = "Jibbers Play together" };
        ticker.Start();
    }

    public override void OnUnload()
    {
        stopping = true;
        Current = null;
        Party.MemberChanged -= OnMember;
        SteamInvites.Requested -= OnInvite;
        SteamInvites.Remove();
        session?.Dispose();
        channel?.Dispose();
    }

    void OnMember(PartyMember member, bool joined)
    {
        if (!joined) session?.MemberLeft(member.Id);
    }

    void OnInvite(SteamId lobby)
    {
        if (AutoJoin) session?.AutoJoinNext();
        Party.JoinLobby(lobby);
    }

    void Tick()
    {
        while (!stopping)
        {
            Thread.Sleep(500);
            if (!Enabled || session is null) continue;
            ServerInfo info;
            try
            {
                info = MainThread.Run(GameServer.Read);
                lastError = "";
            }
            catch (TimeoutException)
            {
                continue; // loading: try again next time
            }
            catch (Exception e)
            {
                if (e.Message != lastError) Log(e.Message);
                lastError = e.Message;
                info = ServerInfo.None;
            }
            try { session.Update(info); }
            catch (Exception e) { Sdk.Log.Error("play together", e); }
        }
    }

    // ---- IServerSessionHost ----

    SteamId IServerSessionHost.Me => Party.Local?.Id ?? SteamId.None;
    bool IServerSessionHost.InParty => Party.State == PartyState.InParty;
    IReadOnlyList<PartyMember> IServerSessionHost.Members => Party.Members;
    string IServerSessionHost.PanelKey => Party.PanelKey;
    void IServerSessionHost.Send(SteamId to, byte[] payload, bool reliable) => channel?.Send(to, payload, reliable ? Delivery.Reliable : Delivery.Unreliable);
    void IServerSessionHost.Announce(string line) => Party.Announce(line);
    void IServerSessionHost.CreateParty() => Party.Create();
    void IServerSessionHost.OpenInviteOverlay() => Party.OpenInviteOverlay();

    void IServerSessionHost.Connect(string address, int port, string password)
    {
        Log($"joining a server through Steam (game connects to {address}:{port})");
        MainThread.Later(() => GameServer.Connect(address, port, password));
    }

    void IServerSessionHost.Fast(bool on)
    {
        if (on == fastHeld) return;
        fastHeld = on;
        if (on) Interlocked.Increment(ref Party.FastPoll);
        else Interlocked.Decrement(ref Party.FastPoll);
    }
}
