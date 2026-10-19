using System.Text.Json.Nodes;
using Jibbers.Sdk.Interop;
using static Jibbers.Sdk.Interop.Il2CppApi;

namespace Jibbers.Sdk;

/// <summary>
/// JSON descriptions of live game objects, for logs and probes: numbers, bools, strings, enums (as
/// numbers), structs, arrays (first 64 items) and objects down to <c>depth</c>. Main thread only.
/// </summary>
public static unsafe class Inspect
{
    const int MaxItems = 64;

    static bool NativePointer(int kind) => kind is 0x0f or 0x10 or 0x18 or 0x19 or 0x1b;

    /// <summary>Describes a value of IL2CPP type <paramref name="type"/> stored at <paramref name="slot"/>.</summary>
    public static JsonNode DescribeValue(nint type, nint slot, int depth)
    {
        if (type == 0 || slot == 0)
        {
            return JsonValue.Create((string?)null)!;
        }
        if (NativePointer(type_get_type(type)))
        {
            return new JsonObject { ["$pointer"] = $"0x{*(nint*)slot:x}" };
        }
        var p = (byte*)slot;
        switch (Il2Cpp.TypeName(type))
        {
            case "System.Boolean": return *p != 0;
            case "System.Byte": return *p;
            case "System.SByte": return *(sbyte*)p;
            case "System.Int16": return *(short*)p;
            case "System.UInt16": return *(ushort*)p;
            case "System.Char": return ((char)*(ushort*)p).ToString();
            case "System.Int32": return *(int*)p;
            case "System.UInt32": return *(uint*)p;
            case "System.Int64": return *(long*)p;
            case "System.UInt64": return *(ulong*)p;
            case "System.Single": return float.IsFinite(*(float*)p) ? *(float*)p : (*(float*)p).ToString();
            case "System.Double": return double.IsFinite(*(double*)p) ? *(double*)p : (*(double*)p).ToString();
        }
        var k = new Il2CppClass(class_from_type(type));
        if (k.IsNull)
        {
            return JsonValue.Create((string?)null)!;
        }
        return k.IsValueType ? DescribeStruct(k, slot, depth) : Describe(*(nint*)slot, depth);
    }

    static JsonNode DescribeStruct(Il2CppClass k, nint data, int depth)
    {
        var o = new JsonObject { ["$type"] = k.FullName };
        if (depth <= 0)
        {
            return o;
        }
        foreach (var f in k.Fields)
        {
            if (f.IsStatic || f.Offset < 16)
            {
                continue;
            }
            o[f.Name] = DescribeValue(f.Type, data + f.Offset - 16, depth - 1);
        }
        return k.IsEnum && o["value__"] is { } v ? v.DeepClone() : o;
    }

    /// <summary>Describes an object (null, string, array, boxed value or class instance).</summary>
    public static JsonNode Describe(nint obj, int depth = 1)
    {
        if (obj == 0)
        {
            return JsonValue.Create((string?)null)!;
        }
        var k = Il2Cpp.ClassOf(obj);
        if (k.IsValueType)
        {
            return DescribeValue(k.Type, (nint)object_unbox(obj), depth);
        }
        var name = k.FullName;
        if (name == "System.String")
        {
            return Il2Cpp.ReadString(obj);
        }
        if (name.EndsWith(']'))
        {
            var n = Il2Cpp.ArrayLength(obj);
            var items = new JsonArray();
            if (depth > 0)
            {
                var ek = k.ElementClass;
                var size = ek.IsValueType ? ek.ValueSize : 8;
                for (var i = 0; i < Math.Min(n, MaxItems); i++)
                {
                    items.Add(DescribeValue(ek.Type, Il2Cpp.ArrayData(obj) + i * size, depth - 1));
                }
            }
            return new JsonObject { ["$type"] = name, ["length"] = n, ["items"] = items };
        }
        var o = new JsonObject { ["$type"] = name };
        if (IsUnityObject(k))
        {
            if (Unity.IsNull(obj))
            {
                o["$destroyed"] = true;
                return o;
            }
            o["$name"] = Unity.Name(obj);
        }
        if (depth <= 0)
        {
            return o;
        }
        for (var c = k; !c.IsNull; c = c.Parent)
        {
            if (c.Namespace == "UnityEngine" || c.Name == "Object")
            {
                break;
            }
            foreach (var f in c.Fields)
            {
                if (!f.IsStatic)
                {
                    o[f.Name] = DescribeValue(f.Type, obj + f.Offset, depth - 1);
                }
            }
        }
        return o;
    }

    /// <summary>Describes a field's current value (static or of <paramref name="obj"/>).</summary>
    public static JsonNode DescribeField(nint obj, Il2CppField f, int depth = 1)
    {
        if (!f.IsStatic)
        {
            return obj == 0 ? JsonValue.Create((string?)null)! : DescribeValue(f.Type, obj + f.Offset, depth);
        }
        var k = f.TypeClass;
        var buf = new byte[Math.Max(8, k.IsValueType ? k.ValueSize : 8)];
        fixed (byte* b = buf)
        {
            field_static_get_value(f.Ptr, b);
            return DescribeValue(f.Type, (nint)b, depth);
        }
    }

    static Il2CppClass uobj;

    /// <summary>Whether a class derives from <c>UnityEngine.Object</c>.</summary>
    public static bool IsUnityObject(Il2CppClass k)
    {
        if (uobj.IsNull)
        {
            uobj = Il2Cpp.Class(Unity.Core, "UnityEngine", "Object");
        }
        return !uobj.IsNull && uobj.IsAssignableFrom(k);
    }
}
