using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jibbers.Sdk.Symbols;

// The game's metadata, as read from IL2CPP or a dump file (the format of sdk.symbols.dump).

/// <summary>Every class of one or more images.</summary>
public sealed class Snapshot
{
    /// <summary>Fingerprint of the build the snapshot came from.</summary>
    [JsonPropertyName("game_build")] public string GameBuild { get; set; } = "";
    /// <summary>The classes.</summary>
    [JsonPropertyName("classes")] public List<ClassInfo> Classes { get; set; } = new();
}

/// <summary>One class's metadata.</summary>
public sealed class ClassInfo
{
    [JsonPropertyName("image")] public string Image { get; set; } = "";
    [JsonPropertyName("namespace")] public string Namespace { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    /// <summary>IL2CPP type name, the form field and parameter types use.</summary>
    [JsonPropertyName("full_name")] public string FullName { get; set; } = "";
    [JsonPropertyName("parent")] public string Parent { get; set; } = "";
    [JsonPropertyName("declaring")] public string Declaring { get; set; } = "";
    [JsonPropertyName("valuetype")] public bool ValueType { get; set; }
    [JsonPropertyName("interfaces")] public List<string> Interfaces { get; set; } = new();
    [JsonPropertyName("fields")] public List<FieldInfo> Fields { get; set; } = new();
    [JsonPropertyName("methods")] public List<MethodInfo> Methods { get; set; } = new();
}

/// <summary>A field's metadata.</summary>
public sealed class FieldInfo
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("type")] public string Type { get; set; } = "";
    [JsonPropertyName("static")] public bool Static { get; set; }
    [JsonPropertyName("offset")] public int Offset { get; set; }
}

/// <summary>A method's metadata.</summary>
public sealed class MethodInfo
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("params")] public List<string> Params { get; set; } = new();
    [JsonPropertyName("return")] public string Return { get; set; } = "";
    [JsonPropertyName("static")] public bool Static { get; set; }
    /// <summary>Parameter names, when known (assemblies read offline; IL2CPP dumps leave them out).</summary>
    [JsonPropertyName("param_names")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public List<string>? ParamNames { get; set; }

    /// <summary>A C# parameter name for parameter <paramref name="i"/>: the real one when known and safe, else <c>a{i}</c>.</summary>
    public string ParamName(int i)
    {
        var n = ParamNames is { } p && i < p.Count ? p[i] : "";
        return n.Length > 0 && (char.IsLetter(n[0]) || n[0] == '_') && n.All(c => char.IsLetterOrDigit(c) || c == '_') && !n.StartsWith("__") && n is not ("Ptr" or "Class" or "IsNull") && !System.Text.RegularExpressions.Regex.IsMatch(n, @"^[ap]\d+$")
            && (ParamNames!.IndexOf(n) == i) ? n : "a" + i;
    }
}

// Rules (the symbols.json format).

/// <summary>The catalogue file.</summary>
public sealed class Catalogue
{
    [JsonPropertyName("symbols")] public List<Symbol> Symbols { get; set; } = new();

    /// <summary>Parses a catalogue.</summary>
    public static Catalogue Parse(string json) => JsonSerializer.Deserialize<Catalogue>(json) ?? new Catalogue();
}

/// <summary>One game class and the members mods use, by stable name.</summary>
public sealed class Symbol
{
    /// <summary>Stable dotted id (<c>map.importer</c>).</summary>
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("summary")] public string Summary { get; set; } = "";
    [JsonPropertyName("tags")] public List<string> Tags { get; set; } = new();
    [JsonPropertyName("notes")] public string Notes { get; set; } = "";
    [JsonPropertyName("match")] public ClassRule Class { get; set; } = new();
    /// <summary>Member name to rule; addressed as <c>&lt;id&gt;.&lt;member&gt;</c>.</summary>
    [JsonPropertyName("members")] public Dictionary<string, MemberRule> Members { get; set; } = new();
}

/// <summary>Conditions on a class. Absent conditions match anything.</summary>
public sealed class ClassRule
{
    [JsonPropertyName("image")] public string? Image { get; set; }
    [JsonPropertyName("namespace")] public string? Namespace { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("obfuscated")] public bool? Obfuscated { get; set; }
    [JsonPropertyName("parent")] public string? Parent { get; set; }
    [JsonPropertyName("declaring")] public string? Declaring { get; set; }
    [JsonPropertyName("valuetype")] public bool? ValueType { get; set; }
    [JsonPropertyName("interfaces")] public List<string> Interfaces { get; set; } = new();
    [JsonPropertyName("fields")] public List<FieldRule> Fields { get; set; } = new();
    [JsonPropertyName("methods")] public List<MethodRule> Methods { get; set; } = new();
    [JsonPropertyName("instance_fields")] public int[]? InstanceFields { get; set; }
}

