using System.Runtime.InteropServices;
using System.Text;
using static Jibbers.Sdk.Interop.Il2CppApi;

namespace Jibbers.Sdk.Interop;

/// <summary>A managed exception thrown by game code, with its IL2CPP message and stack trace.</summary>
public sealed class Il2CppException : Exception
{
    /// <summary>The IL2CPP exception object.</summary>
    public nint Object { get; }

    /// <summary>Wraps an IL2CPP exception.</summary>
    public Il2CppException(string message, nint obj) : base(message)
    {
        Object = obj;
    }
}

/// <summary>A game class (<c>Il2CppClass*</c>).</summary>
public readonly unsafe record struct Il2CppClass(nint Ptr)
{
    /// <summary>Whether this is the null class.</summary>
    public bool IsNull => Ptr == 0;
    /// <summary>Short name (<c>FollowCam</c>, or an obfuscated <c>qj</c>).</summary>
    public string Name => Il2Cpp.Str(class_get_name(Ptr));
    /// <summary>Namespace ("" for the game's own classes).</summary>
    public string Namespace => Il2Cpp.Str(class_get_namespace(Ptr));
    /// <summary>IL2CPP type name, the form field and parameter types use (<c>FollowCam.CamState</c>).</summary>
    public string FullName => Il2Cpp.TypeName(class_get_type(Ptr));
    /// <summary>The parent class, or the null class.</summary>
    public Il2CppClass Parent => new(class_get_parent(Ptr));
    /// <summary>The enclosing class of a nested class.</summary>
    public Il2CppClass DeclaringType => new(class_get_declaring_type(Ptr));
    /// <summary>A struct or enum.</summary>
    public bool IsValueType => class_is_valuetype(Ptr) != 0;
    /// <summary>An enum.</summary>
    public bool IsEnum => class_is_enum(Ptr) != 0;
    /// <summary>Size of a value type's data (no object header).</summary>
    public int ValueSize => class_value_size(Ptr, null);
    /// <summary>Size of an instance, header included.</summary>
    public int InstanceSize => class_instance_size(Ptr);
    /// <summary>The <c>Il2CppType*</c> of the class.</summary>
    public nint Type => class_get_type(Ptr);
    /// <summary>The <c>System.Type</c> object (for reflection calls such as <c>GetComponent(Type)</c>).</summary>
    public nint SystemType => type_get_object(class_get_type(Ptr));
    /// <summary>Element class of an array class.</summary>
    public Il2CppClass ElementClass => new(class_get_element_class(Ptr));

    /// <summary>Runs the class's static constructor if it has not run.</summary>
    public void Init() => runtime_class_init(Ptr);

    /// <summary>Whether <paramref name="other"/> is this class or derives from it.</summary>
    public bool IsAssignableFrom(Il2CppClass other) => class_is_assignable_from(Ptr, other.Ptr) != 0;

    /// <summary>Declared fields (not inherited).</summary>
    public IEnumerable<Il2CppField> Fields
    {
        get
        {
            var list = new List<Il2CppField>();
            nint it = 0;
            for (nint f; (f = class_get_fields(Ptr, &it)) != 0;)
            {
                list.Add(new Il2CppField(f));
            }
            return list;
        }
    }

    /// <summary>Declared methods (not inherited).</summary>
    public IEnumerable<Il2CppMethod> Methods
    {
        get
        {
            var list = new List<Il2CppMethod>();
            nint it = 0;
            for (nint m; (m = class_get_methods(Ptr, &it)) != 0;)
            {
                list.Add(new Il2CppMethod(m));
            }
            return list;
        }
    }

    /// <summary>Declared properties.</summary>
    public IEnumerable<(string Name, Il2CppMethod Get, Il2CppMethod Set)> Properties
    {
        get
        {
            var list = new List<(string, Il2CppMethod, Il2CppMethod)>();
            nint it = 0;
            for (nint p; (p = class_get_properties(Ptr, &it)) != 0;)
            {
                list.Add((Il2Cpp.Str(property_get_name(p)), new Il2CppMethod(property_get_get_method(p)), new Il2CppMethod(property_get_set_method(p))));
            }
            return list;
        }
    }

    /// <summary>Nested classes.</summary>
    public IEnumerable<Il2CppClass> NestedTypes
    {
        get
        {
            var list = new List<Il2CppClass>();
            nint it = 0;
            for (nint k; (k = class_get_nested_types(Ptr, &it)) != 0;)
            {
                list.Add(new Il2CppClass(k));
            }
            return list;
        }
    }

    /// <summary>Interfaces the class implements.</summary>
    public IEnumerable<Il2CppClass> Interfaces
    {
        get
        {
            var list = new List<Il2CppClass>();
            nint it = 0;
            for (nint k; (k = class_get_interfaces(Ptr, &it)) != 0;)
            {
                list.Add(new Il2CppClass(k));
            }
            return list;
        }
    }

    /// <summary>A field by name, searching parents too.</summary>
    public Il2CppField Field(string name)
    {
        var bytes = Il2Cpp.Utf8z(name);
        for (var k = this; !k.IsNull; k = k.Parent)
        {
            fixed (byte* p = bytes)
            {
                var f = class_get_field_from_name(k.Ptr, p);
                if (f != 0)
                {
                    return new Il2CppField(f);
                }
            }
        }
        return default;
    }

    /// <summary>A method by name and parameter count (-1 = any), searching parents too.</summary>
    public Il2CppMethod Method(string name, int argc = -1)
    {
        var bytes = Il2Cpp.Utf8z(name);
        for (var k = this; !k.IsNull; k = k.Parent)
        {
            fixed (byte* p = bytes)
            {
                var m = class_get_method_from_name(k.Ptr, p, argc);
                if (m != 0)
                {
                    return new Il2CppMethod(m);
                }
            }
        }
        return default;
    }

    /// <summary>A method by name and exact parameter type names, searching parents too.</summary>
    public Il2CppMethod Method(string name, params string[] parameterTypes)
    {
        for (var k = this; !k.IsNull; k = k.Parent)
        {
            foreach (var m in k.Methods)
            {
                if (m.Name == name && m.ParameterTypes.SequenceEqual(parameterTypes))
                {
                    return m;
                }
            }
        }
        return default;
    }

    /// <summary>A new, uninitialised instance (call a constructor with <see cref="Il2CppMethod.Invoke"/>).</summary>
    public nint New() => object_new(Ptr);

    /// <summary>A new instance with its parameterless constructor run.</summary>
    public nint Construct()
    {
        var o = object_new(Ptr);
        var ctor = Method(".ctor", 0);
        if (!ctor.IsNull)
        {
            ctor.Invoke(o);
        }
        return o;
    }

    /// <summary>The array class of this element class (<c>T[]</c>).</summary>
    public Il2CppClass ArrayClass(uint rank = 1) => new(array_class_get(Ptr, rank));

    /// <inheritdoc/>
    public override string ToString() => IsNull ? "<null class>" : FullName;
}

