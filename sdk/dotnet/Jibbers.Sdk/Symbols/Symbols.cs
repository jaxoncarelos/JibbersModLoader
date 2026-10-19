using System.Text.Json;
using System.Text.Json.Nodes;
using Jibbers.Sdk.Interop;

namespace Jibbers.Sdk.Symbols;

/// <summary>
/// Stable names for the game's obfuscated classes and members, resolved on the running build from
/// the built-in catalogue (<c>symbols.json</c>). Usually you use the generated <c>Game.*</c>
/// bindings, which sit on top of this; use it directly for symbols you <see cref="Register"/>.
/// </summary>
/// <remarks>Lookups read metadata only (any thread). Reading and writing objects is main thread only.</remarks>
public static class SymbolTable
{
    sealed class LiveState
    {
        public required Snapshot Snap;
        public required List<Il2CppClass> Classes;
        public required List<List<Il2CppField>> Fields;
        public required List<List<Il2CppMethod>> Methods;
        public required HashSet<string> Images;
    }

    static readonly object gate = new();
    static readonly List<Symbol> extra = new();
    static LiveState? live;
    static Catalogue? cat;
    static Resolution? res;

    /// <summary>The built-in catalogue's JSON.</summary>
    public static string BuiltInJson
    {
        get
        {
            using var s = typeof(SymbolTable).Assembly.GetManifestResourceStream("Jibbers.Sdk.symbols.json")!;
            using var r = new StreamReader(s);
            return r.ReadToEnd();
        }
    }

    static Catalogue Catalogue()
    {
        var c = Symbols.Catalogue.Parse(BuiltInJson);
        c.Symbols.AddRange(extra);
        return c;
    }

    /// <summary>Adds symbols (a catalogue in the <c>symbols.json</c> format). Prefix ids with your mod id.</summary>
    public static void Register(string catalogueJson)
    {
        var add = Symbols.Catalogue.Parse(catalogueJson);
        lock (gate)
        {
            var all = Catalogue();
            all.Symbols.AddRange(add.Symbols);
            var errs = Shape.Lint(all);
            if (errs.Count > 0)
            {
                throw new ArgumentException("register: " + string.Join("; ", errs));
            }
            extra.AddRange(add.Symbols);
            cat = null;
            res = null;
        }
    }

    /// <summary>Reads one class's declared members.</summary>
    internal static (ClassInfo, List<Il2CppField>, List<Il2CppMethod>) Read(string image, Il2CppClass k)
    {
        var info = new ClassInfo
        {
            Image = image,
            Namespace = k.Namespace,
            Name = k.Name,
            FullName = k.FullName,
            Parent = k.Parent.IsNull ? "" : k.Parent.FullName,
            Declaring = k.DeclaringType.IsNull ? "" : k.DeclaringType.FullName,
            ValueType = k.IsValueType,
            Interfaces = k.Interfaces.Select(i => i.FullName).ToList(),
        };
        var fields = k.Fields.ToList();
        foreach (var f in fields)
        {
            info.Fields.Add(new FieldInfo { Name = f.Name, Type = f.TypeName, Static = f.IsStatic, Offset = f.IsStatic ? 0 : f.Offset });
        }
        var methods = k.Methods.ToList();
        foreach (var m in methods)
        {
            info.Methods.Add(new MethodInfo { Name = m.Name, Params = m.ParameterTypes.ToList(), Return = m.ReturnType, Static = m.IsStatic });
        }
        return (info, fields, methods);
    }

    static LiveState ReadLive(HashSet<string> images)
    {
        var l = new LiveState { Snap = new Snapshot { GameBuild = Bootstrap.GameBuild }, Classes = new(), Fields = new(), Methods = new(), Images = images };
        foreach (var (_, name) in Il2Cpp.Assemblies())
        {
            if (!images.Contains(name))
            {
                continue;
            }
            foreach (var k in Il2Cpp.Classes(name))
            {
                if (k.IsNull)
                {
                    continue;
                }
                var (info, f, m) = Read(name, k);
                l.Snap.Classes.Add(info);
                l.Classes.Add(k);
                l.Fields.Add(f);
                l.Methods.Add(m);
            }
        }
        return l;
    }