/// <summary>Conditions on a field.</summary>
public sealed class FieldRule
{
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("obfuscated")] public bool? Obfuscated { get; set; }
    [JsonPropertyName("backing")] public bool? Backing { get; set; }
    [JsonPropertyName("type")] public string? Type { get; set; }
    [JsonPropertyName("static")] public bool? Static { get; set; }
    [JsonPropertyName("nth")] public int? Nth { get; set; }
    [JsonPropertyName("inherited")] public bool Inherited { get; set; }
}

/// <summary>Conditions on a method.</summary>
public sealed class MethodRule
{
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("obfuscated")] public bool? Obfuscated { get; set; }
    [JsonPropertyName("params")] public List<string>? Params { get; set; }
    [JsonPropertyName("argc")] public int? Argc { get; set; }
    [JsonPropertyName("return")] public string? Return { get; set; }
    [JsonPropertyName("static")] public bool? Static { get; set; }
    [JsonPropertyName("nth")] public int? Nth { get; set; }
    [JsonPropertyName("inherited")] public bool Inherited { get; set; }
}

/// <summary>A member rule: exactly one of <see cref="Field"/> or <see cref="Method"/>.</summary>
public sealed class MemberRule
{
    [JsonPropertyName("field")] public FieldRule? Field { get; set; }
    [JsonPropertyName("method")] public MethodRule? Method { get; set; }
}

// Results.

/// <summary>A resolved member: the declaring class (the symbol's class or a parent) and its index.</summary>
public readonly record struct Member(int Class, bool IsField, int Index);

/// <summary>A resolved symbol.</summary>
public sealed record Resolved(int Class, IReadOnlyDictionary<string, Member> Members);

/// <summary>Why a symbol did not resolve.</summary>
public sealed record Failure(string Status, string Detail)
{
    /// <inheritdoc/>
    public override string ToString() => Detail.Length == 0 ? Status : $"{Status}: {Detail}";
}

/// <summary>Outcome of resolving a catalogue.</summary>
public sealed class Resolution
{
    public Dictionary<string, Resolved> Ok { get; } = new();
    public Dictionary<string, Failure> Failed { get; } = new();

    /// <summary>Full name of the class a symbol resolved to.</summary>
    public string? ClassName(Snapshot s, string id) => Ok.TryGetValue(id, out var r) ? s.Classes[r.Class].FullName : null;
}

/// <summary>
/// Finds obfuscated game classes and members by shape (field types, signatures, parents) instead of
/// their names, which change every build. Pure logic: runs offline on dump files too.
/// </summary>
public static class Shape
{
    /// <summary>Default image for game classes.</summary>
    public const string GameImage = "Assembly-CSharp.dll";

    /// <summary>Two or three lowercase letters (a property backing field counts by its inner name).</summary>
    public static bool IsObfuscated(string name)
    {
        var n = name;
        if (n.StartsWith('<') && n.EndsWith(">k__BackingField"))
        {
            n = n[1..^">k__BackingField".Length];
        }
        return n.Length is 2 or 3 && n.All(c => c is >= 'a' and <= 'z');
    }

    /// <summary>Resolves every symbol of <paramref name="cat"/> against <paramref name="snap"/>.</summary>
    public static Resolution Resolve(Catalogue cat, Snapshot snap)
    {
        var r = new Resolver(cat, snap);
        foreach (var s in cat.Symbols)
        {
            try
            {
                r.Symbol(s.Id);
            }
            catch (Fail)
            {
                // Recorded in Out.Failed.
            }
        }
        return r.Out;
    }

    /// <summary>The real (obfuscated) name of a resolved member.</summary>
    public static string MemberName(Snapshot snap, Member m)
    {
        var k = snap.Classes[m.Class];
        if (m.IsField)
        {
            return k.Fields[m.Index].Name;
        }
        var mi = k.Methods[m.Index];
        return $"{mi.Name}({string.Join(", ", mi.Params)})";
    }

    sealed class Fail(Failure f) : Exception(f.ToString())
    {
        public Failure F { get; } = f;
    }

    sealed class Resolver
    {
        readonly Snapshot snap;
        readonly Dictionary<string, Symbol> byId;
        readonly Dictionary<string, int> byName = new();
        readonly HashSet<string> active = new();
        readonly Dictionary<string, int> picked = new();
        public readonly Resolution Out = new();

        public Resolver(Catalogue cat, Snapshot snap)
        {
            this.snap = snap;
            byId = new Dictionary<string, Symbol>();
            foreach (var s in cat.Symbols)
            {
                byId.TryAdd(s.Id, s);
            }
            for (var i = 0; i < snap.Classes.Count; i++)
            {
                byName.TryAdd(snap.Classes[i].FullName, i);
            }
        }

