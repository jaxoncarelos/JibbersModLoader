using System.Reflection;
using System.Runtime.InteropServices;
using Jibbers.Sdk.Multiplayer;

namespace SnowShovel.Integration;

/// <summary>
/// Snow for everyone on your game server (not just your party): the players on the server are read
/// from the game's roster (as the mod loader reads it), and messages go straight to each of them over
/// Steam (relayed by Valve, on Snow Shovel's own channel), so only players running Snow Shovel ever
/// read them. Roster reads are main thread only; sending and receiving are any thread.
/// </summary>
internal sealed unsafe class ServerLink
{
    const int IdentitySteamId = 16, IdentitySize = 136, SendReliable = 8, SendUnreliable = 0;
    /// <summary>The Steam channel this link talks on (Snow Shovel's "SN"; the SDK's party has its own).</summary>
    readonly int channel;
    readonly nint msgs;
    readonly delegate* unmanaged<nint, byte*, void*, uint, int, int, int> sendToUser;
    readonly delegate* unmanaged<nint, int, nint*, int, int> receive;
    readonly delegate* unmanaged<nint, byte*, byte> accept;
    readonly ulong me;
    readonly MethodInfo? roster;
    HashSet<ulong> players = new();

    ServerLink(nint lib, int channel)
    {
        this.channel = channel;
        nint Get(string name) => NativeLibrary.GetExport(lib, name);
        msgs = ((delegate* unmanaged<nint>)Get("SteamAPI_SteamNetworkingMessages_SteamAPI_v002"))();
        var user = ((delegate* unmanaged<nint>)Get("SteamAPI_SteamUser_v023"))();
        if (msgs == 0 || user == 0) throw new InvalidOperationException("Steam isn't running");
        me = ((delegate* unmanaged<nint, ulong>)Get("SteamAPI_ISteamUser_GetSteamID"))(user);
        sendToUser = (delegate* unmanaged<nint, byte*, void*, uint, int, int, int>)Get("SteamAPI_ISteamNetworkingMessages_SendMessageToUser");
        receive = (delegate* unmanaged<nint, int, nint*, int, int>)Get("SteamAPI_ISteamNetworkingMessages_ReceiveMessagesOnChannel");
        accept = (delegate* unmanaged<nint, byte*, byte>)Get("SteamAPI_ISteamNetworkingMessages_AcceptSessionWithUser");
        // The mod loader's own reader of the server's players (SteamId + name records).
        roster = typeof(Party).Assembly.GetType("Jibbers.Sdk.Multiplayer.CurrentServerPlayers")
            ?.GetMethod("Read", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
    }

    [DllImport("kernel32", CharSet = CharSet.Unicode)]
    static extern nint GetModuleHandleW(string name);

    /// <summary>The link, or null (and why) when the game's Steam or the roster reader isn't there.</summary>
    public static ServerLink? Open(out string why, int channel = 0x534E)
    {
        why = "";
        try
        {
            var lib = GetModuleHandleW("steam_api64.dll");
            if (lib == 0) { why = "the game hasn't loaded Steam"; return null; }
            var link = new ServerLink(lib, channel);
            if (link.roster is null) { why = "this mod loader can't list the server's players"; return null; }
            return link;
        }
        catch (Exception e) when (e is EntryPointNotFoundException or InvalidOperationException)
        {
            why = e.Message;
            return null;
        }
    }

    /// <summary>The other players on your game server right now (their Steam ids). Main thread.</summary>
    public IReadOnlyCollection<ulong> Players => players;

    /// <summary>Re-reads who's on the server; returns the ones who just arrived. Main thread.</summary>
    public List<ulong> Refresh()
    {
        var now = new HashSet<ulong>();
        if (roster?.Invoke(null, null) is System.Collections.IEnumerable list)
            foreach (var p in list)
                if (p?.GetType().GetProperty("Id")?.GetValue(p) is SteamId id && id.IsValid && id.Value != me)
                    now.Add(id.Value);
        var arrived = now.Where(id => !players.Contains(id)).ToList();
        players = now;
        // Let them talk to us (Steam drops messages from people we haven't accepted or written to).
        var who = stackalloc byte[IdentitySize];
        foreach (var id in arrived)
        {
            Identity(who, id);
            accept(msgs, who);
        }
        return arrived;
    }

    /// <summary>Lets a player (found some other way, such as a lobby) talk to us.</summary>
    public void Accept(ulong who)
    {
        var id = stackalloc byte[IdentitySize];
        Identity(id, who);
        accept(msgs, id);
    }

    static void Identity(byte* id, ulong who)
    {
        new Span<byte>(id, IdentitySize).Clear();
        *(int*)id = IdentitySteamId;
        *(int*)(id + 4) = 8;
        *(ulong*)(id + 8) = who;
    }

    /// <summary>Sends to one player: reliably (resent until it arrives, in order), or not (frequent updates: a lost one's replaced by the next).</summary>
    public bool Send(ulong to, ReadOnlySpan<byte> data, bool reliable = true)
    {
        var id = stackalloc byte[IdentitySize];
        Identity(id, to);
        fixed (byte* d = data)
            return sendToUser(msgs, id, d, (uint)data.Length, reliable ? SendReliable : SendUnreliable, channel) == 1;
    }

    public int Broadcast(ReadOnlySpan<byte> data, bool reliable = true)
    {
        var sent = 0;
        foreach (var id in players)
            if (Send(id, data, reliable)) sent++;
        return sent;
    }

    /// <summary>Messages waiting for Snow Shovel: who from, and what.</summary>
    public List<(ulong From, byte[] Data)> Receive()
    {
        var got = new List<(ulong, byte[])>();
        var batch = stackalloc nint[32];
        for (var rounds = 0; rounds < 8; rounds++)
        {
            var n = receive(msgs, channel, batch, 32);
            if (n <= 0) break;
            for (var i = 0; i < n; i++)
            {
                // SteamNetworkingMessage_t: data, size, connection, peer identity (at 16), ..., release (at 184).
                var m = (byte*)batch[i];
                var size = *(int*)(m + 8);
                if (*(int*)(m + 16) == IdentitySteamId && size > 0)
                    got.Add((*(ulong*)(m + 24), new ReadOnlySpan<byte>(*(byte**)m, size).ToArray()));
                ((delegate* unmanaged<nint, void>)*(nint*)(m + 184))((nint)m);
            }
            if (n < 32) break;
        }
        return got;
    }
}