/// <summary>A field (<c>FieldInfo*</c>).</summary>
public readonly unsafe record struct Il2CppField(nint Ptr)
{
    /// <summary>Whether this is the null field.</summary>
    public bool IsNull => Ptr == 0;
    /// <summary>Real name (obfuscated for most game fields).</summary>
    public string Name => Il2Cpp.Str(field_get_name(Ptr));
    /// <summary>IL2CPP type name.</summary>
    public string TypeName => Il2Cpp.TypeName(field_get_type(Ptr));
    /// <summary>The field's <c>Il2CppType*</c>.</summary>
    public nint Type => field_get_type(Ptr);
    /// <summary>Class of the field's type.</summary>
    public Il2CppClass TypeClass => new(class_from_type(field_get_type(Ptr)));
    /// <summary>Static field.</summary>
    public bool IsStatic => (field_get_flags(Ptr) & 0x10) != 0;
    /// <summary>Byte offset in an instance (object header included).</summary>
    public int Offset => (int)field_get_offset(Ptr);

    /// <summary>Reads a value field of <paramref name="obj"/>. <typeparamref name="T"/> must match the field's type.</summary>
    public T Get<T>(nint obj) where T : unmanaged
    {
        T v = default;
        if (IsStatic)
        {
            field_static_get_value(Ptr, &v);
        }
        else
        {
            Il2Cpp.Require(obj, Name);
            field_get_value(obj, Ptr, &v);
        }
        return v;
    }

    /// <summary>Writes a value field of <paramref name="obj"/>.</summary>
    public void Set<T>(nint obj, T value) where T : unmanaged
    {
        if (IsStatic)
        {
            field_static_set_value(Ptr, &value);
        }
        else
        {
            Il2Cpp.Require(obj, Name);
            field_set_value(obj, Ptr, &value);
        }
    }

    /// <summary>Reads an object-reference field.</summary>
    public nint GetObject(nint obj) => Get<nint>(obj);

    /// <summary>Writes an object-reference field.</summary>
    public void SetObject(nint obj, nint value)
    {
        if (IsStatic)
        {
            field_static_set_value(Ptr, (void*)value);
        }
        else
        {
            Il2Cpp.Require(obj, Name);
            field_set_value(obj, Ptr, (void*)value);
        }
    }

    /// <summary>Reads a string field.</summary>
    public string GetString(nint obj) => Il2Cpp.ReadString(GetObject(obj));

    /// <summary>Writes a string field.</summary>
    public void SetString(nint obj, string? value) => SetObject(obj, value is null ? 0 : Il2Cpp.String(value));

    /// <inheritdoc/>
    public override string ToString() => IsNull ? "<null field>" : $"{TypeName} {Name}";
}

