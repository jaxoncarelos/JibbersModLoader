using Jibbers.Sdk.Symbols;

namespace Jibbers.Sdk.Generator;

/// <summary>How a game type appears in C#.</summary>
internal enum Kind
{
    Void,
    /// <summary>A blittable value with a C# mirror (int, float, Vector3...).</summary>
    Value,
    String,
    /// <summary>A game or engine class with a generated wrapper.</summary>
    Wrapper,
    /// <summary>A known reference type without a wrapper (arrays, collections, delegates): <c>nint</c>.</summary>
    Object,
    /// <summary>A struct without a mirror, a byref or a pointer: <c>nint</c> pointing at the value.</summary>
    Pointer,
    /// <summary>Not in the dumps: reachable only through the raw field/method reference.</summary>
    Unknown,
}

internal sealed record CsType(Kind Kind, string Cs);

/// <summary>A raw-bound class.</summary>
internal sealed class RawType
{
    public required ClassInfo Info;
    public required string Namespace;
    public required string Name;
    /// <summary>Name for <c>RawClassRef</c>: <c>Outer/Inner</c> for nested classes.</summary>
    public required string RefName;
    public string Global => $"global::{Namespace}.{Name}";
}

/// <summary>A resolved catalogue symbol.</summary>
internal sealed class FriendlyType
{
    public required Symbol Symbol;
    public required Resolved Resolved;
    public required ClassInfo Info;
    public required string Namespace;
    public required string Name;
    public bool IsEvent => Info.ValueType && Symbol.Tags.Contains("event");
    public string Global => $"global::{Namespace}.{Name}";
}

internal sealed class Model
{
    public readonly Catalogue Catalogue;
    public readonly Snapshot Snap;
    public readonly Resolution Resolution;
    public readonly List<RawType> RawClasses = new();
    public readonly List<FriendlyType> Friendly = new();
    public readonly Dictionary<string, ClassInfo> ByName = new();
    readonly Dictionary<string, RawType> rawByName = new();
    readonly Dictionary<string, FriendlyType> friendlyByName = new();

    /// <summary>Classes the bindings leave out on purpose: networking (see docs/SYMBOLS.md, "Out of scope").</summary>
    public static bool Excluded(ClassInfo k) =>
        k.Namespace.StartsWith("FishNet") || k.Name.StartsWith("NetBus") || k.FullName.StartsWith("NetBus")
        || k.Name.EndsWith("Broadcast") || k.Name is "OfficialServers" || k.Image.StartsWith("FishNet");

    /// <summary>Compiler-generated and generic classes have no usable binding.</summary>
    public static bool Bindable(ClassInfo k) => !k.FullName.Any(c => c is '<' or '>' or '{' or '}' or '`' or '|' or '=' or '-' or ' ' or ',');

    static readonly string[] KnownRefs =
    {
        "UnityEngine.Object", "UnityEngine.GameObject", "UnityEngine.Component", "UnityEngine.Transform", "UnityEngine.RectTransform",
        "UnityEngine.MonoBehaviour", "UnityEngine.Behaviour", "UnityEngine.Camera", "UnityEngine.Material", "UnityEngine.Mesh",
        "UnityEngine.Texture", "UnityEngine.Texture2D", "UnityEngine.RenderTexture", "UnityEngine.Terrain", "UnityEngine.TerrainData",
        "UnityEngine.Renderer", "UnityEngine.MeshRenderer", "UnityEngine.SkinnedMeshRenderer", "UnityEngine.MeshFilter", "UnityEngine.Rigidbody",
        "UnityEngine.Collider", "UnityEngine.Canvas", "UnityEngine.CanvasGroup", "UnityEngine.AudioSource", "UnityEngine.AudioClip",
        "UnityEngine.Animator", "UnityEngine.Light", "UnityEngine.Shader", "UnityEngine.Sprite", "UnityEngine.ScriptableObject",
        "UnityEngine.LODGroup", "UnityEngine.ParticleSystem", "UnityEngine.UI.Image", "UnityEngine.UI.Slider", "UnityEngine.UI.RawImage",
        "TMPro.TMP_Text", "TMPro.TextMeshProUGUI", "TMPro.TMP_InputField", "DG.Tweening.Tween", "System.Object", "System.Type",
        "System.Delegate", "System.Action", "System.Exception", "System.Xml.XmlDocument", "System.Xml.XmlElement", "System.IO.Stream",
    };

