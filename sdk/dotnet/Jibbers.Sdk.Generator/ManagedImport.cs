// Reads .NET assemblies (Unity's managed engine modules) into the dump format, so engine classes get raw
// bindings like the game's. Unity's names are not obfuscated, so the bindings resolve against the game
// by name at runtime; field offsets are not known offline and are written as 0 (nothing reads them).

using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using Jibbers.Sdk.Symbols;
using FieldInfo = Jibbers.Sdk.Symbols.FieldInfo;
using MethodInfo = Jibbers.Sdk.Symbols.MethodInfo;

namespace Jibbers.Sdk.Generator;

static class ManagedImport
{
    /// <summary>The classes of <paramref name="path"/>, named as IL2CPP names them (<c>Outer.Inner</c>, <c>List&lt;T&gt;</c>).</summary>
    public static List<ClassInfo> Read(string path)
    {
        using var pe = new PEReader(File.OpenRead(path));
        var md = pe.GetMetadataReader();
        var image = Path.GetFileName(path);
        var names = new Names(md);
        var result = new List<ClassInfo>();
        foreach (var h in md.TypeDefinitions)
        {
            var t = md.GetTypeDefinition(h);
            var typeParams = t.GetGenericParameters().Select(p => md.GetString(md.GetGenericParameter(p).Name)).ToImmutableArray();
            var ctx = new Context(typeParams, ImmutableArray<string>.Empty);
            var k = new ClassInfo
            {
                Image = image,
                Namespace = names.Namespace(h),
                Name = md.GetString(t.Name),
                FullName = names.Definition(h),
                Declaring = t.GetDeclaringType().IsNil ? "" : names.Definition(t.GetDeclaringType()),
                Parent = t.BaseType.IsNil ? "" : names.Handle(t.BaseType, ctx),
                Interfaces = t.GetInterfaceImplementations().Select(i => names.Handle(md.GetInterfaceImplementation(i).Interface, ctx)).ToList(),
            };
            k.ValueType = k.Parent is "System.ValueType" or "System.Enum";
            foreach (var fh in t.GetFields())
            {
                var f = md.GetFieldDefinition(fh);
                k.Fields.Add(new FieldInfo
                {
                    Name = md.GetString(f.Name),
                    Type = f.DecodeSignature(names, ctx),
                    Static = (f.Attributes & FieldAttributes.Static) != 0,
                });
            }
            foreach (var mh in t.GetMethods())
            {
                var m = md.GetMethodDefinition(mh);
                var mctx = new Context(typeParams, m.GetGenericParameters().Select(p => md.GetString(md.GetGenericParameter(p).Name)).ToImmutableArray());
                var sig = m.DecodeSignature(names, mctx);
                k.Methods.Add(new MethodInfo
                {
                    Name = md.GetString(m.Name),
                    Params = sig.ParameterTypes.ToList(),
                    Return = sig.ReturnType,
                    Static = (m.Attributes & MethodAttributes.Static) != 0,
                    ParamNames = ParamNames(md, m, sig.ParameterTypes.Length),
                });
            }
            result.Add(k);
        }
        return result;
    }

    static List<string> ParamNames(MetadataReader md, MethodDefinition m, int count)
    {
        var names = Enumerable.Range(0, count).Select(i => "").ToList();
        foreach (var ph in m.GetParameters())
        {
            var p = md.GetParameter(ph);
            if (p.SequenceNumber >= 1 && p.SequenceNumber <= count)
            {
                names[p.SequenceNumber - 1] = md.GetString(p.Name);
            }
        }
        return names;
    }

    readonly record struct Context(ImmutableArray<string> Type, ImmutableArray<string> Method);

    sealed class Names(MetadataReader md) : ISignatureTypeProvider<string, Context>
    {
        static string Strip(string n) => n.IndexOf('`') is var i and >= 0 ? n[..i] : n;

        public string Namespace(TypeDefinitionHandle h)
        {
            var t = md.GetTypeDefinition(h);
            return t.GetDeclaringType().IsNil ? md.GetString(t.Namespace) : Namespace(t.GetDeclaringType());
        }

