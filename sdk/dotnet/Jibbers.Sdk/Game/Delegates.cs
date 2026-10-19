using Jibbers.Sdk.Interop;

namespace Jibbers.Sdk;

/// <summary>
/// Game delegates (<c>System.Action&lt;T&gt;</c>, button callbacks, ...) that call a native or C#
/// <c>[UnmanagedCallersOnly]</c> function, and generic-type reflection. Main thread only.
/// </summary>
/// <remarks>
/// IL2CPP calls a delegate as <c>invoke_impl(method_code, args..., method)</c>. A delegate from
/// <see cref="Native"/> has <c>invoke_impl</c> = your function and <c>method_code</c> = your
/// <c>ctx</c>, so for an <c>Action&lt;T&gt;</c> your function is
/// <c>(nint ctx, T arg, nint method)</c> with Windows x64 argument rules: structs of 1, 2, 4 or 8
/// bytes by value, other structs as a pointer to a copy. <see cref="Events"/> and
/// <see cref="GameVars"/> wrap this for you.
/// </remarks>
public static unsafe class Delegates
{
    /// <summary>The class behind a <c>System.Type</c> object.</summary>
    public static Il2CppClass ClassOf(nint systemType) =>
        systemType == 0 ? throw new ArgumentNullException(nameof(systemType)) : new Il2CppClass(Il2CppApi.class_from_system_type(systemType));

    /// <summary><c>def&lt;args&gt;</c>: a generic class instantiated with <paramref name="args"/> (it must exist in the game).</summary>
    public static Il2CppClass Instantiate(Il2CppClass def, params Il2CppClass[] args)
    {
        var types = Il2Cpp.NewArray(Il2Cpp.RequireClass("mscorlib.dll", "System.Type"), args.Length);
        for (var i = 0; i < args.Length; i++)
        {
            ((nint*)Il2Cpp.ArrayData(types))[i] = args[i].SystemType;
        }
        return ClassOf(Unity.Call(def.SystemType, "MakeGenericType", types));
    }

    /// <summary>The generic arguments of an instantiated class (<c>List&lt;int&gt;</c> → <c>[int]</c>).</summary>
    public static Il2CppClass[] GenericArgs(Il2CppClass k) =>
        Il2Cpp.ArrayObjects(Unity.Call(k.SystemType, "GetGenericArguments")).Select(ClassOf).ToArray();

    /// <summary><c>System.Action&lt;T&gt;</c> for <paramref name="arg"/>.</summary>
    public static Il2CppClass ActionOf(Il2CppClass arg) => Instantiate(Il2Cpp.RequireClass("mscorlib.dll", "System.Action`1"), arg);

    /// <summary>A new delegate of class <paramref name="delegateClass"/> calling <paramref name="function"/> with <paramref name="ctx"/> first.</summary>
    public static nint Native(Il2CppClass delegateClass, nint function, nint ctx)
    {
        var invoke = delegateClass.Method("Invoke", -1);
        if (invoke.IsNull)
        {
            throw new InvalidOperationException($"{delegateClass} is not a delegate");
        }
        var d = delegateClass.New();
        foreach (var (name, v) in new[] { ("method_ptr", function), ("invoke_impl", function), ("method", invoke.Ptr), ("method_code", ctx) })
        {
            var f = delegateClass.Field(name);
            if (f.IsNull)
            {
                throw new InvalidOperationException($"delegate field {name} missing");
            }
            f.Set(d, v);
        }
        return d;
    }

    /// <summary>Calls a delegate's <c>Invoke</c> (to notify the game's own listeners).</summary>
    public static nint Invoke(nint del, params nint[] args)
    {
        var m = Il2Cpp.ClassOf(del).Method("Invoke", args.Length);
        return m.IsNull ? throw new InvalidOperationException("not a delegate") : m.Invoke(del, args);
    }

    /// <summary>Whether a value of <paramref name="size"/> bytes is passed by value in a register (Windows x64).</summary>
    public static bool InRegister(int size) => size is 1 or 2 or 4 or 8;

    /// <summary>A new <c>System.Object</c> (an owner key for registries keyed by owner).</summary>
    public static nint NewOwner() => Il2Cpp.RequireClass("mscorlib.dll", "System.Object").New();
}