    static readonly Dictionary<string, string> Values = new()
    {
        ["System.Boolean"] = "bool", ["System.Byte"] = "byte", ["System.SByte"] = "sbyte", ["System.Int16"] = "short",
        ["System.UInt16"] = "ushort", ["System.Char"] = "char", ["System.Int32"] = "int", ["System.UInt32"] = "uint",
        ["System.Int64"] = "long", ["System.UInt64"] = "ulong", ["System.Single"] = "float", ["System.Double"] = "double",
        ["System.IntPtr"] = "nint", ["System.UIntPtr"] = "nuint",
        ["UnityEngine.Vector2"] = "global::Jibbers.Sdk.Vector2", ["UnityEngine.Vector3"] = "global::Jibbers.Sdk.Vector3",
        ["UnityEngine.Vector4"] = "global::Jibbers.Sdk.Vector4", ["UnityEngine.Quaternion"] = "global::Jibbers.Sdk.Quaternion",
        ["UnityEngine.Color"] = "global::Jibbers.Sdk.Color", ["UnityEngine.Rect"] = "global::Jibbers.Sdk.Rect",
        ["UnityEngine.Bounds"] = "global::Jibbers.Sdk.Bounds", ["UnityEngine.Matrix4x4"] = "global::Jibbers.Sdk.Matrix4x4",
        ["UnityEngine.LayerMask"] = "int",
    };

    public Model(Catalogue cat, Snapshot snap)
    {
        Catalogue = cat;
        Snap = snap;
        Resolution = Shape.Resolve(cat, snap);
        foreach (var k in snap.Classes)
        {
            ByName.TryAdd(k.FullName, k);
        }
        var used = new HashSet<string>();
        foreach (var k in snap.Classes)
        {
            if (Excluded(k) || !Bindable(k) || k.ValueType)
            {
                continue;
            }
            var ns = "Jibbers.Interop." + (k.Namespace.Length == 0 ? "Game" : string.Join(".", k.Namespace.Split('.').Select(Names.Ident)));
            var chain = new List<string> { k.Name };
            var refChain = new List<string> { k.Name };
            for (var d = k.Declaring; d.Length > 0 && ByName.TryGetValue(d, out var outer); d = outer.Declaring)
            {
                chain.Insert(0, outer.Name);
                refChain.Insert(0, outer.Name);
            }
            var name = Names.Ident(string.Join("_", chain));
            while (!used.Add(ns + "." + name))
            {
                name += "_";
            }
            var t = new RawType { Info = k, Namespace = ns, Name = name, RefName = string.Join("/", refChain) };
            RawClasses.Add(t);
            rawByName.TryAdd(k.FullName, t);
        }
        foreach (var s in cat.Symbols)
        {
            if (!Resolution.Ok.TryGetValue(s.Id, out var r))
            {
                continue;
            }
            var info = snap.Classes[r.Class];
            var dot = s.Id.IndexOf('.');
            var (area, rest) = dot < 0 ? ("Misc", s.Id) : (s.Id[..dot], s.Id[(dot + 1)..]);
            var f = new FriendlyType { Symbol = s, Resolved = r, Info = info, Namespace = "Jibbers.Sdk.Game." + Names.Pascal(area), Name = Names.Pascal(rest) };
            Friendly.Add(f);
            if (!info.ValueType)
            {
                friendlyByName.TryAdd(info.FullName, f);
            }
        }
    }

