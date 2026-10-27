using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Rumble;

/// <summary>
/// PlayStation pads (DualShock 4, DualSense, DualSense Edge) plugged in by USB, driven directly with
/// their HID output reports, for when neither XInput (Steam Input) nor the game's input system can
/// reach their motors. Only the rumble fields are marked valid, so lights and triggers are left alone.
/// Bluetooth pads are skipped: writing them switches the pad's report mode under the game's input.
/// Writes happen on a background thread, so a slow device never stalls a frame.
/// </summary>
internal sealed unsafe class SonyPads : IDisposable
{
    const ushort Sony = 0x054C;
    static readonly ushort[] DualShock4 = { 0x05C4, 0x09CC, 0x0BA0 };
    static readonly ushort[] DualSense = { 0x0CE6, 0x0DF2 };

    readonly ConcurrentQueue<string> messages = new();
    readonly HashSet<string> reported = new();
    readonly AutoResetEvent wake = new(false);
    readonly Thread thread;
    readonly List<Pad> pads = new();
    volatile bool active, stopping;
    Motors wanted;
    readonly object gate = new();

    sealed record Pad(SafeFileHandle Handle, bool IsDualSense, int ReportLength, string Name);

    public SonyPads()
    {
        thread = new Thread(Run) { IsBackground = true, Name = "Rumble PlayStation pads" };
        thread.Start();
    }

    /// <summary>Whether the pads are driven here (nothing else reaches them).</summary>
    public bool Active
    {
        set
        {
            active = value;
            wake.Set();
        }
    }

    public void Set(Motors motors)
    {
        lock (gate)
            wanted = motors;
        wake.Set();
    }

    /// <summary>What the background thread has to say, for the log (read on the main thread).</summary>
    public IEnumerable<string> Messages()
    {
        while (messages.TryDequeue(out var message))
            yield return message;
    }

    void Run()
    {
        var sent = new Motors(-1, -1);
        var scanAt = 0L;
        var refreshAt = 0L;
        while (!stopping)
        {
            wake.WaitOne(500);
            try
            {
                Motors motors;
                lock (gate)
                    motors = active && !stopping ? wanted : Motors.Off;
                var now = Environment.TickCount64;
                if (active && now >= scanAt)
                {
                    scanAt = now + 3000;
                    Scan();
                    sent = new(-1, -1);
                }
                if (pads.Count == 0 || (motors == sent && now < refreshAt))
                    continue;
                // Refresh a held level now and then; send zero once when going inactive.
                refreshAt = now + 1000;
                foreach (var pad in pads.ToArray())
                    if (!Write(pad, motors))
                    {
                        pad.Handle.Dispose();
                        pads.Remove(pad);
                    }
                sent = motors;
                if (!active && motors == Motors.Off)
                    Close();
            }
            catch (Exception exception)
            {
                messages.Enqueue("Rumble: PlayStation pads by USB: " + exception.Message);
            }
        }
        foreach (var pad in pads)
            Write(pad, Motors.Off);
        Close();
    }

    static bool Write(Pad pad, Motors motors)
    {
        var report = Report(pad.IsDualSense, pad.ReportLength, motors);
        fixed (byte* bytes = report)
            return WriteFile(pad.Handle, bytes, report.Length, out _, 0);
    }

    /// <summary>The USB output report that sets the motors and nothing else. Unit tested.</summary>
    internal static byte[] Report(bool isDualSense, int length, Motors motors)
    {
        var report = new byte[length];
        var (strong, weak) = ((byte)(Math.Clamp(motors.Low, 0, 1) * 255), (byte)(Math.Clamp(motors.High, 0, 1) * 255));
        if (isDualSense)
        {
            // Report 0x02: valid flags (rumble emulation, rumble rather than haptics), lights untouched, right then left motor.
            report[0] = 0x02;
            report[1] = 0x03;
            report[3] = weak;
            report[4] = strong;
        }
        else
        {
            // Report 0x05: valid flags (motors only), right then left motor.
            report[0] = 0x05;
            report[1] = 0x01;
            report[4] = weak;
            report[5] = strong;
        }
        return report;
    }

