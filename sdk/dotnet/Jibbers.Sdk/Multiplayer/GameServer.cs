using Jibbers.Interop.Game;
using Jibbers.Sdk.Interop;

namespace Jibbers.Sdk.Multiplayer;

/// <summary>What the game's own networking (FishNet) is doing, read on the main thread.</summary>
internal readonly record struct ServerInfo(bool Hosting, bool Client, int Port, string Password)
{
    public static readonly ServerInfo None = new(false, false, 0, "");
}

/// <summary>
/// The game's networking, through its network manager (<c>ks</c>, which owns FishNet's
/// NetworkManager and the Tugboat UDP transport). The server browser's "join" is
/// <c>ks.Connect(address, port, password)</c>; joining through the tunnel calls exactly that with
/// the local relay's address. Main thread only.
/// </summary>
internal static class GameServer
{
    static Il2CppClass tugboat;
    static bool searched;
    static GameRef? manager, transport;

    /// <summary>The game's network manager, found once and kept while it lives (finding objects is slow).</summary>
    static ks Manager()
    {
        if (manager is { } m && !Unity.IsNull(m.Target)) return new ks(m.Target);
        manager?.Dispose();
        manager = null;
        var k = ks.Live().FirstOrDefault(k => !k.IsNull && !Unity.IsNull(k.Ptr));
        if (!k.IsNull) manager = new GameRef(k.Ptr);
        return k;
    }

    public static ServerInfo Read()
    {
        var k = Manager();
        if (k.IsNull) return ServerInfo.None;
        var network = ks.qxx_Field.GetObject(k.Ptr);
        if (network == 0 || Unity.IsNull(network)) return ServerInfo.None;
        var type = Il2Cpp.ClassOf(network);
        var hosting = Flag(type, network, "get_IsServerStarted");
        var client = Flag(type, network, "get_IsClientStarted");
        if (!hosting) return new ServerInfo(false, client, 0, "");
        var auth = k.qxy;
        var password = auth.IsNull ? "" : auth.serverPassword ?? "";
        return new ServerInfo(true, client, Port(), password);
    }

    /// <summary>Connects the game to a server, as the server browser's join does.</summary>
    public static void Connect(string address, int port, string password)
    {
        var k = Manager();
        if (k.IsNull) throw new InvalidOperationException("the game's network manager isn't loaded");
        k.Connect(address, (ushort)port, password);
    }

    static bool Flag(Il2CppClass type, nint obj, string getter)
    {
        var m = type.Method(getter, 0);
        return !m.IsNull && m.Invoke<bool>(obj);
    }

    /// <summary>The port the server listens on: Tugboat's, else the game's default.</summary>
    static int Port()
    {
        if (!searched)
        {
            searched = true;
            foreach (var (_, name) in Il2Cpp.Assemblies())
            {
                var k = Il2Cpp.Class(name, "FishNet.Transporting.Tugboat", "Tugboat");
                if (!k.IsNull) { tugboat = k; break; }
            }
        }
        var getPort = tugboat.IsNull ? default : tugboat.Method("GetPort", 0);
        if (getPort.IsNull) return ks.DefaultPort;
        if (transport is not { } t || Unity.IsNull(t.Target))
        {
            transport?.Dispose();
            transport = null;
            var found = Unity.FindObjects(tugboat).FirstOrDefault(o => !Unity.IsNull(o));
            if (found == 0) return ks.DefaultPort;
            transport = new GameRef(found);
        }
        var port = getPort.Invoke<ushort>(transport.Target);
        return port != 0 ? port : ks.DefaultPort;
    }
}
