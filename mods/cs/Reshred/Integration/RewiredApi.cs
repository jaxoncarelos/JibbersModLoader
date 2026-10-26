using Jibbers.Sdk;
using Jibbers.Sdk.Interop;

namespace Reshred;

/// <summary>Checked Rewired reflection and enumeration. All raw values are validated at this boundary.</summary>
internal sealed unsafe class RewiredApi
{
    const int MaxItems = 16384;
    readonly Dictionary<(nint, string, int, string), Il2CppMethod> methods = new();
    /// <summary>Exact name first, explicit-interface suffix second, inherited methods included.</summary>
    public Il2CppMethod Resolve(Il2CppClass declaringClass, string name, int parameterCount, string returnType)
    {
        var key = (declaringClass.Ptr, name, parameterCount, returnType);
        if (methods.TryGetValue(key, out var hit))
        {
            return hit;
        }

        Il2CppMethod? explicitImplementation = null;
        for (var ancestor = declaringClass; !ancestor.IsNull; ancestor = ancestor.Parent)
        {
            foreach (var method in ancestor.Methods)
            {
                if (method.ParameterCount != parameterCount)
                {
                    continue;
                }

                var actual = method.Name;
                if (actual != name && !actual.EndsWith("." + name))
                {
                    continue;
                }

                var result = method.ReturnType;
                if ((returnType.Length > 0 && result != returnType) || result.Contains("<T>"))
                {
                    continue;
                }

                if (actual == name)
                {
                    return methods[key] = method;
                }

                explicitImplementation ??= method;
            }
        }

        return explicitImplementation is { } explicitMethod ? methods[key] = explicitMethod : throw new InvalidOperationException($"Rewired method {name}/{parameterCount} -> {returnType} not found");
    }

    public nint Call(nint instance, string name, string returnType = "", params nint[] arguments)
    {
        if (instance == 0)
        {
            throw new InvalidOperationException($"Rewired {name} on null");
        }

        return Resolve(Il2Cpp.ClassOf(instance), name, arguments.Length, returnType).Invoke(instance, arguments);
    }

    /// <summary>A bool result, checked: never construct a bool from unchecked bytes.</summary>
    public bool AsBool(nint boxed, string operation)
    {
        var valueClass = Il2Cpp.ClassOf(boxed);
        if (boxed == 0 || valueClass.FullName != "System.Boolean")
        {
            throw new InvalidOperationException($"Rewired {operation} returned an unexpected type");
        }

        var encodedBoolean = *(byte*)Il2CppApi.object_unbox(boxed);
        return encodedBoolean switch
        {
            0 => false,
            1 => true,
            _ => throw new InvalidOperationException("Rewired returned an invalid Boolean"),
        };
    }

    /// <summary>An int (or int-backed enum) result, checked.</summary>
    public int AsInt(nint boxed, string operation)
    {
        var valueClass = Il2Cpp.ClassOf(boxed);
        var validScalar = boxed != 0 && valueClass.IsValueType && valueClass.ValueSize == 4 && (valueClass.FullName == "System.Int32" || (valueClass.IsEnum && valueClass.Field("value__").TypeName == "System.Int32"));
        return validScalar ? *(int*)Il2CppApi.object_unbox(boxed) : throw new InvalidOperationException($"Rewired {operation} returned an unexpected scalar type");
    }

    public bool Bool(nint instance, string name) => AsBool(Call(instance, name), name);
    public int Int(nint instance, string name) => AsInt(Call(instance, name), name);
    public string Text(nint instance, string name) => Il2Cpp.ReadString(Call(instance, name, "System.String"));
    /// <summary>An instance object-reference field, validated before reading.</summary>
    public nint ReferenceField(nint instance, string name)
    {
        var field = Il2Cpp.ClassOf(instance).Field(name);
        if (field.IsNull || field.IsStatic || field.TypeClass.IsValueType)
        {
            throw new InvalidOperationException($"Rewired field {name} is not an instance object reference");
        }

        return field.GetObject(instance);
    }

    public nint ReInput(string name) => Unity.CallStatic(Il2Cpp.RequireClass("Rewired_Core.dll", "Rewired.ReInput"), name);
    /// <summary>Walks a managed IEnumerable; never assumes a List or array layout. Every item is rooted.</summary>
    public RewiredObjectCollection Items(nint collection)
    {
        using var collectionRoot = new GameRef(collection);
        var collectionClass = Il2Cpp.ClassOf(collection);
        Il2CppMethod getEnumerator;
        try
        {
            getEnumerator = Resolve(collectionClass, "GetEnumerator", 0, "System.Collections.IEnumerator");
        }
        catch (InvalidOperationException)
        {
            getEnumerator = Resolve(collectionClass, "GetEnumerator", 0, "");
        }

        var enumerator = getEnumerator.Invoke(collectionRoot.Target);
        using var enumeratorRoot = new GameRef(enumerator);
        var enumeratorClass = Il2Cpp.ClassOf(enumerator);
        var enumeratorInstance = enumeratorClass.IsValueType ? (nint)Il2CppApi.object_unbox(enumerator) : enumerator;
        var moveNext = Resolve(enumeratorClass, "MoveNext", 0, "System.Boolean");
        Il2CppMethod current;
        try
        {
            current = Resolve(enumeratorClass, "get_Current", 0, "System.Object");
        }
        catch (InvalidOperationException)
        {
            current = Resolve(enumeratorClass, "get_Current", 0, "");
        }

        var items = new List<GameRef>();
        try
        {
            while (AsBool(moveNext.Invoke(enumeratorInstance), "MoveNext"))
            {
                if (items.Count >= MaxItems)
                {
                    throw new InvalidOperationException("Rewired enumeration limit exceeded");
                }

                var item = current.Invoke(enumeratorInstance);
                if (item != 0)
                {
                    items.Add(new GameRef(item));
                }
            }
        }
        catch
        {
            Cleanup.Run(items.Select(item => (Action)item.Dispose).ToArray());
            throw;
        }
        finally
        {
            try
            {
                Resolve(enumeratorClass, "Dispose", 0, "System.Void").Invoke(enumeratorInstance);
            }
            catch (Exception)
            {
            // optional
            }
        }

        return new RewiredObjectCollection(items);
    }

    public void SetEnabled(nint instance, bool enabled)
    {
        var setter = Resolve(Il2Cpp.ClassOf(instance), "set_enabled", 1, "System.Void");
        if (setter.ParameterTypes[0] != "System.Boolean")
        {
            throw new InvalidOperationException("Rewired set_enabled parameter is not Boolean");
        }

        var encodedValue = enabled;
        setter.Invoke(instance, (nint)(&encodedValue));
    }
}