/// <summary>A method (<c>MethodInfo*</c>).</summary>
public readonly unsafe record struct Il2CppMethod(nint Ptr)
{
    /// <summary>Whether this is the null method.</summary>
    public bool IsNull => Ptr == 0;
    /// <summary>Real name.</summary>
    public string Name => Il2Cpp.Str(method_get_name(Ptr));
    /// <summary>Number of parameters.</summary>
    public int ParameterCount => (int)method_get_param_count(Ptr);
    /// <summary>Parameter type names.</summary>
    public string[] ParameterTypes
    {
        get
        {
            var r = new string[ParameterCount];
            for (var i = 0; i < r.Length; i++)
            {
                r[i] = Il2Cpp.TypeName(method_get_param(Ptr, (uint)i));
            }
            return r;
        }
    }
    /// <summary>Parameter names (obfuscated ones are often <c>a</c>, <c>b</c>, ...).</summary>
    public string[] ParameterNames
    {
        get
        {
            var r = new string[ParameterCount];
            for (var i = 0; i < r.Length; i++)
            {
                r[i] = Il2Cpp.Str(method_get_param_name(Ptr, (uint)i));
            }
            return r;
        }
    }
    /// <summary>Return type name.</summary>
    public string ReturnType => Il2Cpp.TypeName(method_get_return_type(Ptr));
    /// <summary>Static method.</summary>
    public bool IsStatic
    {
        get
        {
            uint iflags;
            return (method_get_flags(Ptr, &iflags) & 0x10) != 0;
        }
    }
    /// <summary>The declaring class.</summary>
    public Il2CppClass DeclaringClass => new(method_get_class(Ptr));
    /// <summary>The method's native code (the first field of <c>MethodInfo</c>): the address to detour.</summary>
    public nint NativeCode => *(nint*)Ptr;

    /// <summary>
    /// Calls the method. <paramref name="args"/> are pointers: to the value for value types
    /// (<c>(nint)(&amp;myInt)</c>), the object itself for reference types. Returns the result
    /// (boxed for value types; unbox with <see cref="Il2Cpp.Unbox{T}"/>). Throws
    /// <see cref="Il2CppException"/> if the game throws.
    /// </summary>
    public nint Invoke(nint self, params nint[] args)
    {
        if (IsNull)
        {
            throw new InvalidOperationException("null method");
        }
        if (args.Length != ParameterCount)
        {
            throw new ArgumentException($"{Name} takes {ParameterCount} arguments, got {args.Length}");
        }
        nint exc = 0;
        nint r;
        fixed (nint* a = args)
        {
            r = runtime_invoke(Ptr, self, args.Length == 0 ? null : a, &exc);
        }
        if (exc != 0)
        {
            throw Il2Cpp.Exception(exc);
        }
        return r;
    }

    /// <summary>Calls the method and unboxes a value-type result.</summary>
    public T Invoke<T>(nint self, params nint[] args) where T : unmanaged
    {
        var r = Invoke(self, args);
        if (r == 0)
        {
            throw new InvalidOperationException($"{Name} returned null");
        }
        return Il2Cpp.Unbox<T>(r);
    }

    /// <summary>The override of this method that <paramref name="obj"/> really runs (IL2CPP's invoke is not virtual).</summary>
    public Il2CppMethod Virtual(nint obj)
    {
        if (obj == 0 || IsStatic)
        {
            return this;
        }
        var name = Name;
        var types = ParameterTypes;
        for (var k = Il2Cpp.ClassOf(obj); !k.IsNull; k = k.Parent)
        {
            foreach (var m in k.Methods)
            {
                if (m.Ptr == Ptr)
                {
                    return this;
                }
                if (m.Name == name && m.ParameterTypes.SequenceEqual(types))
                {
                    return m;
                }
            }
        }
        return this;
    }

    /// <inheritdoc/>
    public override string ToString() => IsNull ? "<null method>" : $"{ReturnType} {Name}({string.Join(", ", ParameterTypes)})";
}

