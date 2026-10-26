using Jibbers.Sdk.Interop;

namespace Jibbers.Sdk;

/// <summary>One native setter boundary for scalar and vector component properties.</summary>
public static unsafe class UnityProperties
{
    /// <summary>Calls a property setter such as <c>set_volume</c> with an unmanaged value (float, Vector3, ...).</summary>
    public static void Set<T>(nint instance, string setter, T value)
        where T : unmanaged => Unity.Call(instance, setter, (nint)(&value));
}