        /// <summary>A definition's own name, keeping the generic arity (<c>List`1</c>) as IL2CPP's class names do.</summary>
        public string Definition(TypeDefinitionHandle h)
        {
            var t = md.GetTypeDefinition(h);
            if (!t.GetDeclaringType().IsNil)
            {
                return Definition(t.GetDeclaringType()) + "." + md.GetString(t.Name);
            }
            var ns = md.GetString(t.Namespace);
            return ns.Length == 0 ? md.GetString(t.Name) : ns + "." + md.GetString(t.Name);
        }

        string Reference(TypeReferenceHandle h)
        {
            var r = md.GetTypeReference(h);
            var name = Strip(md.GetString(r.Name));
            if (r.ResolutionScope.Kind == HandleKind.TypeReference)
            {
                return Reference((TypeReferenceHandle)r.ResolutionScope) + "." + name;
            }
            var ns = md.GetString(r.Namespace);
            return ns.Length == 0 ? name : ns + "." + name;
        }

        public string Handle(EntityHandle h, Context ctx) => h.Kind switch
        {
            HandleKind.TypeDefinition => GetTypeFromDefinition(md, (TypeDefinitionHandle)h, 0),
            HandleKind.TypeReference => Reference((TypeReferenceHandle)h),
            HandleKind.TypeSpecification => md.GetTypeSpecification((TypeSpecificationHandle)h).DecodeSignature(this, ctx),
            _ => "System.Object",
        };

        public string GetPrimitiveType(PrimitiveTypeCode c) => c switch
        {
            PrimitiveTypeCode.Void => "System.Void",
            PrimitiveTypeCode.Boolean => "System.Boolean",
            PrimitiveTypeCode.Char => "System.Char",
            PrimitiveTypeCode.SByte => "System.SByte",
            PrimitiveTypeCode.Byte => "System.Byte",
            PrimitiveTypeCode.Int16 => "System.Int16",
            PrimitiveTypeCode.UInt16 => "System.UInt16",
            PrimitiveTypeCode.Int32 => "System.Int32",
            PrimitiveTypeCode.UInt32 => "System.UInt32",
            PrimitiveTypeCode.Int64 => "System.Int64",
            PrimitiveTypeCode.UInt64 => "System.UInt64",
            PrimitiveTypeCode.Single => "System.Single",
            PrimitiveTypeCode.Double => "System.Double",
            PrimitiveTypeCode.String => "System.String",
            PrimitiveTypeCode.IntPtr => "System.IntPtr",
            PrimitiveTypeCode.UIntPtr => "System.UIntPtr",
            PrimitiveTypeCode.TypedReference => "System.TypedReference",
            _ => "System.Object",
        };

        // Type names in signatures drop the arity (List<T>), as IL2CPP's type names do.
        public string GetTypeFromDefinition(MetadataReader r, TypeDefinitionHandle h, byte kind)
        {
            var t = r.GetTypeDefinition(h);
            var name = Strip(r.GetString(t.Name));
            if (!t.GetDeclaringType().IsNil)
            {
                return GetTypeFromDefinition(r, t.GetDeclaringType(), 0) + "." + name;
            }
            var ns = r.GetString(t.Namespace);
            return ns.Length == 0 ? name : ns + "." + name;
        }

        public string GetTypeFromReference(MetadataReader r, TypeReferenceHandle h, byte kind) => Reference(h);
        public string GetTypeFromSpecification(MetadataReader r, Context ctx, TypeSpecificationHandle h, byte kind) => r.GetTypeSpecification(h).DecodeSignature(this, ctx);
        public string GetSZArrayType(string e) => e + "[]";
        public string GetArrayType(string e, ArrayShape s) => e + "[" + new string(',', s.Rank - 1) + "]";
        public string GetByReferenceType(string e) => e + "&";
        public string GetPointerType(string e) => e + "*";
        public string GetPinnedType(string e) => e;
        public string GetGenericInstantiation(string g, ImmutableArray<string> a) => g + "<" + string.Join(",", a) + ">";
        public string GetGenericTypeParameter(Context ctx, int i) => i < ctx.Type.Length ? ctx.Type[i] : "T" + i;
        public string GetGenericMethodParameter(Context ctx, int i) => i < ctx.Method.Length ? ctx.Method[i] : "M" + i;
        public string GetFunctionPointerType(MethodSignature<string> s) => "System.IntPtr";
        public string GetModifiedType(string modifier, string unmodified, bool required) => unmodified;
    }
}