    static (LiveState, Catalogue, Resolution) State()
    {
        lock (gate)
        {
            cat ??= Catalogue();
            var want = cat.Symbols.Select(s => s.Class.Image ?? Shape.GameImage).Append(Shape.GameImage).ToHashSet();
            if (live is null || !want.IsSubsetOf(live.Images))
            {
                live = ReadLive(want);
                res = null;
            }
            if (res is null)
            {
                res = Shape.Resolve(cat, live.Snap);
                foreach (var (id, f) in res.Failed)
                {
                    Log.Info($"symbols: {id} unresolved: {f}");
                }
            }
            return (live, cat, res);
        }
    }

    static string Why(Resolution r, Catalogue c, string id) =>
        r.Failed.TryGetValue(id, out var f) ? $"symbol {id} unresolved on this game build: {f}"
        : c.Symbols.Any(s => s.Id == id) ? $"symbol {id} unresolved" : $"unknown symbol {id}";

    static (string Id, string Member) Split(string path)
    {
        var dot = path.LastIndexOf('.');
        return dot <= 0 ? throw new ArgumentException($"{path} is not <symbol>.<member>") : (path[..dot], path[(dot + 1)..]);
    }

    /// <summary>The class a symbol resolves to. Throws <see cref="SymbolException"/> if it does not resolve.</summary>
    public static Il2CppClass Class(string id)
    {
        var (l, c, r) = State();
        return r.Ok.TryGetValue(id, out var ok) ? l.Classes[ok.Class] : throw new SymbolException(Why(r, c, id));
    }

    /// <summary>Whether a symbol resolves on this build.</summary>
    public static bool Resolves(string id) => State().Item3.Ok.ContainsKey(id);

    /// <summary>The metadata of a symbol's class (real names, types, offsets).</summary>
    public static ClassInfo Info(string id)
    {
        var (l, c, r) = State();
        return r.Ok.TryGetValue(id, out var ok) ? l.Snap.Classes[ok.Class] : throw new SymbolException(Why(r, c, id));
    }

    static (LiveState, Member) Resolve(string path)
    {
        var (id, member) = Split(path);
        var (l, c, r) = State();
        if (!r.Ok.TryGetValue(id, out var ok))
        {
            throw new SymbolException(Why(r, c, id));
        }
        return ok.Members.TryGetValue(member, out var m) ? (l, m) : throw new SymbolException($"symbol {id} has no member {member}");
    }

    /// <summary>A field by <c>&lt;symbol&gt;.&lt;member&gt;</c>.</summary>
    public static Il2CppField Field(string path)
    {
        var (l, m) = Resolve(path);
        return m.IsField ? l.Fields[m.Class][m.Index] : throw new SymbolException($"{path} is a method, not a field");
    }

    /// <summary>A method by <c>&lt;symbol&gt;.&lt;member&gt;</c>.</summary>
    public static Il2CppMethod Method(string path)
    {
        var (l, m) = Resolve(path);
        return !m.IsField ? l.Methods[m.Class][m.Index] : throw new SymbolException($"{path} is a field, not a method");
    }

    /// <summary>The real (obfuscated) name of a symbol's class or member on this build.</summary>
    public static string RealName(string path)
    {
        var (l, _, r) = State();
        if (r.Ok.TryGetValue(path, out var ok))
        {
            return l.Snap.Classes[ok.Class].FullName;
        }
        var (live2, m) = Resolve(path);
        return Shape.MemberName(live2.Snap, m);
    }

    /// <summary>Live instances of a symbol's class (components and other Unity objects). Main thread.</summary>
    public static nint[] Live(string id) => Unity.FindObjects(Class(id));

    /// <summary>Every symbol's status on the running build.</summary>
    public static JsonObject Report()
    {
        var (l, c, r) = State();
        var syms = new JsonObject();
        foreach (var s in c.Symbols)
        {
            if (r.Ok.TryGetValue(s.Id, out var ok))
            {
                var members = new JsonObject();
                foreach (var (n, m) in ok.Members)
                {
                    members[n] = Shape.MemberName(l.Snap, m);
                }
                syms[s.Id] = new JsonObject { ["class"] = l.Snap.Classes[ok.Class].FullName, ["members"] = members };
            }
            else
            {
                syms[s.Id] = new JsonObject { ["error"] = r.Failed.TryGetValue(s.Id, out var f) ? f.ToString() : "unresolved" };
            }
        }
        return new JsonObject { ["game_build"] = l.Snap.GameBuild, ["resolved"] = r.Ok.Count, ["failed"] = r.Failed.Count, ["symbols"] = syms };
    }

