using Jibbers.Sdk;
using Jibbers.Sdk.Runtime;
using Jibbers.Sdk.Symbols;
using Jibbers.Mac;
using System.Text.Json;
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

using var schema=JsonDocument.Parse("""{"fields":[{"name":"speed","type":"System.Single","static":false}],"methods":["Update()->System.Void"]}""");
var actual=new ClassInfo { FullName="Example" };
actual.Fields.Add(new FieldInfo{Name="speed",Type="System.Single",Static=false});
actual.Methods.Add(new MethodInfo{Name="Update",Return="System.Void"});
Host.ValidateClassSchema(actual,schema.RootElement);
actual.Fields[0].Type="System.Double";
var rejected=false;
try { Host.ValidateClassSchema(actual,schema.RootElement); } catch(InvalidOperationException) { rejected=true; }
if(!rejected)throw new Exception("Changed field type was accepted");
actual.Fields[0].Type="System.Single";
actual.Methods.Clear();rejected=false;
try { Host.ValidateClassSchema(actual,schema.RootElement); } catch(InvalidOperationException) { rejected=true; }
if(!rejected)throw new Exception("Missing method was accepted");
Console.WriteLine("Mac schema guard: valid members accepted; changed field type and missing method rejected before hooks.");
