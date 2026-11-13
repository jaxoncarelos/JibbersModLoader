using System.Runtime.InteropServices;
using System.Text;

namespace HankModPack;

/// <summary>
/// Whether a PlayStation controller (Sony, USB id 054C: DualShock 4, DualSense) is plugged in, read from the list of the computer's input devices.
/// Steam Input shows such a pad to the game as an Xbox 360 one, so its name cannot tell: the real device is looked for instead.
/// </summary>
static class SonyPad
{
    [StructLayout(LayoutKind.Sequential)]
    struct RawDevice { public nint Handle; public uint Type; }

    [DllImport("user32.dll")] static extern uint GetRawInputDeviceList([Out] RawDevice[]? list, ref uint count, uint size);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetRawInputDeviceInfoW")] static extern uint GetRawInputDeviceInfo(nint device, uint command, StringBuilder? data, ref uint size);

    const uint DeviceName = 0x20000007, TypeHid = 2;

    /// <summary>The names of the Sony devices found (empty when none, or when the list cannot be read).</summary>
    public static List<string> Find()
    {
        var found = new List<string>();
        if (!OperatingSystem.IsWindows()) return found;
        try
        {
            uint count = 0, size = (uint)Marshal.SizeOf<RawDevice>();
            if (GetRawInputDeviceList(null, ref count, size) == uint.MaxValue || count == 0) return found;
            var list = new RawDevice[count];
            if (GetRawInputDeviceList(list, ref count, size) == uint.MaxValue) return found;
            for (var i = 0; i < count; i++)
            {
                if (list[i].Type != TypeHid) continue;
                uint len = 0;
                GetRawInputDeviceInfo(list[i].Handle, DeviceName, null, ref len);
                if (len == 0 || len > 1024) continue;
                var sb = new StringBuilder((int)len);
                if (GetRawInputDeviceInfo(list[i].Handle, DeviceName, sb, ref len) == uint.MaxValue) continue;
                var name = sb.ToString();
                if (name.Contains("VID_054C", StringComparison.OrdinalIgnoreCase)) found.Add(name);
            }
        }
        catch { }
        return found;
    }
}
