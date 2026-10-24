using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Jibbers.Sdk;
using Jibbers.Sdk.Interop;
using Jibbers.Sdk.Runtime;
using Jibbers.Sdk.Symbols;

namespace Doorstop
{
    public static class Entrypoint
    {
        // Doorstop invokes this before Unity finishes starting. Do not touch game objects here.
        public static void Start() => Jibbers.Mac.Host.Start();
    }
}

namespace Jibbers.Mac
{
internal static unsafe class Host
{
    static string Version => typeof(Host).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false).OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion ?? "mac-preview";
    static readonly string Root = Environment.GetEnvironmentVariable("JIBBERS_GAME_ROOT") ?? AppContext.BaseDirectory;
    static readonly string Mods = Path.Combine(Root, "JibbersMods");
    static readonly string App = Environment.GetEnvironmentVariable("JIBBERS_GAME_APP") ?? "";
    static readonly object LogLock = new();
    static int started, mainThread;
    static bool inputFailed;
    static long lastInput;
    static NativeApi* api;
    static HostTable* table;
    static readonly ConcurrentQueue<Job> jobs = new();
    static readonly List<Registration> plugins = new();
    static readonly object PluginLock = new();
    [ThreadStatic] static string? lastError;
    sealed class Job
    {
        internal nint Function, Context;
        internal int State; // pending=0, started=1, cancelled=2
        internal readonly ManualResetEventSlim Done = new();
    }
    sealed record Registration(uint Id, string File, nint Context, nint Handle, nint Free, nint Unload);
    sealed class HookSlot
    {
        internal Il2CppMethod Method;
        internal nint Original, Handle;
        internal readonly List<nint> Callbacks = new();
    }
    static readonly HookSlot?[] hooks = new HookSlot[12];
    static readonly object HookLock = new();
    static readonly nint[] frameOriginal = new nint[2];
    static readonly List<nint> frameHandles = new();

    [DllImport("jibbers_macos_hooks", EntryPoint="jh_detour_create")] static extern nint CreateNative(nint target, nint replacement, nint* original);
    [DllImport("jibbers_macos_hooks", EntryPoint="jh_detour_enable")] static extern int EnableNative(nint handle);
    [DllImport("jibbers_macos_hooks", EntryPoint="jh_detour_disable")] static extern int DisableNative(nint handle);
    [DllImport("jibbers_macos_hooks", EntryPoint="jh_detour_destroy")] static extern void DestroyNative(nint handle);
    [DllImport("jibbers_macos_ui", EntryPoint="jibbers_macos_ui_init")] static extern int InitMenu(HostTable* host, [MarshalAs(UnmanagedType.LPUTF8Str)] string root);

