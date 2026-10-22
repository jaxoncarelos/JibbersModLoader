using Jibbers.Sdk.Interop;

namespace Jibbers.Sdk;

/// <summary>One native setter boundary for scalar and vector component properties.</summary>
internal static unsafe class UnityProperties
{
    public static void Set<T>(nint instance, string setter, T value)
        where T : unmanaged => Unity.Call(instance, setter, (nint)(&value));
}