    /// <summary>Writes every class of <paramref name="images"/> as a dump file (the generator's input). Returns the class count.</summary>
    public static int Dump(string path, params string[] images)
    {
        var set = (images.Length == 0 ? new[] { Shape.GameImage } : images).ToHashSet();
        var l = ReadLive(set);
        File.WriteAllText(path, JsonSerializer.Serialize(l.Snap));
        return l.Snap.Classes.Count;
    }
}

/// <summary>A symbol that does not resolve on the running game build.</summary>
public sealed class SymbolException(string message) : Exception(message);

/// <summary>A class symbol, resolved once and cached.</summary>
public sealed class ClassRef(string id)
{
    Il2CppClass k;
    /// <summary>The symbol id.</summary>
    public string Id { get; } = id;
    /// <summary>The class (resolved on first use).</summary>
    public Il2CppClass Class => k.IsNull ? (k = SymbolTable.Class(Id)) : k;
    /// <summary>Whether it resolves on this build.</summary>
    public bool Resolves => !k.IsNull || SymbolTable.Resolves(Id);
    /// <summary>Whether <paramref name="obj"/> is an instance of this class (or a subclass).</summary>
    public bool IsInstance(nint obj) => obj != 0 && Class.IsAssignableFrom(Il2Cpp.ClassOf(obj));
}

/// <summary>A field symbol member, resolved once and cached.</summary>
public sealed class FieldRef(string path)
{
    Il2CppField f;
    /// <summary><c>&lt;symbol&gt;.&lt;member&gt;</c>.</summary>
    public string Path { get; } = path;
    /// <summary>The field (resolved on first use).</summary>
    public Il2CppField Field => f.IsNull ? (f = SymbolTable.Field(Path)) : f;

    void Init()
    {
        if (Field.IsStatic)
        {
            SymbolTable.Class(Path[..Path.LastIndexOf('.')]).Init();
        }
    }

    /// <summary>Reads a value field (obj ignored for static fields).</summary>
    public T Get<T>(nint obj) where T : unmanaged
    {
        Init();
        return Field.Get<T>(obj);
    }

    /// <summary>Writes a value field.</summary>
    public void Set<T>(nint obj, T value) where T : unmanaged
    {
        Init();
        Field.Set(obj, value);
    }

    /// <summary>Reads an object field.</summary>
    public nint GetObject(nint obj)
    {
        Init();
        return Field.GetObject(obj);
    }

    /// <summary>Writes an object field.</summary>
    public void SetObject(nint obj, nint value)
    {
        Init();
        Field.SetObject(obj, value);
    }

    /// <summary>Reads a string field.</summary>
    public string GetString(nint obj) => Il2Cpp.ReadString(GetObject(obj));

    /// <summary>Writes a string field.</summary>
    public void SetString(nint obj, string? value) => SetObject(obj, value is null ? 0 : Il2Cpp.String(value));

    /// <summary>Byte offset of an instance field inside a struct's data (object header removed).</summary>
    public int StructOffset => Field.Offset - 16;
}

/// <summary>A method symbol member, resolved once and cached.</summary>
public sealed class MethodRef(string path)
{
    Il2CppMethod m;
    /// <summary><c>&lt;symbol&gt;.&lt;member&gt;</c>.</summary>
    public string Path { get; } = path;
    /// <summary>The method (resolved on first use).</summary>
    public Il2CppMethod Method => m.IsNull ? (m = SymbolTable.Method(Path)) : m;
    /// <summary>The method's native code, for <see cref="Detour"/>.</summary>
    public nint NativeCode => Method.NativeCode;

    /// <summary>Calls the method (virtual on instance methods). Arguments as for <see cref="Il2CppMethod.Invoke"/>.</summary>
    public nint Invoke(nint self, params nint[] args)
    {
        var target = Method.IsStatic ? Method : Method.Virtual(self);
        if (!Method.IsStatic && self == 0)
        {
            throw new NullReferenceException($"{Path} on a null object");
        }
        return target.Invoke(self, args);
    }

    /// <summary>Calls the method and unboxes its value-type result.</summary>
    public T Invoke<T>(nint self, params nint[] args) where T : unmanaged
    {
        var r = Invoke(self, args);
        return r == 0 ? throw new InvalidOperationException($"{Path} returned null") : Il2Cpp.Unbox<T>(r);
    }

    /// <summary>Pre-hooks the method (see <see cref="Hooks.Before"/>).</summary>
    public void Before(Hooks.PreHook callback) => Hooks.Before(Method, callback);

