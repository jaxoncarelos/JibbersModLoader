using System.Runtime.InteropServices;
using System.Text;

namespace Jibbers.Sdk.Runtime;

/// <summary>The loader's host table (<c>abi::Host</c> in the native SDK).</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct HostTable
{
    public uint AbiVersion;
    public delegate* unmanaged<delegate* unmanaged<nint, void>, nint, int> RunMain;
    public delegate* unmanaged<nint, nint, int> PreHook;
    public delegate* unmanaged<byte**, nuint*, void> ListPlugins;
    public delegate* unmanaged<uint, byte*, nuint, byte**, nuint*, int> CallPlugin;
    public delegate* unmanaged<byte*, nuint, void> FreeBuf;
}

/// <summary>The bridge's function table (<c>NativeApi</c> in <c>jibbers-dotnet-bridge</c>). Append only.</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NativeApi
{
    public uint Version;
    public HostTable* Host;
    public delegate* unmanaged<byte*, nuint, void> Log;
    public delegate* unmanaged<byte*, nuint, nint, nint, nint, nint, uint> RegisterPlugin;
    public delegate* unmanaged<nint, nint, nint*, nint> DetourCreate;
    public delegate* unmanaged<nint, int> DetourEnable;
    public delegate* unmanaged<nint, int> DetourDisable;
    public delegate* unmanaged<nint, void> DetourDestroy;
    public delegate* unmanaged<byte*, nuint, nuint> LastError;
    public byte* GameDir;
    public byte* ModsDir;
    public byte* GameBuild;
}

/// <summary>Access to the native side. Internal: mods use the public wrappers.</summary>
internal static unsafe class Native
{
    public const uint ApiVersion = 1;
    public static NativeApi* Api;

    public static bool Ready => Api != null;

    public static string LastError()
    {
        if (Api == null)
        {
            return "SDK not started";
        }
        var buf = new byte[1024];
        nuint n;
        fixed (byte* b = buf)
        {
            n = Api->LastError(b, (nuint)buf.Length);
        }
        return Encoding.UTF8.GetString(buf, 0, (int)Math.Min(n, (nuint)buf.Length));
    }

    public static string Utf8(byte* p) => p == null ? "" : Marshal.PtrToStringUTF8((nint)p) ?? "";
}
