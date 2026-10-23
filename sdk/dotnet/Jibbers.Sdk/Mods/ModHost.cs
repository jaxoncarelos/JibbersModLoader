using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Text;
using System.Text.Json.Nodes;

namespace Jibbers.Sdk;

/// <summary>Which mod is running SDK calls right now (for attributing hooks, detours and events).</summary>
internal static class ModContext
{
    static readonly AsyncLocal<string?> current = new();
    static readonly Dictionary<string, JibbersMod> mods = new();

    public static string? CurrentId => current.Value;

    public static void Run(string? owner, Action callback)
    {
        var previous = current.Value;
        current.Value = owner;
        try { callback(); }
        finally { current.Value = previous; }
    }

    public static T With<T>(JibbersMod mod, Func<T> f)
    {
        var was = current.Value;
        current.Value = mod.Id;
        try
        {
            return f();
        }
        finally
        {
            current.Value = was;
        }
    }

    public static void Add(JibbersMod m)
    {
        lock (mods)
        {
            mods[m.Id] = m;
        }
    }

    public static IReadOnlyList<JibbersMod> All()
    {
        lock (mods)
        {
            return mods.Values.ToList();
        }
    }

    /// <summary>Whether callbacks of this mod should run (unknown owners always run).</summary>
    public static bool Enabled(string? id)
    {
        if (id is null)
        {
            return true;
        }
        lock (mods)
        {
            return !mods.TryGetValue(id, out var m) || m.Enabled;
        }
    }
}

/// <summary>Answers the Mods tab's <c>plugin.*</c> protocol and custom commands for one mod.</summary>
internal sealed class ModPlugin
{
    public readonly JibbersMod Mod;

    public ModPlugin(JibbersMod mod) => Mod = mod;

    JsonObject Info() => new()
    {
        ["ok"] = true,
        ["id"] = Mod.Id,
        ["name"] = Mod.Name,
        ["version"] = Mod.Version,
        ["author"] = Mod.Author,
        ["description"] = Mod.Description,
        ["sdk"] = "dotnet " + typeof(ModPlugin).Assembly.GetName().Version?.ToString(3),
        ["enabled"] = Mod.Enabled,
        ["settings"] = Mod.Store!.Schema(),
    };

    JsonObject Set(string key, JsonNode? value)
    {
        var stored = Mod.Store!.Set(key, value);
        PropertySettings.Apply(Mod, key, stored);
        ModContext.With(Mod, () =>
        {
            Mod.OnSettingChanged(key, stored);
            return 0;
        });
        return new JsonObject { ["ok"] = true, ["values"] = Mod.Store.Values() };
    }

    public JsonNode Handle(JsonObject req)
    {
        var cmd = req["cmd"]?.GetValue<string>() ?? "";
        switch (cmd)
        {
            case "plugin.info":
                return Info();
            case "plugin.get":
                return new JsonObject { ["ok"] = true, ["values"] = Mod.Store!.Values() };
            case "plugin.set":
                return Set(req["key"]?.GetValue<string>() ?? throw new ArgumentException("plugin.set needs a key"), req["value"] ?? throw new ArgumentException("plugin.set needs a value"));
            case "plugin.enable":
                var on = req["on"]?.GetValue<bool>() ?? throw new ArgumentException("plugin.enable needs \"on\"");
                Set("enabled", on);
                return new JsonObject { ["ok"] = true, ["enabled"] = on };
        }
        if (cmd.StartsWith("plugin."))
        {
            throw new ArgumentException($"unknown plugin command {cmd}");
        }
        var r = ModContext.With(Mod, () => Mod.OnCommand(cmd, req)) ?? throw new ArgumentException($"{cmd} is not a command of {Mod.Id}");
        if (r is JsonObject o && !o.ContainsKey("ok"))
        {
            o["ok"] = true;
        }
        return r;
    }
}

/// <summary>Loads C# mods, registers them with the loader and routes requests to them.</summary>
internal static unsafe class ModHost
{
    static readonly List<GCHandle> handles = new();

