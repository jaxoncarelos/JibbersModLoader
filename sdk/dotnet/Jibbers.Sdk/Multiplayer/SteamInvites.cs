using System.Runtime.InteropServices;

namespace Jibbers.Sdk.Multiplayer;

/// <summary>
/// Accepting a friend's Steam invite, or "Join game" on them in the friends list, joins their party.
/// Steam reports those as callbacks, which the game pulls itself (Steamworks.NET's manual dispatch
/// calls <c>SteamAPI_ManualDispatch_GetNextCallback</c> each frame and drops the ones it doesn't
/// use), so the SDK watches that call. When the game wasn't running, Steam starts it with
/// <c>+connect_lobby &lt;id&gt;</c> instead, which is read from the command line.
/// </summary>
internal static unsafe class SteamInvites
{
    const int GameLobbyJoinRequested = 333, GameRichPresenceJoinRequested = 337;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate byte NextCallback(int pipe, nint message);

    static Detour<NextCallback>? detour;
    static volatile bool seen;

    /// <summary>A lobby to join arrived (on the game's thread; keep it quick).</summary>
    public static event Action<SteamId>? Requested;

    public static void Install()
    {
        if (CommandLineLobby() is { IsValid: true } launched) Raise(launched);
        if (detour is not null) return;
        var lib = SteamFlat.Library();
        if (lib == 0 || !NativeLibrary.TryGetExport(lib, "SteamAPI_ManualDispatch_GetNextCallback", out var target))
        {
            Log.Info("party", "Steam invites: the game's Steam has no manual dispatch; joining from Steam works by code only");
            return;
        }
        detour = Detour<NextCallback>.Create(target, Next);
        detour.Enable();
    }

    public static void Remove()
    {
        detour?.Dispose();
        detour = null;
    }

    static byte Next(int pipe, nint message)
    {
        var got = detour!.Original(pipe, message);
        if (got == 0 || message == 0) return got;
        try
        {
            // CallbackMsg_t { HSteamUser user; int callback; uint8* param; int size; }
            var id = *(int*)(message + 4);
            var param = *(nint*)(message + 8);
            if (param == 0) return got;
            if (!seen) { seen = true; Log.Info("party", "Steam invites: watching Steam's callbacks"); }
            switch (id)
            {
                case GameLobbyJoinRequested: // { CSteamID lobby; CSteamID friend; }
                    Raise(new SteamId(*(ulong*)param));
                    break;
                case GameRichPresenceJoinRequested: // { CSteamID friend; char connect[256]; }
                    if (ParseLobby(Marshal.PtrToStringUTF8(param + 8, 256)) is { IsValid: true } lobby) Raise(lobby);
                    break;
            }
        }
        catch (Exception e)
        {
            Log.Error("party: Steam invite", e);
        }
        return got;
    }

    static void Raise(SteamId lobby)
    {
        Log.Info("party", $"Steam asked to join lobby {lobby}");
        try { Requested?.Invoke(lobby); }
        catch (Exception e) { Log.Error("party: Steam invite", e); }
    }

    /// <summary>The lobby in "+connect_lobby 1234" (from a command line or a rich presence connect string).</summary>
    internal static SteamId ParseLobby(string? text)
    {
        if (string.IsNullOrEmpty(text)) return SteamId.None;
        var parts = text.Replace('\0', ' ').Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var at = Array.IndexOf(parts, "+connect_lobby");
        return at >= 0 && at + 1 < parts.Length && SteamId.TryParse(parts[at + 1], out var id) ? id : SteamId.None;
    }

    [DllImport("kernel32", CharSet = CharSet.Unicode)]
    static extern nint GetCommandLineW();

    static SteamId CommandLineLobby()
    {
        try { return ParseLobby(OperatingSystem.IsWindows() ? Marshal.PtrToStringUni(GetCommandLineW()) : string.Join(' ', Environment.GetCommandLineArgs())); }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) { return SteamId.None; }
    }
}