/// <summary>Helpers for IL2CPP strings, arrays, boxing, classes and objects.</summary>
public static unsafe class Il2Cpp
{
    internal static string Str(byte* p) => p == null ? "" : Marshal.PtrToStringUTF8((nint)p) ?? "";

    internal static byte[] Utf8z(string s)
    {
        var b = new byte[Encoding.UTF8.GetByteCount(s) + 1];
        Encoding.UTF8.GetBytes(s, b);
        return b;
    }

    internal static void Require(nint obj, string what)
    {
        if (obj == 0)
        {
            throw new NullReferenceException($"{what} of a null object");
        }
    }

    /// <summary>The name of an <c>Il2CppType*</c> (<c>System.Int32</c>, <c>UnityEngine.Terrain[]</c>).</summary>
    public static string TypeName(nint type)
    {
        if (type == 0)
        {
            return "void";
        }
        var p = type_get_name(type);
        var s = Str(p);
        Il2CppApi.free(p);
        return s;
    }

    /// <summary>Every loaded assembly image: (image, name).</summary>
    public static List<(nint Image, string Name)> Assemblies()
    {
        nuint n = 0;
        var list = domain_get_assemblies(domain_get(), &n);
        var r = new List<(nint, string)>();
        for (nuint i = 0; i < n; i++)
        {
            var img = assembly_get_image(list[i]);
            r.Add((img, Str(image_get_name(img))));
        }
        return r;
    }

    /// <summary>Every class of an image (<c>Assembly-CSharp.dll</c>).</summary>
    public static List<Il2CppClass> Classes(string image)
    {
        var list = new List<Il2CppClass>();
        var img = Assemblies().FirstOrDefault(a => a.Name == image).Image;
        if (img == 0)
        {
            return list;
        }
        var n = image_get_class_count(img);
        for (nuint i = 0; i < n; i++)
        {
            list.Add(new Il2CppClass(image_get_class(img, i)));
        }
        return list;
    }

    /// <summary>A class by image, namespace and name, or the null class.</summary>
    public static Il2CppClass Class(string image, string ns, string name)
    {
        var img = Assemblies().FirstOrDefault(a => a.Name == image).Image;
        if (img == 0)
        {
            return default;
        }
        fixed (byte* n = Utf8z(ns), c = Utf8z(name))
        {
            return new Il2CppClass(class_from_name(img, n, c));
        }
    }

    /// <summary>A class by image and namespace-qualified name (<c>UnityEngine.GameObject</c>); throws if missing.</summary>
    public static Il2CppClass RequireClass(string image, string fullName)
    {
        var dot = fullName.LastIndexOf('.');
        var k = dot < 0 ? Class(image, "", fullName) : Class(image, fullName[..dot], fullName[(dot + 1)..]);
        return k.IsNull ? throw new InvalidOperationException($"class {fullName} not found in {image}") : k;
    }