    public static string ModsDir = "";

    sealed class ModLoadContext(string path) : AssemblyLoadContext(Path.GetFileNameWithoutExtension(path), isCollectible: false)
    {
        readonly AssemblyDependencyResolver resolver = new(path);

        protected override Assembly? Load(AssemblyName name)
        {
            // The SDK is shared: every mod sees the same Jibbers.Sdk.
            if (name.Name == "Jibbers.Sdk")
            {
                return typeof(ModHost).Assembly;
            }
            var p = resolver.ResolveAssemblyToPath(name);
            if (p is null)
            {
                var local = Path.Combine(Path.GetDirectoryName(path)!, name.Name + ".dll");
                p = File.Exists(local) ? local : null;
            }
            return p is null ? null : LoadFromAssemblyPath(p);
        }

        protected override nint LoadUnmanagedDll(string unmanagedDllName)
        {
            var p = resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
            return p is null ? 0 : LoadUnmanagedDllFromPath(p);
        }
    }

    static bool ValidId(string id) => id.Length > 0 && id.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '_');

    /// <summary>Mod DLLs: <c>cs\*.dll</c> and <c>cs\&lt;name&gt;\&lt;name&gt;.dll</c>.</summary>
    public static IEnumerable<string> ModFiles(string dir)
    {
        if (!Directory.Exists(dir))
        {
            yield break;
        }
        foreach (var f in Directory.GetFiles(dir, "*.dll").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            yield return f;
        }
        foreach (var sub in Directory.GetDirectories(dir).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            var f = Path.Combine(sub, Path.GetFileName(sub) + ".dll");
            if (File.Exists(f))
            {
                yield return f;
            }
        }
    }

    public static void LoadAll(string modsDir)
    {
        ModsDir = modsDir;
        var dir = Path.Combine(modsDir, "cs");
        Directory.CreateDirectory(dir);
        // Gather first so replacement mods reserve their conflicts before legacy OnLoad runs.
        // Stable sorting retains catalog priority over manual copies of the same mod.
        var candidates=new List<(JibbersMod Mod,string File)>();
        foreach (var file in PublicModPackages.Files(modsDir).Concat(ModFiles(dir)))
        {
            try
            {
                candidates.AddRange(ReadFile(file).Select(mod=>(mod,file)));
            }
            catch (Exception e)
            {
                Log.Error("dotnet: " + Path.GetFileName(file), e);
            }
        }
        foreach(var candidate in ModConflicts.Order(candidates.Select(c=>c.Mod)))
        {
            var file=candidates.First(c=>ReferenceEquals(c.Mod,candidate)).File;
            try { Register(candidate,file); }
            catch(Exception e) { Log.Error("dotnet: "+Path.GetFileName(file),e); }
        }
    }

    static IEnumerable<JibbersMod> ReadFile(string file)
    {
        var alc = new ModLoadContext(file);
        var asm = alc.LoadFromAssemblyPath(file);
        var types = asm.GetTypes().Where(t => typeof(JibbersMod).IsAssignableFrom(t) && !t.IsAbstract).ToList();
        if (types.Count == 0)
        {
            Log.Info($"dotnet: {Path.GetFileName(file)} has no JibbersMod class; skipped");
            yield break;
        }
        foreach (var t in types)
        {
            var mod = (JibbersMod)Activator.CreateInstance(t)!;
            yield return mod;
        }
    }

    /// <summary>Sets up and registers one mod, then runs its OnLoad.</summary>
    internal static void Register(JibbersMod mod, string file)
    {
        if (!ValidId(mod.Id))
        {
            Log.Info($"dotnet: {mod.GetType().Name}: id \"{mod.Id}\" must be lowercase letters, digits and underscores; skipped");
            return;
        }
        if (ModContext.All().Any(m => m.Id == mod.Id))
        {
            Log.Info($"dotnet: duplicate mod id {mod.Id} ({file}); skipped");
            return;
        }
        var conflict = ModConflicts.Find(mod, ModContext.All());
        if (conflict is not null)
        {
            Log.Info($"dotnet: {mod.Id} ({file}) conflicts with active {conflict.Id}; skipped before OnLoad");
            return;
        }
        mod.Store = new SettingsStore(mod.Settings, mod.EnabledToggle ? mod.Name : null, Path.Combine(ModsDir, mod.Id + ".settings.json"));
        PropertySettings.Load(mod);
        ModContext.Add(mod);
        var plugin = new ModPlugin(mod);
        if (Runtime.Native.Api->RegisterPlugin != null)
        {
            var h = GCHandle.Alloc(plugin);
            handles.Add(h);
            var name = Encoding.UTF8.GetBytes(Path.GetFileNameWithoutExtension(file) + (ModContext.All().Count(m => m.GetType().Assembly == mod.GetType().Assembly) > 1 ? "." + mod.Id : ""));
            fixed (byte* n = name)
            {
                Runtime.Native.Api->RegisterPlugin(n, (nuint)name.Length, GCHandle.ToIntPtr(h),
                    (nint)(delegate* unmanaged<nint, byte*, nuint, byte**, nuint*, void>)&HandleRequest,
                    (nint)(delegate* unmanaged<nint, byte*, nuint, void>)&FreeReply,
                    (nint)(delegate* unmanaged<nint, void>)&Unload);
            }
        }
        ModContext.With(mod, () =>
        {
            mod.OnLoad();
            return 0;
        });
        Log.Info($"{mod.Name} {mod.Version} loaded (C#, {Path.GetFileName(file)})");
    }

    static void Reply(JsonNode reply, byte** outp, nuint* outLen)
    {
        var bytes = Encoding.UTF8.GetBytes(reply.ToJsonString());
        var mem = (byte*)NativeMemory.Alloc((nuint)Math.Max(1, bytes.Length));
        bytes.CopyTo(new Span<byte>(mem, bytes.Length));
        *outp = mem;
        *outLen = (nuint)bytes.Length;
    }

    [UnmanagedCallersOnly]
    static void HandleRequest(nint ctx, byte* req, nuint len, byte** outp, nuint* outLen)
    {
        JsonNode reply;
        try
        {
            var plugin = (ModPlugin)GCHandle.FromIntPtr(ctx).Target!;
            var text = req == null ? "" : Encoding.UTF8.GetString(req, (int)len);
            var obj = JsonNode.Parse(text) as JsonObject ?? throw new ArgumentException("request must be a JSON object");
            reply = plugin.Handle(obj);
        }
        catch (Exception e)
        {
            reply = new JsonObject { ["ok"] = false, ["error"] = e is ArgumentException ? e.Message : $"{e.GetType().Name}: {e.Message}" };
        }
        Reply(reply, outp, outLen);
    }

    [UnmanagedCallersOnly]
    static void FreeReply(nint ctx, byte* p, nuint len) => NativeMemory.Free(p);

    [UnmanagedCallersOnly]
    static void Unload(nint ctx)
    {
        try
        {
            var mod = ((ModPlugin)GCHandle.FromIntPtr(ctx).Target!).Mod;
            UnloadMod(mod);
        }
        catch (Exception e)
        {
            Log.Error("dotnet: unload", e);
        }
    }

    internal static void UnloadMod(JibbersMod mod)
    {
        Input.RemoveOwner(mod.Id);
        try
        {
            ModContext.With(mod, () =>
            {
                mod.OnUnload();
                return 0;
            });
        }
        catch (Exception e)
        {
            Log.Error(mod.Id + ": OnUnload", e);
        }
        Hooks.RemoveOwner(mod.Id);
        Detour.RemoveOwner(mod.Id);
        try
        {
            MainThread.Run(() =>
            {
                Events.RemoveOwner(mod.Id);
                GameVars.RemoveOwner(mod.Id);
                UI.Hud.RemoveOwner(mod.Id);
            });
        }
        catch (Exception e)
        {
            Log.Info($"{mod.Id}: could not remove game subscriptions: {e.Message}");
        }
    }
}
