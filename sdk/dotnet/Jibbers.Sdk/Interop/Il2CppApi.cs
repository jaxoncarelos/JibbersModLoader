using System.Runtime.InteropServices;

namespace Jibbers.Sdk.Interop;

/// <summary>
/// The IL2CPP embedding API exported by <c>GameAssembly.dll</c>, as raw function pointers.
/// Prefer the typed wrappers (<see cref="Il2Cpp"/>, <see cref="Il2CppClass"/>, the generated
/// <c>Game</c> and <c>Interop</c> bindings); use these only for what they do not cover.
/// </summary>
/// <remarks>Main thread only, like everything that touches game objects.</remarks>
public static unsafe class Il2CppApi
{
    static nint module;

    [DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "dlsym")]
    static extern nint MacSymbol(nint handle, string symbol);

    static nint MacExport(string name)
    {
        var symbol = "il2cpp_" + name;
        var address = MacSymbol(-2, symbol); // RTLD_DEFAULT: already-loaded game images only.
        if (address != 0) return address;
        var path = Environment.GetEnvironmentVariable("JIBBERS_IL2CPP_LIBRARY");
        if (module == 0 && path is not null) NativeLibrary.TryLoad(path, out module);
        return module != 0 && NativeLibrary.TryGetExport(module, symbol, out address) ? address : 0;
    }

    static nint Get(string name)
    {
        if (OperatingSystem.IsMacOS())
            return MacExport(name) is var p && p != 0 ? p : throw new EntryPointNotFoundException("il2cpp_" + name);
        if (module == 0)
        {
            module = NativeLibrary.Load("GameAssembly.dll");
        }
        return NativeLibrary.GetExport(module, "il2cpp_" + name);
    }

    static nint TryGet(string name)
    {
        if (OperatingSystem.IsMacOS()) return MacExport(name);
        if (module == 0)
        {
            module = NativeLibrary.Load("GameAssembly.dll");
        }
        return NativeLibrary.TryGetExport(module, "il2cpp_" + name, out var p) ? p : 0;
    }

#pragma warning disable CS1591
    public static delegate* unmanaged<nint> domain_get;
    public static delegate* unmanaged<nint, nuint*, nint*> domain_get_assemblies;
    public static delegate* unmanaged<nint, nint> assembly_get_image;
    public static delegate* unmanaged<nint, byte*> image_get_name;
    public static delegate* unmanaged<nint, nuint> image_get_class_count;
    public static delegate* unmanaged<nint, nuint, nint> image_get_class;
    public static delegate* unmanaged<nint, nint> thread_attach;
    public static delegate* unmanaged<nint> thread_current;
    public static delegate* unmanaged<nint, byte*, byte*, nint> class_from_name;
    public static delegate* unmanaged<nint, byte*> class_get_name;
    public static delegate* unmanaged<nint, byte*> class_get_namespace;
    public static delegate* unmanaged<nint, nint> class_get_parent;
    public static delegate* unmanaged<nint, nint*, nint> class_get_fields;
    public static delegate* unmanaged<nint, nint*, nint> class_get_methods;
    public static delegate* unmanaged<nint, nint*, nint> class_get_properties;
    public static delegate* unmanaged<nint, nint*, nint> class_get_nested_types;
    public static delegate* unmanaged<nint, nint*, nint> class_get_interfaces;
    public static delegate* unmanaged<nint, nint> class_get_declaring_type;
    public static delegate* unmanaged<nint, byte*, int, nint> class_get_method_from_name;
    public static delegate* unmanaged<nint, byte*, nint> class_get_field_from_name;
    public static delegate* unmanaged<nint, nint> class_get_type;
    public static delegate* unmanaged<nint, byte> class_is_valuetype;
    public static delegate* unmanaged<nint, byte> class_is_enum;
    public static delegate* unmanaged<nint, nint, byte> class_is_assignable_from;
    public static delegate* unmanaged<nint, nint> class_from_type;
    public static delegate* unmanaged<nint, nint> class_from_system_type;
    public static delegate* unmanaged<nint, nint> class_get_element_class;
    public static delegate* unmanaged<nint, uint*, int> class_value_size;
    public static delegate* unmanaged<nint, int> class_instance_size;
    public static delegate* unmanaged<nint, void> runtime_class_init;
    public static delegate* unmanaged<nint, byte*> field_get_name;
    public static delegate* unmanaged<nint, nint> field_get_type;
    public static delegate* unmanaged<nint, nuint> field_get_offset;
    public static delegate* unmanaged<nint, int> field_get_flags;
    public static delegate* unmanaged<nint, nint, void*, void> field_get_value;
    public static delegate* unmanaged<nint, nint, void*, void> field_set_value;
    public static delegate* unmanaged<nint, void*, void> field_static_get_value;
    public static delegate* unmanaged<nint, void*, void> field_static_set_value;
    public static delegate* unmanaged<nint, byte*> method_get_name;
    public static delegate* unmanaged<nint, uint> method_get_param_count;
    public static delegate* unmanaged<nint, uint, nint> method_get_param;
    public static delegate* unmanaged<nint, uint, byte*> method_get_param_name;
    public static delegate* unmanaged<nint, nint> method_get_return_type;
    public static delegate* unmanaged<nint, uint*, uint> method_get_flags;
    public static delegate* unmanaged<nint, nint> method_get_class;
    public static delegate* unmanaged<nint, byte*> property_get_name;
    public static delegate* unmanaged<nint, nint> property_get_get_method;
    public static delegate* unmanaged<nint, nint> property_get_set_method;
    public static delegate* unmanaged<nint, byte*> type_get_name;
    public static delegate* unmanaged<nint, int> type_get_type;
    public static delegate* unmanaged<nint, nint> type_get_object;
    public static delegate* unmanaged<nint, byte> type_is_byref;
    public static delegate* unmanaged<void*, void> free;
    public static delegate* unmanaged<nint, nint, nint*, nint*, nint> runtime_invoke;
    public static delegate* unmanaged<nint, nint> object_get_class;
    public static delegate* unmanaged<nint, void*> object_unbox;
    public static delegate* unmanaged<nint, nint> object_new;
    public static delegate* unmanaged<nint, void*, nint> value_box;
    public static delegate* unmanaged<byte*, uint, nint> string_new_len;
    public static delegate* unmanaged<char*, int, nint> string_new_utf16;
    public static delegate* unmanaged<nint, int> string_length;
    public static delegate* unmanaged<nint, char*> string_chars;
    public static delegate* unmanaged<nint, nuint, nint> array_new;
    public static delegate* unmanaged<nint, uint> array_length;
    public static delegate* unmanaged<nint, nuint*, nuint*, nint> array_new_full;
    public static delegate* unmanaged<nint, uint, byte, nint> bounded_array_class_get;
    public static delegate* unmanaged<nint, uint, nint> array_class_get;
    public static delegate* unmanaged<nint, byte*, int, void> format_exception;
    public static delegate* unmanaged<byte*, nint> resolve_icall;
