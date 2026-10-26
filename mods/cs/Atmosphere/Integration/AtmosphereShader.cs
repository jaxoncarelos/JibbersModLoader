using Jibbers.Sdk;
using Jibbers.Sdk.Interop;

namespace Atmosphere;

/// <summary>Shader globals by name (ids cached).</summary>
static unsafe class AtmosphereShader
{
    static readonly Dictionary<string, int> propertyIds = new();
    static Il2CppMethod setGlobalVector, setGlobalFloat, propertyToId;
    static void EnsureInitialized()
    {
        if (!setGlobalVector.IsNull)
        {
            return;
        }

        var shaderClass = Il2Cpp.RequireClass(Unity.Core, "UnityEngine.Shader");
        setGlobalVector = shaderClass.Method("SetGlobalVector", "System.Int32", "UnityEngine.Vector4");
        setGlobalFloat = shaderClass.Method("SetGlobalFloat", "System.Int32", "System.Single");
        propertyToId = shaderClass.Method("PropertyToID", "System.String");
        if (setGlobalVector.IsNull || setGlobalFloat.IsNull || propertyToId.IsNull)
        {
            throw new InvalidOperationException("Shader.SetGlobalVector / SetGlobalFloat / PropertyToID missing");
        }
    }

    public static int PropertyId(string propertyName)
    {
        EnsureInitialized();
        if (!propertyIds.TryGetValue(propertyName, out var propertyId))
        {
            propertyIds[propertyName] = propertyId = propertyToId.Invoke<int>(0, Il2Cpp.String(propertyName));
        }

        return propertyId;
    }

    public static void SetVector(string propertyName, Vector4 value)
    {
        var propertyId = PropertyId(propertyName);
        setGlobalVector.Invoke(0, (nint)(&propertyId), (nint)(&value));
    }

    public static void SetFloat(string propertyName, float value)
    {
        var propertyId = PropertyId(propertyName);
        setGlobalFloat.Invoke(0, (nint)(&propertyId), (nint)(&value));
    }
}