    void Scan()
    {
        Close();
        HidD_GetHidGuid(out var hidGuid);
        var set = SetupDiGetClassDevsW(ref hidGuid, 0, 0, 0x12);
        if (set == -1)
            return;
        try
        {
            var data = new DeviceInterfaceData { Size = sizeof(DeviceInterfaceData) };
            for (var index = 0; SetupDiEnumDeviceInterfaces(set, 0, ref hidGuid, index, ref data); index++)
            {
                SetupDiGetDeviceInterfaceDetailW(set, ref data, 0, 0, out var required, 0);
                if (required <= 4)
                    continue;
                var detail = Marshal.AllocHGlobal(required);
                try
                {
                    *(int*)detail = 8;
                    if (!SetupDiGetDeviceInterfaceDetailW(set, ref data, detail, required, out _, 0))
                        continue;
                    if (Open(Marshal.PtrToStringUni(detail + 4)!) is { } pad)
                        pads.Add(pad);
                }
                finally
                {
                    Marshal.FreeHGlobal(detail);
                }
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(set);
        }
    }

    Pad? Open(string path)
    {
        // No access needed to read the IDs; write access only for a Sony pad.
        using (var probe = CreateFileW(path, 0, 3, 0, 3, 0, 0))
        {
            if (probe.IsInvalid)
                return null;
            var attributes = new HiddAttributes { Size = sizeof(HiddAttributes) };
            if (!HidD_GetAttributes(probe, ref attributes) || attributes.VendorId != Sony)
                return null;
            if (!DualShock4.Contains(attributes.ProductId) && !DualSense.Contains(attributes.ProductId))
                return null;
        }
        var handle = CreateFileW(path, 0x40000000, 3, 0, 3, 0, 0);
        if (handle.IsInvalid)
            return null;
        var keep = false;
        try
        {
            var attributes = new HiddAttributes { Size = sizeof(HiddAttributes) };
            if (!HidD_GetAttributes(handle, ref attributes) || !HidD_GetPreparsedData(handle, out var preparsed))
                return null;
            HidpCaps caps;
            try
            {
                if (HidP_GetCaps(preparsed, out caps) != 0x00110000)
                    return null;
            }
            finally
            {
                HidD_FreePreparsedData(preparsed);
            }
            var dualSense = DualSense.Contains(attributes.ProductId);
            var name = dualSense ? "DualSense" : "DualShock 4";
            // By USB the output report is 32 (DualShock 4) or 48-64 (DualSense) bytes; by Bluetooth it's 78 or more.
            var length = caps.OutputReportByteLength;
            var usb = dualSense ? length is >= 48 and < 78 : length == 32;
            if (!usb)
            {
                if (reported.Add(path + "bt"))
                    messages.Enqueue($"Rumble: a {name} by Bluetooth can't be driven directly; turn on Steam Input for it, or plug it in by USB.");
                return null;
            }
            if (reported.Add(path))
                messages.Enqueue($"Rumble: {name} by USB vibrates.");
            keep = true;
            return new Pad(handle, dualSense, length, name);
        }
        finally
        {
            if (!keep)
                handle.Dispose();
        }
    }

    void Close()
    {
        foreach (var pad in pads)
            pad.Handle.Dispose();
        pads.Clear();
    }

    public void Dispose()
    {
        stopping = true;
        wake.Set();
        thread.Join(1000);
    }

    [StructLayout(LayoutKind.Sequential)]
    struct DeviceInterfaceData
    {
        public int Size;
        public Guid InterfaceClassGuid;
        public int Flags;
        public nint Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct HiddAttributes
    {
        public int Size;
        public ushort VendorId, ProductId, VersionNumber;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct HidpCaps
    {
        public ushort Usage, UsagePage, InputReportByteLength, OutputReportByteLength, FeatureReportByteLength;
        public fixed ushort Rest[27];
    }

    [DllImport("hid.dll")]
    static extern void HidD_GetHidGuid(out Guid guid);

    [DllImport("hid.dll")]
    static extern bool HidD_GetAttributes(SafeFileHandle device, ref HiddAttributes attributes);

    [DllImport("hid.dll")]
    static extern bool HidD_GetPreparsedData(SafeFileHandle device, out nint data);

    [DllImport("hid.dll")]
    static extern bool HidD_FreePreparsedData(nint data);

    [DllImport("hid.dll")]
    static extern int HidP_GetCaps(nint data, out HidpCaps caps);

    [DllImport("setupapi.dll", SetLastError = true)]
    static extern nint SetupDiGetClassDevsW(ref Guid classGuid, nint enumerator, nint parent, int flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    static extern bool SetupDiEnumDeviceInterfaces(nint set, nint deviceInfo, ref Guid classGuid, int index, ref DeviceInterfaceData data);

    [DllImport("setupapi.dll", SetLastError = true)]
    static extern bool SetupDiGetDeviceInterfaceDetailW(nint set, ref DeviceInterfaceData data, nint detail, int size, out int required, nint deviceInfo);

    [DllImport("setupapi.dll")]
    static extern bool SetupDiDestroyDeviceInfoList(nint set);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern SafeFileHandle CreateFileW(string path, uint access, uint share, nint security, uint creation, uint flags, nint template);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool WriteFile(SafeFileHandle file, byte* buffer, int length, out int written, nint overlapped);
}
