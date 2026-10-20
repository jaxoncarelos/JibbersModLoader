using Jibbers.Sdk.Interop;

namespace Jibbers.Sdk;

/// <summary>A typed handle to a game object: every generated <c>Game.*</c> and <c>Jibbers.Interop.*</c> class type implements it.</summary>
public interface IGameObject
{
    /// <summary>The IL2CPP object pointer.</summary>
    nint Ptr { get; }
}

/// <summary>Helpers for every typed handle.</summary>
public static class GameObjectExtensions
{
    /// <summary>Null, or a destroyed Unity object.</summary>
    public static bool IsDead<T>(this T o) where T : IGameObject => Unity.IsNull(o.Ptr);

    /// <summary>The runtime class of the object.</summary>
    public static Il2CppClass RuntimeClass<T>(this T o) where T : IGameObject => Il2Cpp.ClassOf(o.Ptr);

    /// <summary>The object described as JSON (for logs).</summary>
    public static string Describe<T>(this T o, int depth = 1) where T : IGameObject => Inspect.Describe(o.Ptr, depth).ToJsonString();

    /// <summary>The Unity object's name.</summary>
    public static string UnityName<T>(this T o) where T : IGameObject => Unity.Name(o.Ptr);

    /// <summary>Keeps the object alive across frames (dispose when done).</summary>
    public static GameRef Keep<T>(this T o) where T : IGameObject => new(o.Ptr);
}