#pragma warning restore CS1591

    static bool loaded;

    /// <summary>Resolves every export. Called by the SDK at startup.</summary>
    public static void Load()
    {
        if (loaded)
        {
            return;
        }
        domain_get = (delegate* unmanaged<nint>)Get("domain_get");
        domain_get_assemblies = (delegate* unmanaged<nint, nuint*, nint*>)Get("domain_get_assemblies");
        assembly_get_image = (delegate* unmanaged<nint, nint>)Get("assembly_get_image");
        image_get_name = (delegate* unmanaged<nint, byte*>)Get("image_get_name");
        image_get_class_count = (delegate* unmanaged<nint, nuint>)Get("image_get_class_count");
        image_get_class = (delegate* unmanaged<nint, nuint, nint>)Get("image_get_class");
        thread_attach = (delegate* unmanaged<nint, nint>)Get("thread_attach");
        thread_current = (delegate* unmanaged<nint>)Get("thread_current");
        class_from_name = (delegate* unmanaged<nint, byte*, byte*, nint>)Get("class_from_name");
        class_get_name = (delegate* unmanaged<nint, byte*>)Get("class_get_name");
        class_get_namespace = (delegate* unmanaged<nint, byte*>)Get("class_get_namespace");
        class_get_parent = (delegate* unmanaged<nint, nint>)Get("class_get_parent");
        class_get_fields = (delegate* unmanaged<nint, nint*, nint>)Get("class_get_fields");
        class_get_methods = (delegate* unmanaged<nint, nint*, nint>)Get("class_get_methods");
        class_get_properties = (delegate* unmanaged<nint, nint*, nint>)Get("class_get_properties");
        class_get_nested_types = (delegate* unmanaged<nint, nint*, nint>)Get("class_get_nested_types");
        class_get_interfaces = (delegate* unmanaged<nint, nint*, nint>)Get("class_get_interfaces");
        class_get_declaring_type = (delegate* unmanaged<nint, nint>)Get("class_get_declaring_type");
        class_get_method_from_name = (delegate* unmanaged<nint, byte*, int, nint>)Get("class_get_method_from_name");
        class_get_field_from_name = (delegate* unmanaged<nint, byte*, nint>)Get("class_get_field_from_name");
        class_get_type = (delegate* unmanaged<nint, nint>)Get("class_get_type");
        class_is_valuetype = (delegate* unmanaged<nint, byte>)Get("class_is_valuetype");
        class_is_enum = (delegate* unmanaged<nint, byte>)Get("class_is_enum");
        class_is_assignable_from = (delegate* unmanaged<nint, nint, byte>)Get("class_is_assignable_from");
        class_from_type = (delegate* unmanaged<nint, nint>)Get("class_from_type");
        class_from_system_type = (delegate* unmanaged<nint, nint>)Get("class_from_system_type");
        class_get_element_class = (delegate* unmanaged<nint, nint>)Get("class_get_element_class");
        class_value_size = (delegate* unmanaged<nint, uint*, int>)Get("class_value_size");
        class_instance_size = (delegate* unmanaged<nint, int>)Get("class_instance_size");
        runtime_class_init = (delegate* unmanaged<nint, void>)Get("runtime_class_init");
        field_get_name = (delegate* unmanaged<nint, byte*>)Get("field_get_name");
        field_get_type = (delegate* unmanaged<nint, nint>)Get("field_get_type");
        field_get_offset = (delegate* unmanaged<nint, nuint>)Get("field_get_offset");
        field_get_flags = (delegate* unmanaged<nint, int>)Get("field_get_flags");
        field_get_value = (delegate* unmanaged<nint, nint, void*, void>)Get("field_get_value");
        field_set_value = (delegate* unmanaged<nint, nint, void*, void>)Get("field_set_value");
        field_static_get_value = (delegate* unmanaged<nint, void*, void>)Get("field_static_get_value");
        field_static_set_value = (delegate* unmanaged<nint, void*, void>)Get("field_static_set_value");
        method_get_name = (delegate* unmanaged<nint, byte*>)Get("method_get_name");
        method_get_param_count = (delegate* unmanaged<nint, uint>)Get("method_get_param_count");
        method_get_param = (delegate* unmanaged<nint, uint, nint>)Get("method_get_param");
        method_get_param_name = (delegate* unmanaged<nint, uint, byte*>)Get("method_get_param_name");
        method_get_return_type = (delegate* unmanaged<nint, nint>)Get("method_get_return_type");
        method_get_flags = (delegate* unmanaged<nint, uint*, uint>)Get("method_get_flags");
        method_get_class = (delegate* unmanaged<nint, nint>)Get("method_get_class");
        property_get_name = (delegate* unmanaged<nint, byte*>)Get("property_get_name");
        property_get_get_method = (delegate* unmanaged<nint, nint>)Get("property_get_get_method");
        property_get_set_method = (delegate* unmanaged<nint, nint>)Get("property_get_set_method");
        type_get_name = (delegate* unmanaged<nint, byte*>)Get("type_get_name");
        type_get_type = (delegate* unmanaged<nint, int>)Get("type_get_type");
        type_get_object = (delegate* unmanaged<nint, nint>)Get("type_get_object");
        type_is_byref = (delegate* unmanaged<nint, byte>)TryGet("type_is_byref");
        free = (delegate* unmanaged<void*, void>)Get("free");
        runtime_invoke = (delegate* unmanaged<nint, nint, nint*, nint*, nint>)Get("runtime_invoke");
        object_get_class = (delegate* unmanaged<nint, nint>)Get("object_get_class");
        object_unbox = (delegate* unmanaged<nint, void*>)Get("object_unbox");
        object_new = (delegate* unmanaged<nint, nint>)Get("object_new");
        value_box = (delegate* unmanaged<nint, void*, nint>)Get("value_box");
        string_new_len = (delegate* unmanaged<byte*, uint, nint>)Get("string_new_len");
        string_new_utf16 = (delegate* unmanaged<char*, int, nint>)Get("string_new_utf16");
        string_length = (delegate* unmanaged<nint, int>)Get("string_length");
        string_chars = (delegate* unmanaged<nint, char*>)Get("string_chars");
        array_new = (delegate* unmanaged<nint, nuint, nint>)Get("array_new");
        array_length = (delegate* unmanaged<nint, uint>)Get("array_length");
        array_new_full = (delegate* unmanaged<nint, nuint*, nuint*, nint>)Get("array_new_full");
        bounded_array_class_get = (delegate* unmanaged<nint, uint, byte, nint>)Get("bounded_array_class_get");
        array_class_get = (delegate* unmanaged<nint, uint, nint>)Get("array_class_get");
        format_exception = (delegate* unmanaged<nint, byte*, int, void>)Get("format_exception");
        resolve_icall = (delegate* unmanaged<byte*, nint>)Get("resolve_icall");
        loaded = true;
    }
}
