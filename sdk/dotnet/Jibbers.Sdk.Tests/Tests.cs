// Tests for the parts of the SDK that need no game: the symbol matcher, settings, the Mods-tab
// protocol and the catalogue. `dotnet run` runs them all; the exit code is the number of failures.
// A real game dump, if present (out/dumps/*.json), is also resolved against the catalogue.

using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Jibbers.Sdk;
using Jibbers.Sdk.Symbols;
using FieldInfo = Jibbers.Sdk.Symbols.FieldInfo;
using MethodInfo = Jibbers.Sdk.Symbols.MethodInfo;

[AttributeUsage(AttributeTargets.Method)]
sealed class TestAttribute : Attribute;

static class Assert
{
    public static void True(bool c, string what = "", [CallerArgumentExpression(nameof(c))] string expr = "")
    {
        if (!c)
        {
            throw new Exception($"expected true: {expr} {what}");
        }
    }

    public static void Equal<T>(T expected, T actual, [CallerArgumentExpression(nameof(actual))] string expr = "")
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new Exception($"{expr}: expected {expected}, got {actual}");
        }
    }

    public static void Throws<TE>(Action a, [CallerArgumentExpression(nameof(a))] string expr = "") where TE : Exception
    {
        try
        {
            a();
        }
        catch (TE)
        {
            return;
        }
        throw new Exception($"expected {typeof(TE).Name}: {expr}");
    }
}

static class Program
{
    static int Main()
    {
        var tests = typeof(Program).Assembly.GetTypes().SelectMany(t => t.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
            .Where(m => m.GetCustomAttribute<TestAttribute>() is not null).OrderBy(m => m.DeclaringType!.Name + m.Name).ToList();
        var failed = 0;
        foreach (var t in tests)
        {
            try
            {
                t.Invoke(null, null);
                Console.WriteLine($"ok    {t.DeclaringType!.Name}.{t.Name}");
            }
            catch (TargetInvocationException e)
            {
                failed++;
                Console.WriteLine($"FAIL  {t.DeclaringType!.Name}.{t.Name}: {e.InnerException?.Message}");
            }
        }
        Console.WriteLine($"{tests.Count - failed} passed, {failed} failed");
        return failed;
    }
}

static class ShapeTests
{
    static ClassInfo K(string name, string parent, (string, string)[] fields, (string, string[], string)[] methods, bool vt = false) => new()
    {
        Image = Shape.GameImage,
        Name = name,
        FullName = name,
        Parent = parent,
        ValueType = vt,
        Fields = fields.Select(f => new FieldInfo { Name = f.Item1, Type = f.Item2 }).ToList(),
        Methods = methods.Select(m => new MethodInfo { Name = m.Item1, Params = m.Item2.ToList(), Return = m.Item3 }).ToList(),
    };

    static Snapshot Snap() => new()
    {
        GameBuild = "test",
        Classes =
        {
            K("MapRuntimeImporter", "UnityEngine.MonoBehaviour", new[] { ("uzv", "UnityEngine.Terrain[]"), ("ab", "System.Int32") }, new[] { ("qa", new[] { "MapData" }, "System.Void") }),
            K("kq", "System.Object", new[] { ("aa", "System.String"), ("ab", "System.String"), ("ac", "System.Int32") }, new[] { ("Run", new[] { "System.String[]" }, "System.Void") }),
            K("zz", "System.Object", new[] { ("cm", "kq[]") }, Array.Empty<(string, string[], string)>()),
            K("xy", "System.Object", new[] { ("aa", "System.String") }, Array.Empty<(string, string[], string)>()),
            K("xw", "System.Object", new[] { ("aa", "System.String") }, Array.Empty<(string, string[], string)>()),
        },
    };

    static Catalogue Cat(string json) => Catalogue.Parse(json);

    [Test]
    static void ObfuscatedNames()
    {
        Assert.True(Shape.IsObfuscated("uzv") && Shape.IsObfuscated("zo") && Shape.IsObfuscated("<abc>k__BackingField"));
        Assert.True(!Shape.IsObfuscated("Update") && !Shape.IsObfuscated("a") && !Shape.IsObfuscated("abcd") && !Shape.IsObfuscated("Ab"));
    }

