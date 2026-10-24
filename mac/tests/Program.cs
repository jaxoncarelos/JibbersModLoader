using Jibbers.Sdk;
using Jibbers.Sdk.Runtime;
using System.Runtime.InteropServices;
static void Equal<T>(T expected,T actual) {if(!Equals(expected,actual))throw new Exception($"Expected {expected}, got {actual}");}
Equal<int?>(97, PlatformInput.UnityKey((int)Key.A));
Equal<int?>(291, PlatformInput.UnityKey((int)Key.F10));
Equal<int?>(304, PlatformInput.UnityKey((int)Key.LeftShift));
Equal<int?>(303, PlatformInput.UnityKey((int)Key.RightShift));
Equal<int?>(306, PlatformInput.UnityKey((int)Key.Control));
Equal<int?>(null, PlatformInput.UnityKey(0));
Equal<int?>(null, PlatformInput.UnityKey(0xFFFF));
Equal(48, Marshal.SizeOf<HostTable>());
Equal(96, Marshal.SizeOf<NativeApi>());
Equal((nint)8, Marshal.OffsetOf<HostTable>(nameof(HostTable.RunMain)));
Equal((nint)8, Marshal.OffsetOf<NativeApi>(nameof(NativeApi.Host)));
Equal((nint)72, Marshal.OffsetOf<NativeApi>(nameof(NativeApi.GameDir)));
Console.WriteLine("Mac host tests: function-key/modifier mappings and 64-bit native ABI layouts passed.");