    public CsType Type(string t, bool friendly)
    {
        if (t == "System.Void" || t == "void")
        {
            return new(Kind.Void, "void");
        }
        if (t.EndsWith('&') || t.EndsWith('*'))
        {
            return new(Kind.Pointer, "nint");
        }
        if (Values.TryGetValue(t, out var v))
        {
            return new(Kind.Value, v);
        }
        if (t == "System.String")
        {
            return new(Kind.String, "string");
        }
        if (friendly && friendlyByName.TryGetValue(t, out var f))
        {
            return new(Kind.Wrapper, f.Global);
        }
        if (rawByName.TryGetValue(t, out var r))
        {
            return new(Kind.Wrapper, r.Global);
        }
        if (ByName.TryGetValue(t, out var k))
        {
            if (k.ValueType)
            {
                if (k.Parent == "System.Enum")
                {
                    var under = k.Fields.FirstOrDefault(x => x.Name == "value__")?.Type ?? "System.Int32";
                    return Values.TryGetValue(under, out var u) ? new(Kind.Value, u) : new(Kind.Value, "int");
                }
                return new(Kind.Pointer, "nint");
            }
            return new(Kind.Object, "nint");
        }
        if (t.EndsWith("[]") || t.StartsWith("System.Collections.") || t.StartsWith("System.Action") || t.StartsWith("System.Func")
            || t.StartsWith("System.Predicate") || t.StartsWith("System.Comparison") || KnownRefs.Contains(t))
        {
            return new(Kind.Object, "nint");
        }
        if (t.StartsWith("System.Nullable<") || t.StartsWith("System.ValueTuple<"))
        {
            return new(Kind.Pointer, "nint");
        }
        return new(Kind.Unknown, "nint");
    }

    /// <summary>Whether a class derives from UnityEngine.Object (its chain leaves the dumps at a UnityEngine class).</summary>
    public bool IsUnityObject(ClassInfo k)
    {
        var cur = k;
        for (var i = 0; i < 64; i++)
        {
            if (cur.FullName == "UnityEngine.Object")
            {
                return true;
            }
            if (!ByName.TryGetValue(cur.Parent, out var p))
            {
                return cur.Parent.StartsWith("UnityEngine.") && !cur.ValueType;
            }
            cur = p;
        }
        return false;
    }
}

internal static class Names
{
    static readonly HashSet<string> Keywords = new()
    {
        "abstract", "as", "base", "bool", "break", "byte", "case", "catch", "char", "checked", "class", "const", "continue", "decimal",
        "default", "delegate", "do", "double", "else", "enum", "event", "explicit", "extern", "false", "finally", "fixed", "float", "for",
        "foreach", "goto", "if", "implicit", "in", "int", "interface", "internal", "is", "lock", "long", "namespace", "new", "null",
        "object", "operator", "out", "override", "params", "private", "protected", "public", "readonly", "ref", "return", "sbyte",
        "sealed", "short", "sizeof", "stackalloc", "static", "string", "struct", "switch", "this", "throw", "true", "try", "typeof",
        "uint", "ulong", "unchecked", "unsafe", "ushort", "using", "virtual", "void", "volatile", "while",
    };

    /// <summary>A valid C# identifier for a game name.</summary>
    public static string Ident(string name)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var c in name)
        {
            sb.Append(char.IsLetterOrDigit(c) || c == '_' ? c : '_');
        }
        var s = sb.ToString();
        if (s.Length == 0 || char.IsDigit(s[0]))
        {
            s = "_" + s;
        }
        return Keywords.Contains(s) ? "@" + s : s;
    }

    /// <summary><c>navigatable_menu</c> → <c>NavigatableMenu</c>.</summary>
    public static string Pascal(string snake) =>
        Ident(string.Concat(snake.Split('_', '.').Where(p => p.Length > 0).Select(p => char.ToUpperInvariant(p[0]) + p[1..])));

    /// <summary>XML-escaped text for doc comments.</summary>
    public static string Xml(string s) => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
}