    /// <summary>Detours the method's code (see <see cref="Detour.Create(nint, nint)"/>).</summary>
    public Detour Detour(nint replacement) => Sdk.Detour.Create(NativeCode, replacement);
}

/// <summary>
/// A class by its real name (raw bindings). Unlike <see cref="ClassRef"/> it does not survive renames:
/// obfuscated names change with game builds.
/// </summary>
public sealed class RawClassRef(string image, string ns, string name)
{
    Il2CppClass k;
    /// <summary>Image (<c>Assembly-CSharp.dll</c>).</summary>
    public string Image { get; } = image;
    /// <summary>Namespace.</summary>
    public string Namespace { get; } = ns;
    /// <summary>Name; nested classes as <c>Outer/Inner</c>.</summary>
    public string Name { get; } = name;

    /// <summary>The class (resolved on first use).</summary>
    public Il2CppClass Class
    {
        get
        {
            if (!k.IsNull)
            {
                return k;
            }
            var parts = Name.Split('/');
            var c = Il2Cpp.Class(Image, Namespace, parts[0]);
            foreach (var inner in parts.Skip(1))
            {
                c = c.IsNull ? c : c.NestedTypes.FirstOrDefault(n => n.Name == inner);
            }
            return c.IsNull ? throw new SymbolException($"class {Namespace}.{Name} not found in {Image} (renamed in this game build?)") : (k = c);
        }
    }

    /// <summary>Live instances (Unity objects). Main thread.</summary>
    public nint[] Live() => Unity.FindObjects(Class);
}

/// <summary>A field by real name (raw bindings).</summary>
public sealed class RawFieldRef(RawClassRef owner, string name)
{
    Il2CppField f;
    /// <summary>The field (resolved on first use).</summary>
    public Il2CppField Field => !f.IsNull ? f : (f = Find());

    Il2CppField Find()
    {
        var x = owner.Class.Field(name);
        return x.IsNull ? throw new SymbolException($"field {owner.Name}.{name} not found (renamed in this game build?)") : x;
    }

    void Init()
    {
        if (Field.IsStatic)
        {
            owner.Class.Init();
        }
    }

    /// <summary>Reads a value field.</summary>
    public T Get<T>(nint obj) where T : unmanaged
    {
        Init();
        return Field.Get<T>(obj);
    }

    /// <summary>Writes a value field.</summary>
    public void Set<T>(nint obj, T v) where T : unmanaged
    {
        Init();
        Field.Set(obj, v);
    }

    /// <summary>Reads an object field.</summary>
    public nint GetObject(nint obj)
    {
        Init();
        return Field.GetObject(obj);
    }

    /// <summary>Writes an object field.</summary>
    public void SetObject(nint obj, nint v)
    {
        Init();
        Field.SetObject(obj, v);
    }

    /// <summary>Reads a string field.</summary>
    public string GetString(nint obj) => Il2Cpp.ReadString(GetObject(obj));

    /// <summary>Writes a string field.</summary>
    public void SetString(nint obj, string? v) => SetObject(obj, v is null ? 0 : Il2Cpp.String(v));
}

/// <summary>A method by real name and parameter types (raw bindings).</summary>
public sealed class RawMethodRef(RawClassRef owner, string name, params string[] parameterTypes)
{
    Il2CppMethod m;
    /// <summary>The method (resolved on first use).</summary>
    public Il2CppMethod Method => !m.IsNull ? m : (m = Find());
    /// <summary>The method's native code, for <see cref="Detour"/>.</summary>
    public nint NativeCode => Method.NativeCode;

    Il2CppMethod Find()
    {
        foreach (var x in owner.Class.Methods)
        {
            if (x.Name == name && x.ParameterTypes.SequenceEqual(parameterTypes))
            {
                return x;
            }
        }
        throw new SymbolException($"method {owner.Name}.{name}({string.Join(", ", parameterTypes)}) not found (renamed in this game build?)");
    }

    /// <summary>Calls the method (virtual on instance methods).</summary>
    public nint Invoke(nint self, params nint[] args) => (Method.IsStatic ? Method : Method.Virtual(self)).Invoke(self, args);

    /// <summary>Calls and unboxes a value-type result.</summary>
    public T Invoke<T>(nint self, params nint[] args) where T : unmanaged
    {
        var r = Invoke(self, args);
        return r == 0 ? throw new InvalidOperationException($"{name} returned null") : Il2Cpp.Unbox<T>(r);
    }
}
