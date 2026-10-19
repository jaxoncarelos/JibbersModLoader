// jibbers-gen: generates the SDK's game bindings and the docs index.
//
//   jibbers-gen bindings --dump <dump.json> [--dump <more.json>]... --out <Jibbers.Sdk/Generated>
//       Game.g.cs        friendly bindings for every catalogue symbol (stable across game builds)
//       Interop.*.g.cs   raw bindings for every class of the dumped images (real names)
//   jibbers-gen docs --dump <dump.json>... --sdk <Jibbers.Sdk.dll> --out <docs/data>
//       api.json         every SDK type and member, every symbol, every raw class, for the docs site
//   jibbers-gen lint | resolve --dump <dump.json>
//   jibbers-gen import --managed <dir> [--images <ScriptingAssemblies.json>] --build <text> --out <dump.json>
//       a dump of .NET assemblies (Unity's engine modules: their names are not obfuscated)
//
// Game dumps come from the game: {"cmd":"sdk.symbols.dump","images":[...]} (see docs).

using System.Text.Json;
using Jibbers.Sdk.Generator;
using Jibbers.Sdk.Symbols;

static List<string> Opts(List<string> args, string name)
{
    var r = new List<string>();
    for (var i = args.IndexOf(name); i >= 0; i = args.IndexOf(name))
    {
        if (i + 1 >= args.Count)
        {
            throw new ArgumentException($"{name} needs a value");
        }
        r.Add(args[i + 1]);
        args.RemoveRange(i, 2);
    }
    return r;
}

static Snapshot LoadDumps(IEnumerable<string> paths)
{
    var all = new Snapshot();
    foreach (var p in paths)
    {
        var s = JsonSerializer.Deserialize<Snapshot>(File.ReadAllText(p)) ?? throw new ArgumentException($"{p}: not a dump");
        all.GameBuild = all.GameBuild.Length == 0 ? s.GameBuild : all.GameBuild + "; " + s.GameBuild;
        var have = all.Classes.Select(c => c.Image + "|" + c.FullName).ToHashSet();
        all.Classes.AddRange(s.Classes.Where(c => have.Add(c.Image + "|" + c.FullName)));
    }
    return all;
}

var argv = args.ToList();
if (argv.Count == 0)
{
    Console.Error.WriteLine("usage: jibbers-gen <bindings|docs|lint|resolve> ... (see the header of Program.cs)");
    return 2;
}
var cmd = argv[0];
argv.RemoveAt(0);
try
{
    var catalogue = Catalogue.Parse(SymbolTable.BuiltInJson);
    switch (cmd)
    {
        case "lint":
        {
            var errs = Shape.Lint(catalogue);
            errs.ForEach(Console.WriteLine);
            Console.WriteLine($"{catalogue.Symbols.Count} symbols, {errs.Count} problems");
            return errs.Count == 0 ? 0 : 1;
        }
        case "resolve":
        {
            var snap = LoadDumps(Opts(argv, "--dump"));
            var res = Shape.Resolve(catalogue, snap);
            foreach (var s in catalogue.Symbols)
            {
                Console.WriteLine(res.Ok.TryGetValue(s.Id, out var r) ? $"ok    {s.Id,-30} {snap.Classes[r.Class].FullName}" : $"FAIL  {s.Id,-30} {res.Failed[s.Id]}");
            }
            Console.WriteLine($"{res.Ok.Count} resolved, {res.Failed.Count} failed");
            return res.Failed.Count == 0 ? 0 : 1;
        }
        case "bindings":
        {
            var snap = LoadDumps(Opts(argv, "--dump"));
            var outDir = Opts(argv, "--out").SingleOrDefault() ?? throw new ArgumentException("--out <dir>");
            var model = new Model(catalogue, snap);
            Directory.CreateDirectory(outDir);
            foreach (var old in Directory.GetFiles(outDir, "*.g.cs"))
            {
                File.Delete(old);
            }
            var files = new BindingWriter(model).Write();
            foreach (var (name, text) in files)
            {
                File.WriteAllText(Path.Combine(outDir, name), text);
            }
            Console.WriteLine($"{files.Count} files, {model.RawClasses.Count} raw classes, {model.Friendly.Count} symbols -> {outDir}");
            return 0;
        }
        case "docs":
        {
            var snap = LoadDumps(Opts(argv, "--dump"));
            var sdk = Opts(argv, "--sdk").SingleOrDefault() ?? throw new ArgumentException("--sdk <Jibbers.Sdk.dll>");
            var outDir = Opts(argv, "--out").SingleOrDefault() ?? throw new ArgumentException("--out <dir>");
            Directory.CreateDirectory(outDir);
            var n = DocsWriter.Write(new Model(catalogue, snap), sdk, outDir);
            Console.WriteLine($"{n} entries -> {outDir}");
            return 0;
        }
        case "import":
        {
            var dir = Opts(argv, "--managed").SingleOrDefault() ?? throw new ArgumentException("--managed <dir>");
            var outFile = Opts(argv, "--out").SingleOrDefault() ?? throw new ArgumentException("--out <dump.json>");
            var build = Opts(argv, "--build").SingleOrDefault() ?? "";
            // Only the assemblies the game ships (its ScriptingAssemblies.json), when given.
            var list = Opts(argv, "--images").SingleOrDefault();
            var wanted = list is null ? null : JsonDocument.Parse(File.ReadAllText(list)).RootElement.GetProperty("names").EnumerateArray().Select(e => e.GetString()!).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var snap = new Snapshot { GameBuild = build };
            foreach (var dll in Directory.GetFiles(dir, "*.dll").Order())
            {
                if (wanted is null || wanted.Contains(Path.GetFileName(dll)))
                {
                    snap.Classes.AddRange(ManagedImport.Read(dll));
                }
            }
            File.WriteAllText(outFile, JsonSerializer.Serialize(snap));
            Console.WriteLine($"{snap.Classes.Select(c => c.Image).Distinct().Count()} images, {snap.Classes.Count} classes -> {outFile}");
            return 0;
        }
        default:
            Console.Error.WriteLine($"unknown command {cmd}");
            return 2;
    }
}
catch (Exception e) when (e is ArgumentException or IOException or JsonException)
{
    Console.Error.WriteLine($"jibbers-gen: {e.Message}");
    return 2;
}