    /// <summary>The runtime class of an object.</summary>
    public static Il2CppClass ClassOf(nint obj) => obj == 0 ? default : new Il2CppClass(object_get_class(obj));

    /// <summary>A new IL2CPP string.</summary>
    public static nint String(string s)
    {
        fixed (char* p = s)
        {
            return string_new_utf16(p, s.Length);
        }
    }

    /// <summary>Reads an IL2CPP string ("" for null).</summary>
    public static string ReadString(nint str)
    {
        if (str == 0)
        {
            return "";
        }
        return new string(string_chars(str), 0, string_length(str));
    }

    /// <summary>Unboxes a boxed value type.</summary>
    public static T Unbox<T>(nint boxed) where T : unmanaged => *(T*)object_unbox(boxed);

    /// <summary>Boxes a value of class <paramref name="k"/>.</summary>
    public static nint Box<T>(Il2CppClass k, T value) where T : unmanaged => value_box(k.Ptr, &value);

    /// <summary>Length of a managed array.</summary>
    public static int ArrayLength(nint arr) => arr == 0 ? 0 : (int)array_length(arr);

    /// <summary>Pointer to the first element (x64: header 16 + bounds 8 + length 8).</summary>
    public static nint ArrayData(nint arr) => arr + 32;

    /// <summary>The object references of a reference-type array.</summary>
    public static nint[] ArrayObjects(nint arr)
    {
        var n = ArrayLength(arr);
        var r = new nint[n];
        for (var i = 0; i < n; i++)
        {
            r[i] = ((nint*)ArrayData(arr))[i];
        }
        return r;
    }

    /// <summary>The elements of a value-type array.</summary>
    public static T[] ArrayValues<T>(nint arr) where T : unmanaged
    {
        var n = ArrayLength(arr);
        var r = new T[n];
        for (var i = 0; i < n; i++)
        {
            r[i] = ((T*)ArrayData(arr))[i];
        }
        return r;
    }

    /// <summary>A new array of <paramref name="length"/> elements of class <paramref name="element"/>.</summary>
    public static nint NewArray(Il2CppClass element, int length)
    {
        var a = array_new(element.Ptr, (nuint)length);
        return a == 0 ? throw new OutOfMemoryException("array_new failed") : a;
    }

    /// <summary>A new <c>string[]</c>.</summary>
    public static nint StringArray(IReadOnlyList<string> items)
    {
        var a = NewArray(RequireClass("mscorlib.dll", "System.String"), items.Count);
        for (var i = 0; i < items.Count; i++)
        {
            ((nint*)ArrayData(a))[i] = String(items[i]);
        }
        return a;
    }

    /// <summary>The native function behind an internal call (<c>UnityEngine.Time::get_deltaTime</c>), or 0.</summary>
    public static nint ICall(string name)
    {
        fixed (byte* p = Utf8z(name))
        {
            return resolve_icall(p);
        }
    }

    /// <summary>Formats an IL2CPP exception with its stack trace.</summary>
    public static Il2CppException Exception(nint exc)
    {
        var buf = new byte[4096];
        fixed (byte* b = buf)
        {
            format_exception(exc, b, buf.Length);
        }
        var msg = Encoding.UTF8.GetString(buf, 0, Math.Max(0, Array.IndexOf(buf, (byte)0)));
        var st = ClassOf(exc).Method("get_StackTrace", 0);
        if (!st.IsNull)
        {
            nint e2 = 0;
            var s = runtime_invoke(st.Ptr, exc, null, &e2);
            if (e2 == 0 && s != 0)
            {
                msg += " | at " + ReadString(s).Replace("\n", " | ");
            }
        }
        return new Il2CppException(msg, exc);
    }

    /// <summary>Attaches the calling thread to the IL2CPP domain (needed before IL2CPP calls from your own threads).</summary>
    public static void AttachThread() => thread_attach(domain_get());
}
