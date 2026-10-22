using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Jibbers.Sdk.Symbols;
using FieldInfo = System.Reflection.FieldInfo;

namespace Jibbers.Sdk.Generator;

/// <summary>Writes <c>api.json</c> for the docs site: the SDK's public API, every symbol, every raw class.</summary>
internal static class DocsWriter
{
    sealed record Arg(string Name, string Type, string Doc);

    /// <summary>One searchable entry.</summary>
    sealed class Entry
    {
        public required string Kind;
        public required string Name;
        public required string Parent;
        public string Signature = "";
        public string Doc = "";
        public string Remarks = "";
        public string Example = "";
        public string Returns = "";
        public List<Arg> Args = new();
        public List<string> Tags = new();
        public string Real = "";
        public string Section = "";

        public JsonObject Json()
        {
            var o = new JsonObject { ["k"] = Kind, ["n"] = Name, ["p"] = Parent, ["s"] = Signature, ["d"] = Doc, ["sec"] = Section };
            if (Remarks.Length > 0) o["rm"] = Remarks;
            if (Example.Length > 0) o["ex"] = Example;
            if (Returns.Length > 0) o["r"] = Returns;
            if (Real.Length > 0) o["real"] = Real;
            if (Tags.Count > 0) o["t"] = new JsonArray(Tags.Select(t => (JsonNode)t!).ToArray());
            if (Args.Count > 0) o["a"] = new JsonArray(Args.Select(a => (JsonNode)new JsonObject { ["n"] = a.Name, ["t"] = a.Type, ["d"] = a.Doc }).ToArray());
            return o;
        }
    }

    public static int Write(Model m, string sdkDll, string outDir)
    {
        var entries = new List<Entry>();
        Sdk(entries, sdkDll);
        Symbols(entries, m);
        Raw(entries, m);
        // Raw entries are most of the index: a separate file the site loads on demand.
        foreach (var (file, part) in new[] { ("api.json", entries.Where(e => !e.Kind.StartsWith("raw"))), ("raw.json", entries.Where(e => e.Kind.StartsWith("raw"))) })
        {
            var root = new JsonObject
            {
                ["build"] = m.Snap.GameBuild,
                ["generated"] = DateTime.UtcNow.ToString("yyyy-MM-dd"),
                ["sdk"] = AssemblyName.GetAssemblyName(sdkDll).Version?.ToString(3),
                ["entries"] = new JsonArray(part.Select(e => (JsonNode)e.Json()).ToArray()),
            };
            File.WriteAllText(Path.Combine(outDir, file), root.ToJsonString(new JsonSerializerOptions { WriteIndented = false }));
        }
        return entries.Count;
    }

    // ---- the SDK's public API, from reflection + the XML doc file ----

    static string Clean(XElement? e)
    {
        if (e is null)
        {
            return "";
        }
        var parts = e.Nodes().Select(n => n switch
        {
            XText t => t.Value,
            XElement x when x.Name == "see" || x.Name == "seealso" => Short((string?)x.Attribute("cref") ?? (string?)x.Attribute("langword") ?? ""),
            XElement x when x.Name == "paramref" || x.Name == "typeparamref" => (string?)x.Attribute("name") ?? "",
            XElement x when x.Name == "c" => "`" + x.Value + "`",
            XElement x when x.Name == "code" => "\n```\n" + x.Value.Trim('\n') + "\n```\n",
            XElement x when x.Name == "para" => "\n\n" + Clean(x),
            XElement x => Clean(x),
            _ => "",
        });
        return Regex.Replace(string.Concat(parts), @"[ \t]*\n[ \t]*", "\n").Replace("\n\n\n", "\n\n").Trim();
    }

    static string Short(string cref)
    {
        var s = cref.Length > 2 && cref[1] == ':' ? cref[2..] : cref;
        s = Regex.Replace(s, @"\(.*\)", "");
        s = s.Replace("Jibbers.Sdk.", "");
        return "`" + s.Replace("`1", "<T>") + "`";
    }