    [Test]
    static void ResolvesByShapeAndReference()
    {
        var c = Cat("""
        {"symbols": [
          {"id": "map.importer", "summary": "s", "match": {"name": "MapRuntimeImporter"},
           "members": {"terrains": {"field": {"type": "UnityEngine.Terrain[]"}}, "load": {"method": {"params": ["MapData"], "return": "System.Void"}}}},
          {"id": "chat.registry", "summary": "s", "match": {"obfuscated": true, "fields": [{"type": "@chat.command[]"}]}},
          {"id": "chat.command", "summary": "s", "match": {"obfuscated": true, "methods": [{"name": "Run", "params": ["System.String[]"]}]},
           "members": {"usage": {"field": {"type": "System.String", "nth": 1}}}}
        ]}
        """);
        Assert.Equal(0, Shape.Lint(c).Count);
        var s = Snap();
        var r = Shape.Resolve(c, s);
        Assert.Equal(0, r.Failed.Count);
        Assert.Equal("uzv", Shape.MemberName(s, r.Ok["map.importer"].Members["terrains"]));
        Assert.Equal("qa(MapData)", Shape.MemberName(s, r.Ok["map.importer"].Members["load"]));
        Assert.Equal("zz", r.ClassName(s, "chat.registry"));
        Assert.Equal("ab", Shape.MemberName(s, r.Ok["chat.command"].Members["usage"]));
    }

    [Test]
    static void AmbiguityAndMemberErrorsAreFailures()
    {
        var c = Cat("""
        {"symbols": [
          {"id": "a", "summary": "s", "match": {"obfuscated": true, "parent": "System.Object", "fields": [{"type": "System.String"}], "instance_fields": [1, 1]}},
          {"id": "b", "summary": "s", "match": {"name": "kq"}, "members": {"name": {"field": {"type": "System.String"}}}},
          {"id": "c", "summary": "s", "match": {"name": "Nope"}},
          {"id": "d", "summary": "s", "match": {"obfuscated": true, "fields": [{"type": "@c"}]}}
        ]}
        """);
        var r = Shape.Resolve(c, Snap());
        Assert.Equal("ambiguous", r.Failed["a"].Status);
        Assert.True(r.Failed["a"].Detail == "xy, xw");
        Assert.Equal("members", r.Failed["b"].Status);
        Assert.Equal("not_found", r.Failed["c"].Status);
        Assert.Equal("dependency", r.Failed["d"].Status);
    }

    [Test]
    static void CyclesFailInsteadOfLooping()
    {
        var c = Cat("""{"symbols": [{"id": "x", "summary": "s", "match": {"fields": [{"type": "@y"}]}}, {"id": "y", "summary": "s", "match": {"fields": [{"type": "@x"}]}}]}""");
        var r = Shape.Resolve(c, Snap());
        Assert.Equal(0, r.Ok.Count);
        Assert.Equal(2, r.Failed.Count);
    }

    [Test]
    static void MembersMayReferToSymbolsThatReferBack()
    {
        var s = Snap();
        s.Classes[1].Fields.Add(new FieldInfo { Name = "bk", Type = "zz" });
        var c = Cat("""
        {"symbols": [
          {"id": "a", "summary": "s", "match": {"name": "zz"}, "members": {"cmds": {"field": {"type": "@b[]"}}}},
          {"id": "b", "summary": "s", "match": {"name": "kq", "fields": [{"type": "@a"}]}, "members": {"back": {"field": {"type": "@a"}}}}
        ]}
        """);
        var r = Shape.Resolve(c, s);
        Assert.Equal(0, r.Failed.Count);
        Assert.Equal("cm", Shape.MemberName(s, r.Ok["a"].Members["cmds"]));
    }