        /// <summary>Full class name of a symbol, resolving it on demand; throws <see cref="Fail"/>.</summary>
        public string Symbol(string id)
        {
            if (Out.Ok.TryGetValue(id, out var ok))
            {
                return snap.Classes[ok.Class].FullName;
            }
            if (Out.Failed.TryGetValue(id, out var f))
            {
                throw new Fail(f);
            }
            if (!byId.TryGetValue(id, out var sym))
            {
                throw new Fail(new Failure("unknown_symbol", id));
            }
            if (!active.Add(id))
            {
                // A member may refer to a symbol whose class is already chosen.
                return picked.TryGetValue(id, out var k) ? snap.Classes[k].FullName : throw new Fail(new Failure("cycle", id));
            }
            try
            {
                var res = Match(sym);
                Out.Ok[id] = res;
                return snap.Classes[res.Class].FullName;
            }
            catch (Fail e)
            {
                Out.Failed[id] = e.F;
                throw;
            }
            finally
            {
                active.Remove(id);
                picked.Remove(id);
            }
        }

        string Expand(string pattern)
        {
            var sb = new System.Text.StringBuilder();
            var i = 0;
            while (i < pattern.Length)
            {
                var at = pattern.IndexOf('@', i);
                if (at < 0)
                {
                    sb.Append(pattern, i, pattern.Length - i);
                    break;
                }
                sb.Append(pattern, i, at - i);
                var end = at + 1;
                while (end < pattern.Length && (char.IsLetterOrDigit(pattern[end]) || pattern[end] is '_' or '.'))
                {
                    end++;
                }
                var id = pattern[(at + 1)..end].TrimEnd('.');
                string name;
                try
                {
                    name = Symbol(id);
                }
                catch (Fail)
                {
                    throw new Fail(new Failure("dependency", id));
                }
                sb.Append(name);
                i = at + 1 + id.Length;
            }
            return sb.ToString();
        }

        bool TypeOk(string? pattern, string actual) => pattern is null || pattern == "*" || Expand(pattern) == actual;

        bool FieldOk(FieldRule r, FieldInfo f) =>
            (r.Name is null || r.Name == f.Name)
            && (r.Obfuscated is null || r.Obfuscated == IsObfuscated(f.Name))
            && (r.Backing is null || r.Backing == f.Name.EndsWith(">k__BackingField"))
            && (r.Static is null || r.Static == f.Static)
            && TypeOk(r.Type, f.Type);

        bool MethodOk(MethodRule r, MethodInfo m)
        {
            if (!((r.Name is null || r.Name == m.Name)
                  && (r.Obfuscated is null || r.Obfuscated == IsObfuscated(m.Name))
                  && (r.Static is null || r.Static == m.Static)
                  && (r.Argc is null || r.Argc == m.Params.Count)))
            {
                return false;
            }
            if (r.Params is not null)
            {
                if (r.Params.Count != m.Params.Count)
                {
                    return false;
                }
                for (var i = 0; i < r.Params.Count; i++)
                {
                    if (!TypeOk(r.Params[i], m.Params[i]))
                    {
                        return false;
                    }
                }
            }
            return TypeOk(r.Return, m.Return);
        }

        List<int> Chain(int k, bool inherited)
        {
            var list = new List<int> { k };
            while (inherited && byName.TryGetValue(snap.Classes[list[^1]].Parent, out var p) && !list.Contains(p))
            {
                list.Add(p);
            }
            return list;
        }

        List<Member> Matching(int k, MemberRule rule)
        {
            var v = new List<Member>();
            if (rule.Field is { } fr)
            {
                foreach (var c in Chain(k, fr.Inherited))
                {
                    var fs = snap.Classes[c].Fields;
                    for (var i = 0; i < fs.Count; i++)
                    {
                        if (FieldOk(fr, fs[i]))
                        {
                            v.Add(new Member(c, true, i));
                        }
                    }
                }
            }
            else if (rule.Method is { } mr)
            {
                foreach (var c in Chain(k, mr.Inherited))
                {
                    var ms = snap.Classes[c].Methods;
                    for (var i = 0; i < ms.Count; i++)
                    {
                        if (MethodOk(mr, ms[i]))
                        {
                            v.Add(new Member(c, false, i));
                        }
                    }
                }
            }
            return v;
        }

        bool ClassOk(ClassRule r, int ki)
        {
            var k = snap.Classes[ki];
            if (k.Image != (r.Image ?? GameImage)
                || (r.Namespace is not null && r.Namespace != k.Namespace)
                || (r.Name is not null && r.Name != k.Name)
                || (r.Obfuscated is not null && r.Obfuscated != IsObfuscated(k.Name))
                || (r.ValueType is not null && r.ValueType != k.ValueType))
            {
                return false;
            }
            if (r.InstanceFields is [var lo, var hi])
            {
                var n = k.Fields.Count(f => !f.Static);
                if (n < lo || n > hi)
                {
                    return false;
                }
            }
            if (!TypeOk(r.Parent, k.Parent) || !TypeOk(r.Declaring, k.Declaring))
            {
                return false;
            }
            if (r.Interfaces.Any(i => !k.Interfaces.Contains(Expand(i))))
            {
                return false;
            }
            return r.Fields.All(f => Matching(ki, new MemberRule { Field = f }).Count > 0)
                && r.Methods.All(m => Matching(ki, new MemberRule { Method = m }).Count > 0);
        }