    static string TypeName(Type t)
    {
        if (t.IsGenericParameter)
        {
            return t.Name;
        }
        if (t.IsByRef)
        {
            return "ref " + TypeName(t.GetElementType()!);
        }
        if (t.IsPointer)
        {
            return TypeName(t.GetElementType()!) + "*";
        }
        if (t.IsArray)
        {
            return TypeName(t.GetElementType()!) + "[]";
        }
        if (Nullable.GetUnderlyingType(t) is { } u)
        {
            return TypeName(u) + "?";
        }
        var alias = t.FullName switch
        {
            "System.Void" => "void", "System.Boolean" => "bool", "System.Int32" => "int", "System.Int64" => "long", "System.Single" => "float",
            "System.Double" => "double", "System.String" => "string", "System.Byte" => "byte", "System.IntPtr" => "nint", "System.UIntPtr" => "nuint",
            "System.Object" => "object", "System.UInt32" => "uint", "System.Int16" => "short", "System.Char" => "char",
            _ => null,
        };
        if (alias is not null)
        {
            return alias;
        }
        if (t.IsGenericType)
        {
            var n = t.Name[..t.Name.IndexOf('`')];
            return $"{n}<{string.Join(", ", t.GetGenericArguments().Select(TypeName))}>";
        }
        return t.IsNested ? TypeName(t.DeclaringType!) + "." + t.Name : t.Name;
    }

    static string XmlId(MemberInfo mi)
    {
        static string P(Type t) => t.IsGenericParameter ? (t.DeclaringMethod is null ? "`" : "``") + t.GenericParameterPosition
            : t.IsByRef ? P(t.GetElementType()!) + "@"
            : t.IsPointer ? P(t.GetElementType()!) + "*"
            : t.IsArray ? P(t.GetElementType()!) + "[]"
            : t.IsGenericType ? $"{t.Namespace}.{t.Name[..t.Name.IndexOf('`')]}{{{string.Join(",", t.GetGenericArguments().Select(P))}}}"
            : (t.FullName ?? t.Name).Replace('+', '.');
        var owner = (mi.DeclaringType?.FullName ?? "").Replace('+', '.');
        return mi switch
        {
            Type t => "T:" + (t.FullName ?? "").Replace('+', '.'),
            MethodBase mb => $"M:{owner}.{(mb.IsConstructor ? "#ctor" : mb.Name)}{(mb.IsGenericMethod ? "``" + mb.GetGenericArguments().Length : "")}"
                + (mb.GetParameters().Length > 0 ? "(" + string.Join(",", mb.GetParameters().Select(p => P(p.ParameterType))) + ")" : ""),
            PropertyInfo p => $"P:{owner}.{p.Name}",
            FieldInfo f => $"F:{owner}.{f.Name}",
            EventInfo e => $"E:{owner}.{e.Name}",
            _ => "",
        };
    }

    static string Section(Type t)
    {
        var ns = t.Namespace ?? "";
        if (ns.StartsWith("Jibbers.Sdk.Game."))
        {
            return "Game: " + ns["Jibbers.Sdk.Game.".Length..];
        }
        return t.Name switch
        {
            "JibbersMod" or "ModFeature" or "FeatureMod`1" or "SettingAttribute" or "ToggleAttribute" or "SliderAttribute" or "ChoiceAttribute" or "KeyBindingAttribute" or "Setting" or "SettingsStore" or "SettingKind" => "Writing a mod",
            "MainThread" or "Log" or "Hooks" or "Detour" or "Detour`1" or "MethodHook" => "Runtime",
            "Events" or "GameEvent" or "Subscription" => "Events",
            "GameVars" => "Game settings",
            "Input" or "Key" => "Input",
            "Unity" or "GameRef" or "Vector2" or "Vector3" or "Vector4" or "Quaternion" or "Color" or "Rect" or "Bounds" or "Matrix4x4" => "Unity",
            "Inspect" or "Delegates" or "IGameObject" or "GameObjectExtensions" => "Utilities",
            _ when ns.StartsWith("Jibbers.Sdk.Features.") => "Gameplay features",
            _ when ns == "Jibbers.Sdk.Diagnostics" => "Developer tools",
            _ when ns == "Jibbers.Sdk.Interop" => "IL2CPP",
            _ when ns == "Jibbers.Sdk.Symbols" => "Symbols",
            _ => "SDK",
        };
    }

    /// <summary>Parameters a caller writes (not ones the compiler fills in, like [CallerFilePath]).</summary>
    static IEnumerable<ParameterInfo> Visible(MethodBase m) =>
        m.GetParameters().Where(p => !p.CustomAttributes.Any(a => a.AttributeType.Name.StartsWith("Caller")));