    [Test]
    static void InheritedAndBackingMembers()
    {
        var s = Snap();
        s.Classes.Add(K("Button", "Selectable", new[] { ("cb", "System.Action<Button>") }, Array.Empty<(string, string[], string)>()));
        s.Classes.Add(K("Selectable", "UnityEngine.MonoBehaviour", new[] { ("<uhg>k__BackingField", "System.Boolean"), ("plain", "System.Boolean") }, new[] { ("kda", new[] { "System.Boolean" }, "System.Void") }));
        var c = Cat("""
        {"symbols": [{"id": "ui.button", "summary": "s", "match": {"name": "Button", "methods": [{"argc": 1, "inherited": true}]},
          "members": {"interactable": {"field": {"type": "System.Boolean", "backing": true, "inherited": true}}}}]}
        """);
        var r = Shape.Resolve(c, s);
        Assert.Equal(0, r.Failed.Count);
        var m = r.Ok["ui.button"].Members["interactable"];
        Assert.Equal("Selectable", s.Classes[m.Class].Name);
        Assert.Equal("<uhg>k__BackingField", Shape.MemberName(s, m));
    }

    [Test]
    static void LintCatchesMistakes()
    {
        var e = Shape.Lint(Cat("""{"symbols": [{"id": "Bad Id", "summary": "", "match": {}}, {"id": "ok", "summary": "s", "match": {"fields": [{"type": "@missing[]"}]}, "members": {"m": {"method": {}}}}]}"""));
        Assert.True(e.Any(x => x.Contains("lowercase")));
        Assert.True(e.Any(x => x.Contains("missing summary")));
        Assert.True(e.Any(x => x.Contains("match anything")));
        Assert.True(e.Any(x => x.Contains("@missing")));
        Assert.True(e.Any(x => x.Contains("ok.m")));
    }

    [Test]
    static void BuiltInCatalogueIsValid()
    {
        var c = Catalogue.Parse(SymbolTable.BuiltInJson);
        Assert.True(c.Symbols.Count >= 60, $"{c.Symbols.Count} symbols");
        var errs = Shape.Lint(c);
        Assert.Equal(0, errs.Count);
    }

    /// <summary>The real dump of the validated build, when the repo has one: every symbol must resolve.</summary>
    [Test]
    static void EverySymbolResolvesOnTheDumpedBuild()
    {
        var dir = AppContext.BaseDirectory;
        string? dump = null;
        for (var d = new DirectoryInfo(dir); d is not null; d = d.Parent)
        {
            var p = Path.Combine(d.FullName, "out", "dumps", "2026-10-18-build.json");
            if (File.Exists(p))
            {
                dump = p;
                break;
            }
        }
        if (dump is null)
        {
            Console.WriteLine("      (no dump in out/dumps: skipped)");
            return;
        }
        var snap = JsonSerializer.Deserialize<Snapshot>(File.ReadAllText(dump))!;
        var r = Shape.Resolve(Catalogue.Parse(SymbolTable.BuiltInJson), snap);
        Assert.True(r.Failed.Count == 0, string.Join("; ", r.Failed.Select(f => $"{f.Key}: {f.Value}")));
        Assert.Equal("pha", Shape.MemberName(snap, r.Ok["player.ragdoll_spawner"].Members["rider"]));
        Assert.Equal("gwp()", Shape.MemberName(snap, r.Ok["ui.navigatable_menu"].Members["layout"]));
        Assert.Equal("kda(System.Boolean)", Shape.MemberName(snap, r.Ok["ui.behaviour"].Members["set_interactable"]));
    }
}

static class SettingsTests
{
    [Test]
    static void SchemaMatchesWhatTheModsTabReads()
    {
        var s = Setting.Slider("speed", "Speed", 1, 0.5, 3, 0.1).InSection("Movement").WithTooltip("How fast.").ToJson();
        Assert.Equal("slider", s["type"]!.GetValue<string>());
        Assert.Equal(0.5, s["min"]!.GetValue<double>());
        Assert.Equal("Movement", s["section"]!.GetValue<string>());
        var c = Setting.Choice("mode", "Mode", new[] { "Soft", "Hard" }, "Soft").ToJson();
        Assert.Equal(2, c["options"]!.AsArray().Count);
        Assert.Equal("key", Setting.KeyBinding("hotkey", "Hotkey", 0x78).ToJson()["type"]!.GetValue<string>());
    }

