using System.Runtime.InteropServices;

namespace Jibbers.Sdk;

/// <summary>Entry point called by the native bridge (<c>jibbers_dotnet.dll</c>). Not for mods.</summary>
public static unsafe class Bootstrap
{
    /// <summary>Game folder (where <c>Jibbers.exe</c> is).</summary>
    public static string GameDir { get; private set; } = "";
    /// <summary><c>&lt;game&gt;\JibbersMods</c>.</summary>
    public static string ModsDir { get; private set; } = "";
    /// <summary>Fingerprint of the running game build.</summary>
    public static string GameBuild { get; private set; } = "";

    /// <summary>
    /// Called once from the bridge's <c>jh_core_init</c> with its <c>NativeApi</c> table: loads the
    /// IL2CPP API, then every C# mod. Returns 0 on success.
    /// </summary>
    [UnmanagedCallersOnly]
    public static int Init(nint api)
    {
        try
        {
            var a = (Runtime.NativeApi*)api;
            if (a == null || a->Version < Runtime.Native.ApiVersion)
            {
                return 2;
            }
            Runtime.Native.Api = a;
            GameDir = Runtime.Native.Utf8(a->GameDir);
            ModsDir = Runtime.Native.Utf8(a->ModsDir);
            GameBuild = Runtime.Native.Utf8(a->GameBuild);
            Interop.Il2CppApi.Load();
            AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Info($"dotnet: unhandled {e.ExceptionObject}");
            Log.Info($"dotnet: .NET {Environment.Version} running, SDK {typeof(Bootstrap).Assembly.GetName().Version?.ToString(3)}");
            ModHost.LoadAll(ModsDir);
            return 0;
        }
        catch (Exception e)
        {
            try
            {
                Log.Error("dotnet: init", e);
            }
            catch
            {
                // nothing left to report with
            }
            return 1;
        }
    }
}