    internal static void Start()
    {
        if (Interlocked.Exchange(ref started, 1) != 0) return;
        Directory.CreateDirectory(Mods);
        Write($"Native macOS loader {Version}; {RuntimeInformation.OSDescription}; {RuntimeInformation.ProcessArchitecture}; .NET {Environment.Version}");
        NativeLibrary.SetDllImportResolver(typeof(Host).Assembly, (name, _, _) => name switch
        {
            "jibbers_macos_hooks" => NativeLibrary.Load(Path.Combine(Mods, "mac/libjibbers_hooks.dylib")),
            "jibbers_macos_ui" => NativeLibrary.Load(Path.Combine(Mods, "mac/libjibbers_macos_ui.dylib")),
            _ => 0
        });
        new Thread(Initialize) { IsBackground = true, Name = "Jibbers Mac startup" }.Start();
    }
    static void Write(string text)
    {
        lock (LogLock)
            File.AppendAllText(Path.Combine(Root, "jibbers_hotload.log"), $"[{DateTime.UtcNow:O}] [mac] {text}\n");
    }
    static byte* Utf8(string value) => (byte*)Marshal.StringToCoTaskMemUTF8(value);
    static void CheckBuild()
    {
        if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException("This package is for native macOS.");
        if (RuntimeInformation.ProcessArchitecture != Architecture.Arm64)
            throw new PlatformNotSupportedException("This first native preview supports Apple Silicon (arm64) only.");
        var metadata = Path.Combine(App, "Contents/Resources/Data/il2cpp_data/Metadata/global-metadata.dat");
        if (!File.Exists(metadata)) throw new InvalidOperationException("No IL2CPP metadata in this app. Mono builds are not supported by this preview.");
        var digest = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(metadata))).ToLowerInvariant();
        using var stream = typeof(Host).Assembly.GetManifestResourceStream("Jibbers.Mac.expected-build.json")!;
        using var expected = JsonDocument.Parse(stream);
        Write("Game metadata SHA256: " + digest);
        var bytes = File.ReadAllBytes(metadata);
        if (bytes.Length < 8 || BitConverter.ToUInt32(bytes, 0) != 0xFAB11BAF || BitConverter.ToInt32(bytes, 4) != expected.RootElement.GetProperty("metadata_version").GetInt32())
            throw new InvalidOperationException("Unsupported IL2CPP metadata format; no hooks or mods installed.");
        Write(expected.RootElement.GetProperty("metadata_sha256").GetString() == digest
            ? "Metadata file matches reference; checking gameplay member schema."
            : "Platform metadata differs from Windows reference; checking compiled gameplay member schema.");
    }
    static void Initialize()
    {
        try
        {
            CheckBuild();
            Thread.Sleep(8000);
            Il2CppApi.Load();
            var ready = false;
            for (var attempt = 0; attempt < 60; attempt++)
            {
                var domain = Il2CppApi.domain_get();
                if (domain != 0)
                {
                    Il2CppApi.thread_attach(domain);
                    nuint count = 0;
                    Il2CppApi.domain_get_assemblies(domain, &count);
                    if (count > 0) { ready = true; break; }
                }
                Thread.Sleep(500);
            }
            if (!ready) throw new TimeoutException("IL2CPP did not become ready.");
            table = (HostTable*)NativeMemory.AllocZeroed((nuint)sizeof(HostTable));
            *table = new HostTable { AbiVersion = 4, RunMain = &RunMain, PreHook = &PreHook, ListPlugins = &List, CallPlugin = &Call, FreeBuf = &Free };
            api = (NativeApi*)NativeMemory.AllocZeroed((nuint)sizeof(NativeApi));
            *api = new NativeApi { Version = 1, Host = table, Log = &LogLine, RegisterPlugin = &Register,
                DetourCreate = &Create, DetourEnable = &Enable, DetourDisable = &Disable, DetourDestroy = &Destroy, LastError = &LastError,
                GameDir = Utf8(Root), ModsDir = Utf8(Mods), GameBuild = Utf8("macOS native preview; exact metadata baseline") };
            if (Bootstrap.Initialize((nint)api, false) != 0) throw new InvalidOperationException("SDK initialization failed.");
            // Resolve game symbols before any game hook. Ambiguous/missing shapes are a hard stop.
            var report = SymbolTable.Report();
            File.WriteAllText(Path.Combine(Mods, "mac-symbols.json"), report.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            if (report["failed"]!.GetValue<int>() != 0) throw new InvalidOperationException("Game symbol validation failed. See JibbersMods/mac-symbols.json; no mods loaded.");
            ValidateGameplaySchema();
            InstallPump();
            MainThread.Run(() =>
            {
                ModHost.LoadAll(Mods);
                var result = InitMenu(table, Root);
                if (result != 0) throw new InvalidOperationException("Native Mods menu initialization failed: " + result);
            });
            Write("READY: native Mac mods loaded; Options > Mods; F9 drone, F10 weather. No Windows catalog is used.");
        }
        catch (Exception e) { Write("STOPPED: " + e); }
    }
    internal static void ValidateClassSchema(ClassInfo actual, JsonElement expected)
    {
        foreach (var field in expected.GetProperty("fields").EnumerateArray())
        {
            var name = field.GetProperty("name").GetString();
            var type = field.GetProperty("type").GetString();
            var isStatic = field.GetProperty("static").GetBoolean();
            if (!actual.Fields.Any(f => f.Name == name && f.Type == type && f.Static == isStatic))
                throw new InvalidOperationException($"Game schema mismatch: {actual.FullName}.{name}: {type}. No hooks installed.");
        }
        var methods = actual.Methods.Select(m => (m.Name+"("+string.Join(",",m.Params)+")->"+m.Return).Replace(" ", "")).ToHashSet();
        foreach (var method in expected.GetProperty("methods").EnumerateArray())
            if (!methods.Contains(method.GetString()!))
                throw new InvalidOperationException($"Game schema mismatch: {actual.FullName} {method.GetString()}. No hooks installed.");
    }
    static void ValidateGameplaySchema()
    {
        using var stream = typeof(Host).Assembly.GetManifestResourceStream("Jibbers.Mac.expected-schema.json")!;
        using var schema = JsonDocument.Parse(stream);
        using var catalogue = JsonDocument.Parse(SymbolTable.BuiltInJson);
        var checkedClasses = new HashSet<string>();
        foreach (var symbol in catalogue.RootElement.GetProperty("symbols").EnumerateArray())
        {
            var info = SymbolTable.Info(symbol.GetProperty("id").GetString()!);
            if (info.Image != "Assembly-CSharp.dll" || !checkedClasses.Add(info.FullName)) continue;
            if (!schema.RootElement.TryGetProperty(info.FullName, out var expected))
                throw new InvalidOperationException("No compiled gameplay schema for " + info.FullName + "; no hooks installed.");
            ValidateClassSchema(info, expected);
        }
        if (checkedClasses.Count < 10) throw new InvalidOperationException("Incomplete gameplay schema validation; no hooks installed.");
        Write("Gameplay schema validated before hooks: " + checkedClasses.Count + " resolved classes, declared fields and method signatures.");
    }
    static void InstallPump()
    {
        var targets = new[] { ("UnityEngine.UI.dll", "UnityEngine.EventSystems.EventSystem", "Update"), ("Mujoco.Runtime.dll", "Mujoco.MjScene", "LateUpdate") };
        nint[] replacements = { (nint)(delegate* unmanaged<nint, nint, void>)&Frame0, (nint)(delegate* unmanaged<nint, nint, void>)&Frame1 };
        for (var i = 0; i < targets.Length; i++)
        {
            var t = targets[i];
            var cls = Il2Cpp.RequireClass(t.Item1, t.Item2);
            if (cls.IsNull) continue;
            var method = cls.Method(t.Item3, 0);
            if (method.IsNull || method.IsStatic || method.ReturnType != "System.Void") continue;
            nint original;
            var handle = CreateNative(method.NativeCode, replacements[i], &original);
            if (handle == 0) throw new InvalidOperationException("Could not create frame hook " + t.Item2);
            frameOriginal[i] = original;
            if (EnableNative(handle) != 0) throw new InvalidOperationException("Could not enable frame hook " + t.Item2);
            frameHandles.Add(handle);
        }
        if (frameHandles.Count == 0) throw new InvalidOperationException("No compatible Unity frame pump target found.");
    }
    static void Frame(int index, nint self, nint method)
    {
        try
        {
            mainThread = Environment.CurrentManagedThreadId;
            MainThread.Enter(() =>
            {
                if (!inputFailed && Environment.TickCount64 - lastInput >= 8)
                {
                    try { PlatformInput.Sample(); lastInput = Environment.TickCount64; }
                    catch (Exception e) { inputFailed = true; Write("Input unavailable: " + e); }
                }
                while (jobs.TryDequeue(out var job))
                {
                    if (Interlocked.CompareExchange(ref job.State, 1, 0) != 0) continue;
                    try { ((delegate* unmanaged<nint, void>)job.Function)(job.Context); }
                    catch (Exception e) { Write("Main-thread job: " + e); }
                    finally { job.Done.Set(); }
                }
                return 0;
            });
        }
        catch (Exception e) { Write("Frame pump: " + e); }
        finally { var original = frameOriginal[index]; if (original != 0) ((delegate* unmanaged<nint, nint, void>)original)(self, method); }
    }
    [UnmanagedCallersOnly] static void Frame0(nint self, nint method) => Frame(0, self, method);
    [UnmanagedCallersOnly] static void Frame1(nint self, nint method) => Frame(1, self, method);

    [UnmanagedCallersOnly] static int RunMain(delegate* unmanaged<nint, void> f, nint context)
    {
        try
        {
            if (Environment.CurrentManagedThreadId == mainThread) { f(context); return 0; }
            var job = new Job { Function = (nint)f, Context = context };
            jobs.Enqueue(job);
            if (!job.Done.Wait(TimeSpan.FromSeconds(15)))
            {
                if (Interlocked.CompareExchange(ref job.State, 2, 0) == 0) return 1;
                job.Done.Wait(); // Started jobs must finish before the caller releases their context.
            }
            return 0;
        }
        catch (Exception e) { lastError = e.Message; return 1; }
    }
    [UnmanagedCallersOnly] static void LogLine(byte* p, nuint len)
    {
        try { Write(Encoding.UTF8.GetString(p, checked((int)len))); } catch { }
    }
    [UnmanagedCallersOnly] static nuint LastError(byte* p, nuint cap)
    {
        try { var bytes = Encoding.UTF8.GetBytes(lastError ?? "Native detour operation failed"); var n = Math.Min(bytes.Length, checked((int)cap)); bytes.AsSpan(0, n).CopyTo(new Span<byte>(p, n)); return (nuint)bytes.Length; }
        catch { return 0; }
    }
    [UnmanagedCallersOnly] static nint Create(nint target, nint replacement, nint* original)
    { try { return CreateNative(target, replacement, original); } catch (Exception e) { lastError = e.Message; return 0; } }
    [UnmanagedCallersOnly] static int Enable(nint handle)
    { try { return EnableNative(handle); } catch (Exception e) { lastError = e.Message; return 1; } }
    [UnmanagedCallersOnly] static int Disable(nint handle)
    { try { return DisableNative(handle); } catch (Exception e) { lastError = e.Message; return 1; } }
    [UnmanagedCallersOnly] static void Destroy(nint handle)
    { try { DestroyNative(handle); } catch (Exception e) { lastError = e.Message; } }

    [UnmanagedCallersOnly] static uint Register(byte* name, nuint length, nint context, nint handle, nint free, nint unload)
    {
        try { lock (PluginLock) { var id = (uint)plugins.Count + 1; plugins.Add(new(id, Encoding.UTF8.GetString(name, checked((int)length)), context, handle, free, unload)); return id; } }
        catch (Exception e) { lastError = e.Message; return 0; }
    }
    [UnmanagedCallersOnly] static void List(byte** output, nuint* length)
    {
        try { byte[] bytes; lock (PluginLock) bytes = JsonSerializer.SerializeToUtf8Bytes(plugins.Select(p => new { id = p.Id, file = p.File, developer = false })); Reply(bytes, output, length); }
        catch { *output = null; *length = 0; }
    }
    static void Reply(byte[] bytes, byte** output, nuint* length)
    {
        *output = (byte*)NativeMemory.Alloc((nuint)Math.Max(1, bytes.Length));
        bytes.CopyTo(new Span<byte>(*output, bytes.Length)); *length = (nuint)bytes.Length;
    }
    [UnmanagedCallersOnly] static int Call(uint id, byte* request, nuint size, byte** output, nuint* length)
    {
        *output = null; *length = 0;
        try
        {
            Registration? p; lock (PluginLock) p = plugins.FirstOrDefault(p => p.Id == id);
            if (p is null) return 1;
            byte* response = null; nuint responseLength = 0;
            ((delegate* unmanaged<nint, byte*, nuint, byte**, nuint*, void>)p.Handle)(p.Context, request, size, &response, &responseLength);
            try { Reply(new ReadOnlySpan<byte>(response, checked((int)responseLength)).ToArray(), output, length); }
            finally { ((delegate* unmanaged<nint, byte*, nuint, void>)p.Free)(p.Context, response, responseLength); }
            return 0;
        }
        catch (Exception e) { lastError = e.Message; return 1; }
    }
    [UnmanagedCallersOnly] static void Free(byte* pointer, nuint _) => NativeMemory.Free(pointer);

    [UnmanagedCallersOnly] static int PreHook(nint pointer, nint callback)
    {
        try
        {
            var method = new Il2CppMethod(pointer);
            if (Hooks.Unsupported(method) is not null) return 6;
            for (uint i = 0; i < method.ParameterCount; i++)
            {
                var type = Il2CppApi.method_get_param(pointer, i);
                var cls = new Il2CppClass(Il2CppApi.class_from_type(type));
                if (!cls.IsNull && cls.IsValueType && !cls.IsEnum && method.ParameterTypes[i] is not ("System.Boolean" or "System.Byte" or "System.SByte" or "System.Int16" or "System.UInt16" or "System.Int32" or "System.UInt32" or "System.Int64" or "System.UInt64" or "System.IntPtr" or "System.UIntPtr")) return 6;
            }
            lock (HookLock)
            {
                for (var i = 0; i < hooks.Length; i++)
                    if (hooks[i]?.Method.Ptr == pointer) { hooks[i]!.Callbacks.Add(callback); return 0; }
                var slot = Array.FindIndex(hooks, h => h is null);
                if (slot < 0) return 2;
                var h = new HookSlot { Method = method }; h.Callbacks.Add(callback); hooks[slot] = h;
                nint original;
                h.Handle = CreateNative(method.NativeCode, method.ReturnType == "System.Boolean" ? BoolThunks[slot] : VoidThunks[slot], &original);
                if (h.Handle == 0) { hooks[slot] = null; return 4; }
                h.Original = original;
                return EnableNative(h.Handle);
            }
        }
        catch (Exception e) { lastError = e.Message; return 4; }
    }
    static void Before(int index, nint self, nint argument)
    {
        try
        {
            nint[] callbacks; lock (HookLock) callbacks = hooks[index]!.Callbacks.ToArray();
            foreach (var callback in callbacks) ((delegate* unmanaged<nint, nint, void>)callback)(self, argument);
        }
        catch (Exception e) { Write("Prehook callback: " + e); }
    }
    [UnmanagedCallersOnly] static void V0(nint a, nint b, nint c, nint d) { Before(0,a,b); ((delegate* unmanaged<nint,nint,nint,nint,void>)hooks[0]!.Original)(a,b,c,d); }
    [UnmanagedCallersOnly] static byte B0(nint a, nint b, nint c, nint d) { Before(0,a,b); return ((delegate* unmanaged<nint,nint,nint,nint,byte>)hooks[0]!.Original)(a,b,c,d); }
    [UnmanagedCallersOnly] static void V1(nint a, nint b, nint c, nint d) { Before(1,a,b); ((delegate* unmanaged<nint,nint,nint,nint,void>)hooks[1]!.Original)(a,b,c,d); }
    [UnmanagedCallersOnly] static byte B1(nint a, nint b, nint c, nint d) { Before(1,a,b); return ((delegate* unmanaged<nint,nint,nint,nint,byte>)hooks[1]!.Original)(a,b,c,d); }
    [UnmanagedCallersOnly] static void V2(nint a, nint b, nint c, nint d) { Before(2,a,b); ((delegate* unmanaged<nint,nint,nint,nint,void>)hooks[2]!.Original)(a,b,c,d); }
    [UnmanagedCallersOnly] static byte B2(nint a, nint b, nint c, nint d) { Before(2,a,b); return ((delegate* unmanaged<nint,nint,nint,nint,byte>)hooks[2]!.Original)(a,b,c,d); }
    [UnmanagedCallersOnly] static void V3(nint a, nint b, nint c, nint d) { Before(3,a,b); ((delegate* unmanaged<nint,nint,nint,nint,void>)hooks[3]!.Original)(a,b,c,d); }
    [UnmanagedCallersOnly] static byte B3(nint a, nint b, nint c, nint d) { Before(3,a,b); return ((delegate* unmanaged<nint,nint,nint,nint,byte>)hooks[3]!.Original)(a,b,c,d); }
    [UnmanagedCallersOnly] static void V4(nint a, nint b, nint c, nint d) { Before(4,a,b); ((delegate* unmanaged<nint,nint,nint,nint,void>)hooks[4]!.Original)(a,b,c,d); }
    [UnmanagedCallersOnly] static byte B4(nint a, nint b, nint c, nint d) { Before(4,a,b); return ((delegate* unmanaged<nint,nint,nint,nint,byte>)hooks[4]!.Original)(a,b,c,d); }
    [UnmanagedCallersOnly] static void V5(nint a, nint b, nint c, nint d) { Before(5,a,b); ((delegate* unmanaged<nint,nint,nint,nint,void>)hooks[5]!.Original)(a,b,c,d); }
    [UnmanagedCallersOnly] static byte B5(nint a, nint b, nint c, nint d) { Before(5,a,b); return ((delegate* unmanaged<nint,nint,nint,nint,byte>)hooks[5]!.Original)(a,b,c,d); }
    [UnmanagedCallersOnly] static void V6(nint a, nint b, nint c, nint d) { Before(6,a,b); ((delegate* unmanaged<nint,nint,nint,nint,void>)hooks[6]!.Original)(a,b,c,d); }
    [UnmanagedCallersOnly] static byte B6(nint a, nint b, nint c, nint d) { Before(6,a,b); return ((delegate* unmanaged<nint,nint,nint,nint,byte>)hooks[6]!.Original)(a,b,c,d); }
    [UnmanagedCallersOnly] static void V7(nint a, nint b, nint c, nint d) { Before(7,a,b); ((delegate* unmanaged<nint,nint,nint,nint,void>)hooks[7]!.Original)(a,b,c,d); }
    [UnmanagedCallersOnly] static byte B7(nint a, nint b, nint c, nint d) { Before(7,a,b); return ((delegate* unmanaged<nint,nint,nint,nint,byte>)hooks[7]!.Original)(a,b,c,d); }
    [UnmanagedCallersOnly] static void V8(nint a, nint b, nint c, nint d) { Before(8,a,b); ((delegate* unmanaged<nint,nint,nint,nint,void>)hooks[8]!.Original)(a,b,c,d); }
    [UnmanagedCallersOnly] static byte B8(nint a, nint b, nint c, nint d) { Before(8,a,b); return ((delegate* unmanaged<nint,nint,nint,nint,byte>)hooks[8]!.Original)(a,b,c,d); }
    [UnmanagedCallersOnly] static void V9(nint a, nint b, nint c, nint d) { Before(9,a,b); ((delegate* unmanaged<nint,nint,nint,nint,void>)hooks[9]!.Original)(a,b,c,d); }
    [UnmanagedCallersOnly] static byte B9(nint a, nint b, nint c, nint d) { Before(9,a,b); return ((delegate* unmanaged<nint,nint,nint,nint,byte>)hooks[9]!.Original)(a,b,c,d); }
    [UnmanagedCallersOnly] static void V10(nint a, nint b, nint c, nint d) { Before(10,a,b); ((delegate* unmanaged<nint,nint,nint,nint,void>)hooks[10]!.Original)(a,b,c,d); }
    [UnmanagedCallersOnly] static byte B10(nint a, nint b, nint c, nint d) { Before(10,a,b); return ((delegate* unmanaged<nint,nint,nint,nint,byte>)hooks[10]!.Original)(a,b,c,d); }
    [UnmanagedCallersOnly] static void V11(nint a, nint b, nint c, nint d) { Before(11,a,b); ((delegate* unmanaged<nint,nint,nint,nint,void>)hooks[11]!.Original)(a,b,c,d); }
    [UnmanagedCallersOnly] static byte B11(nint a, nint b, nint c, nint d) { Before(11,a,b); return ((delegate* unmanaged<nint,nint,nint,nint,byte>)hooks[11]!.Original)(a,b,c,d); }
    static readonly nint[] VoidThunks = { (nint)(delegate* unmanaged<nint,nint,nint,nint,void>)&V0, (nint)(delegate* unmanaged<nint,nint,nint,nint,void>)&V1, (nint)(delegate* unmanaged<nint,nint,nint,nint,void>)&V2, (nint)(delegate* unmanaged<nint,nint,nint,nint,void>)&V3, (nint)(delegate* unmanaged<nint,nint,nint,nint,void>)&V4, (nint)(delegate* unmanaged<nint,nint,nint,nint,void>)&V5, (nint)(delegate* unmanaged<nint,nint,nint,nint,void>)&V6, (nint)(delegate* unmanaged<nint,nint,nint,nint,void>)&V7, (nint)(delegate* unmanaged<nint,nint,nint,nint,void>)&V8, (nint)(delegate* unmanaged<nint,nint,nint,nint,void>)&V9, (nint)(delegate* unmanaged<nint,nint,nint,nint,void>)&V10, (nint)(delegate* unmanaged<nint,nint,nint,nint,void>)&V11 };
    static readonly nint[] BoolThunks = { (nint)(delegate* unmanaged<nint,nint,nint,nint,byte>)&B0, (nint)(delegate* unmanaged<nint,nint,nint,nint,byte>)&B1, (nint)(delegate* unmanaged<nint,nint,nint,nint,byte>)&B2, (nint)(delegate* unmanaged<nint,nint,nint,nint,byte>)&B3, (nint)(delegate* unmanaged<nint,nint,nint,nint,byte>)&B4, (nint)(delegate* unmanaged<nint,nint,nint,nint,byte>)&B5, (nint)(delegate* unmanaged<nint,nint,nint,nint,byte>)&B6, (nint)(delegate* unmanaged<nint,nint,nint,nint,byte>)&B7, (nint)(delegate* unmanaged<nint,nint,nint,nint,byte>)&B8, (nint)(delegate* unmanaged<nint,nint,nint,nint,byte>)&B9, (nint)(delegate* unmanaged<nint,nint,nint,nint,byte>)&B10, (nint)(delegate* unmanaged<nint,nint,nint,nint,byte>)&B11 };
}
}