    [Test]
    static void ValuesAreValidated()
    {
        var sl = Setting.Slider("speed", "Speed", 1, 0.5, 3, 0.1);
        Assert.Equal(3.0, sl.Normalize(9)!.GetValue<double>());
        Assert.Throws<ArgumentException>(() => sl.Normalize("fast"));
        var ch = Setting.Choice("mode", "Mode", new[] { "Soft", "Hard" }, "Soft");
        Assert.Throws<ArgumentException>(() => ch.Normalize("Medium"));
        Assert.Throws<ArgumentException>(() => Setting.KeyBinding("k", "K", 0).Normalize(300));
        Assert.Throws<ArgumentException>(() => new SettingsStore(new[] { Setting.Slider("x", "X", 1, 2, 1, 0.1) }, null, null));
        Assert.Throws<ArgumentException>(() => new SettingsStore(new[] { Setting.Toggle("a", "A", true), Setting.Toggle("a", "B", true) }, null, null));
    }

    [Test]
    static void EnabledToggleComesFirstAndValuesPersist()
    {
        var path = Path.Combine(Path.GetTempPath(), "jibbers-test-" + Guid.NewGuid() + ".json");
        try
        {
            var decl = new[] { Setting.Slider("speed", "Speed", 1, 0.5, 3, 0.1), Setting.Toggle("trail", "Trail", true) };
            var s = new SettingsStore(decl, "My Mod", path);
            Assert.Equal("enabled", s.Settings[0].Key);
            s.Set("speed", 2.5);
            s.Set("trail", false);
            var again = new SettingsStore(decl, "My Mod", path);
            Assert.Equal(2.5, again.Get("speed")!.GetValue<double>());
            Assert.Equal(false, again.Get("trail")!.GetValue<bool>());
            File.WriteAllText(path, "{ not json");
            Assert.Equal(1.0, new SettingsStore(decl, "My Mod", path).Get("speed")!.GetValue<double>());
        }
        finally
        {
            File.Delete(path);
        }
    }
}

sealed class SampleMod : JibbersMod
{
    public override string Id => "sample";
    public override string Name => "Sample";
    public override IEnumerable<Setting> Settings => new[] { Setting.Slider("power", "Power", 1, 0, 2, 0.05) };
    public readonly List<string> Changed = new();
    public override void OnSettingChanged(string key, JsonNode value) => Changed.Add(key + "=" + value.ToJsonString());
    public override JsonNode? OnCommand(string cmd, JsonObject request) => cmd == "sample.ping" ? new JsonObject { ["pong"] = true } : null;
}

static class ProtocolTests
{
    static (SampleMod, object) Plugin()
    {
        var mod = new SampleMod();
        var store = new SettingsStore(mod.Settings, mod.Name, null);
        typeof(JibbersMod).GetField("Store", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(mod, store);
        var pt = typeof(JibbersMod).Assembly.GetType("Jibbers.Sdk.ModPlugin")!;
        return (mod, Activator.CreateInstance(pt, mod)!);
    }

    static JsonNode Call(object plugin, string json) =>
        (JsonNode)plugin.GetType().GetMethod("Handle")!.Invoke(plugin, new object[] { JsonNode.Parse(json)!.AsObject() })!;

    [Test]
    static void AnswersTheModsTab()
    {
        var (mod, p) = Plugin();
        var info = Call(p, """{"cmd":"plugin.info"}""");
        Assert.Equal("sample", info["id"]!.GetValue<string>());
        Assert.Equal(2, info["settings"]!.AsArray().Count);
        var set = Call(p, """{"cmd":"plugin.set","key":"power","value":1.5}""");
        Assert.Equal(1.5, set["values"]!["power"]!.GetValue<double>());
        Assert.Equal("power=1.5", mod.Changed.Single());
        Call(p, """{"cmd":"plugin.enable","on":false}""");
        Assert.True(!mod.Enabled);
        Assert.Equal(true, Call(p, """{"cmd":"sample.ping"}""")["ok"]!.GetValue<bool>());
    }

    [Test]
    static void RejectsUnknownCommands()
    {
        var (_, p) = Plugin();
        Assert.Throws<TargetInvocationException>(() => Call(p, """{"cmd":"plugin.nope"}"""));
        Assert.Throws<TargetInvocationException>(() => Call(p, """{"cmd":"other.thing"}"""));
    }
}