    static void Sdk(List<Entry> entries, string dll)
    {
        var asm = Assembly.LoadFrom(dll);
        var xmlPath = Path.ChangeExtension(dll, ".xml");
        var docs = File.Exists(xmlPath)
            ? XDocument.Load(xmlPath).Descendants("member").Where(x => x.Attribute("name") is not null).GroupBy(x => (string)x.Attribute("name")!).ToDictionary(g => g.Key, g => g.First())
            : new Dictionary<string, XElement>();
        Entry Make(MemberInfo mi, string kind, string name, string parent, string sig, Type owner)
        {
            docs.TryGetValue(XmlId(mi), out var d);
            var e = new Entry
            {
                Kind = kind,
                Name = name,
                Parent = parent,
                Signature = sig,
                Doc = Clean(d?.Element("summary")),
                Remarks = Clean(d?.Element("remarks")),
                Example = Clean(d?.Element("example")),
                Returns = Clean(d?.Element("returns")),
                Section = Section(owner),
            };
            if (mi is MethodBase mb)
            {
                foreach (var p in Visible(mb))
                {
                    e.Args.Add(new Arg(p.Name ?? "", TypeName(p.ParameterType), Clean(d?.Elements("param").FirstOrDefault(x => (string?)x.Attribute("name") == p.Name))));
                }
            }
            return e;
        }
        foreach (var t in asm.GetExportedTypes().Where(t => !(t.Namespace ?? "").StartsWith("Jibbers.Interop")).OrderBy(t => t.FullName))
        {
            var kind = t.IsEnum ? "enum" : t.IsInterface ? "interface" : t.IsValueType ? "struct" : t.IsAbstract && t.IsSealed ? "static class" : "class";
            var full = (t.Namespace ?? "") + "." + TypeName(t);
            entries.Add(Make(t, kind, TypeName(t), t.Namespace ?? "", $"{kind} {TypeName(t)}", t));
            const BindingFlags all = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
            if (t.IsEnum)
            {
                foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.Static))
                {
                    entries.Add(Make(f, "value", f.Name, full, $"{TypeName(t)}.{f.Name} = {Convert.ToInt64(f.GetRawConstantValue())}", t));
                }
                continue;
            }
            if (typeof(Delegate).IsAssignableFrom(t))
            {
                // A delegate: its signature is all a reader needs.
                var inv = t.GetMethod("Invoke")!;
                entries[^1].Signature = $"delegate {TypeName(inv.ReturnType)} {TypeName(t)}({string.Join(", ", inv.GetParameters().Select(p => TypeName(p.ParameterType) + " " + p.Name))})";
                entries[^1].Kind = "delegate";
                continue;
            }
            foreach (var c in t.GetConstructors(BindingFlags.Public | BindingFlags.Instance))
            {
                entries.Add(Make(c, "constructor", TypeName(t), full, $"new {TypeName(t)}({string.Join(", ", c.GetParameters().Select(p => TypeName(p.ParameterType) + " " + p.Name))})", t));
            }
            foreach (var mi in t.GetMethods(all).Where(x => !x.IsSpecialName && x.Name is not ("Equals" or "GetHashCode" or "ToString" or "Deconstruct" or "<Clone>$" or "PrintMembers")))
            {
                var gen = mi.IsGenericMethod ? "<" + string.Join(", ", mi.GetGenericArguments().Select(a => a.Name)) + ">" : "";
                var sig = $"{(mi.IsStatic ? "static " : "")}{TypeName(mi.ReturnType)} {mi.Name}{gen}({string.Join(", ", Visible(mi).Select(p => TypeName(p.ParameterType) + " " + p.Name + (p.HasDefaultValue ? " = " + (p.DefaultValue is string ds ? "\"" + ds + "\"" : p.DefaultValue ?? "null") : "")))})";
                entries.Add(Make(mi, "method", mi.Name, full, sig, t));
            }
            foreach (var p in t.GetProperties(all))
            {
                var acc = (p.GetMethod?.IsPublic == true ? "get; " : "") + (p.SetMethod?.IsPublic == true ? "set; " : "");
                entries.Add(Make(p, "property", p.Name, full, $"{((p.GetMethod ?? p.SetMethod)!.IsStatic ? "static " : "")}{TypeName(p.PropertyType)} {p.Name} {{ {acc}}}", t));
            }
            foreach (var f in t.GetFields(all).Where(f => !f.IsSpecialName))
            {
                entries.Add(Make(f, "field", f.Name, full, $"{(f.IsStatic ? "static " : "")}{(f.IsLiteral ? "const " : f.IsInitOnly ? "readonly " : "")}{TypeName(f.FieldType)} {f.Name}", t));
            }
        }
    }

    // ---- catalogue symbols (with the real names on the dump's build) ----

    static void Symbols(List<Entry> entries, Model m)
    {
        foreach (var f in m.Friendly)
        {
            var path = f.Namespace + "." + f.Name;
            entries.Add(new Entry
            {
                Kind = "symbol",
                Name = f.Symbol.Id,
                Parent = path,
                Signature = (f.IsEvent ? "event " : "") + path,
                Doc = f.Symbol.Summary,
                Remarks = f.Symbol.Notes,
                Tags = f.Symbol.Tags,
                Real = f.Info.FullName,
                Section = f.IsEvent ? "Game events" : "Game: " + f.Namespace["Jibbers.Sdk.Game.".Length..],
            });
            foreach (var (name, mem) in f.Resolved.Members)
            {
                var decl = m.Snap.Classes[mem.Class];
                string sig, real;
                if (mem.IsField)
                {
                    var fi = decl.Fields[mem.Index];
                    sig = $"{m.Type(fi.Type, true).Cs.Replace("global::", "")} {Names.Pascal(name)}";
                    real = $"{fi.Name}: {fi.Type}";
                }
                else
                {
                    var mi = decl.Methods[mem.Index];
                    sig = $"{m.Type(mi.Return, true).Cs.Replace("global::", "")} {Names.Pascal(name)}({string.Join(", ", mi.Params.Select((p, i) => m.Type(p, true).Cs.Replace("global::", "") + " " + mi.ParamName(i)))})";
                    real = $"{mi.Name}({string.Join(", ", mi.Params)}) -> {mi.Return}";
                }
                entries.Add(new Entry
                {
                    Kind = mem.IsField ? "symbol field" : "symbol method",
                    Name = Names.Pascal(name),
                    Parent = path,
                    Signature = sig,
                    Doc = $"`{f.Symbol.Id}.{name}`",
                    Real = real,
                    Section = f.IsEvent ? "Game events" : "Game: " + f.Namespace["Jibbers.Sdk.Game.".Length..],
                });
            }
        }
        foreach (var (id, fail) in m.Resolution.Failed)
        {
            entries.Add(new Entry { Kind = "symbol", Name = id, Parent = "", Doc = $"Does not resolve on build {m.Snap.GameBuild}: {fail}", Section = "Game: unresolved", Tags = new() { "unresolved" } });
        }
    }

    // ---- every raw class ----

    static void Raw(List<Entry> entries, Model m)
    {
        foreach (var c in m.RawClasses)
        {
            var path = c.Namespace + "." + c.Name;
            var k = c.Info;
            entries.Add(new Entry
            {
                Kind = "raw class",
                Name = c.Name,
                Parent = c.Namespace,
                Signature = $"{path} : {k.Parent}",
                Doc = $"`{k.FullName}` in {k.Image}.",
                Real = k.FullName,
                Section = "Raw: " + k.Image,
            });
            foreach (var f in k.Fields)
            {
                entries.Add(new Entry { Kind = "raw field", Name = f.Name, Parent = path, Signature = $"{(f.Static ? "static " : "")}{f.Type} {f.Name}", Section = "Raw: " + k.Image });
            }
            foreach (var mi in k.Methods.Where(x => x.Name != ".cctor" && !x.Name.Contains('<')))
            {
                entries.Add(new Entry
                {
                    Kind = "raw method",
                    Name = mi.Name,
                    Parent = path,
                    Signature = $"{(mi.Static ? "static " : "")}{mi.Return} {mi.Name}({string.Join(", ", mi.Params.Select((t, i) => mi.ParamNames is null ? t : t + " " + mi.ParamName(i)))})",
                    Section = "Raw: " + k.Image,
                });
            }
        }
    }
}