        Resolved Match(Symbol sym)
        {
            var hits = new List<int>();
            for (var i = 0; i < snap.Classes.Count; i++)
            {
                if (ClassOk(sym.Class, i))
                {
                    hits.Add(i);
                }
            }
            if (hits.Count == 0)
            {
                throw new Fail(new Failure("not_found", ""));
            }
            if (hits.Count > 1)
            {
                throw new Fail(new Failure("ambiguous", string.Join(", ", hits.Select(h => snap.Classes[h].FullName))));
            }
            var cls = hits[0];
            picked[sym.Id] = cls;
            var members = new Dictionary<string, Member>();
            var errors = new List<string>();
            foreach (var (name, rule) in sym.Members)
            {
                var found = Matching(cls, rule);
                var nth = rule.Field?.Nth ?? rule.Method?.Nth;
                if (nth is { } n)
                {
                    if (n < found.Count)
                    {
                        members[name] = found[n];
                    }
                    else
                    {
                        errors.Add($"{name}: only {found.Count} matches, wanted #{n}");
                    }
                }
                else if (found.Count == 1)
                {
                    members[name] = found[0];
                }
                else
                {
                    errors.Add(found.Count == 0 ? $"{name}: no match" : $"{name}: {found.Count} matches: {string.Join(", ", found.Select(m => MemberName(snap, m)))}");
                }
            }
            if (errors.Count > 0)
            {
                throw new Fail(new Failure("members", $"class {snap.Classes[cls].FullName}, but {string.Join("; ", errors)}"));
            }
            return new Resolved(cls, members);
        }
    }

    /// <summary>Mistakes that need no game: bad ids, duplicates, unknown <c>@</c> references, empty rules.</summary>
    public static List<string> Lint(Catalogue cat)
    {
        var errs = new List<string>();
        var ids = cat.Symbols.Select(s => s.Id).ToHashSet();
        var seen = new HashSet<string>();
        foreach (var s in cat.Symbols)
        {
            if (s.Id.Length == 0 || !s.Id.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '_' or '.'))
            {
                errs.Add($"{s.Id}: id must be lowercase letters, digits, '_' and '.'");
            }
            if (!seen.Add(s.Id))
            {
                errs.Add($"{s.Id}: duplicate id");
            }
            if (string.IsNullOrWhiteSpace(s.Summary))
            {
                errs.Add($"{s.Id}: missing summary");
            }
            var c = s.Class;
            if (c.Name is null && c.Fields.Count == 0 && c.Methods.Count == 0 && c.Parent is null)
            {
                errs.Add($"{s.Id}: class rule has no name, parent, fields or methods; it would match anything");
            }
            var patterns = new List<string?> { c.Parent, c.Declaring };
            patterns.AddRange(c.Interfaces);
            patterns.AddRange(c.Fields.Select(f => f.Type));
            foreach (var m in c.Methods)
            {
                patterns.Add(m.Return);
                patterns.AddRange(m.Params ?? new List<string>());
            }
            foreach (var (name, m) in s.Members)
            {
                if (m.Field is { } f)
                {
                    if (f.Name is null && f.Type is null && f.Backing is null)
                    {
                        errs.Add($"{s.Id}.{name}: field rule needs a name or a type");
                    }
                    patterns.Add(f.Type);
                }
                else if (m.Method is { } mr)
                {
                    if (mr.Name is null && mr.Params is null && mr.Return is null)
                    {
                        errs.Add($"{s.Id}.{name}: method rule needs a name, params or a return type");
                    }
                    patterns.Add(mr.Return);
                    patterns.AddRange(mr.Params ?? new List<string>());
                }
                else
                {
                    errs.Add($"{s.Id}.{name}: member is neither a field nor a method");
                }
            }
            foreach (var p in patterns.Where(p => p is not null))
            {
                foreach (var part in p!.Split('@').Skip(1))
                {
                    var end = 0;
                    while (end < part.Length && (char.IsLetterOrDigit(part[end]) || part[end] is '_' or '.'))
                    {
                        end++;
                    }
                    var id = part[..end].TrimEnd('.');
                    if (!ids.Contains(id))
                    {
                        errs.Add($"{s.Id}: refers to unknown symbol @{id}");
                    }
                }
            }
        }
        return errs;
    }
}
