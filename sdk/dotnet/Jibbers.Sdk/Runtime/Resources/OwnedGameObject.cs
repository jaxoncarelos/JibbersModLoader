using Jibbers.Sdk.Interop;

namespace Jibbers.Sdk;
/// <summary>Owns a GameObject the mod created, including its rooted reference. Disposing destroys it.</summary>
public sealed class OwnedGameObject : IDisposable
{
    GameRef? reference;
    bool destroyOnDispose = true;
    /// <summary>The GameObject, or 0 once disposed.</summary>
    public nint Target => reference?.Target ?? 0;
    /// <summary>The GameObject's Transform.</summary>
    public nint Transform => Unity.Transform(Target);

    OwnedGameObject(nint gameObject) => reference = new GameRef(gameObject);
    /// <summary>Creates a named GameObject.</summary>
    public static OwnedGameObject Create(string name)
    {
        var gameObjectClass = Unity.Class("UnityEngine.GameObject");
        var instance = gameObjectClass.New();
        var owned = new OwnedGameObject(instance);
        try
        {
            gameObjectClass.Method(".ctor", "System.String").Invoke(instance, Il2Cpp.String(name));
            return owned;
        }
        catch
        {
            owned.Dispose();
            throw;
        }
    }

    /// <summary>Adds a component by Unity type name and returns it.</summary>
    public nint AddComponent(string typeName) => Il2Cpp.ClassOf(Target).Method("AddComponent", "System.Type").Invoke(Target, Unity.Class(typeName).SystemType);
    /// <summary>Parents this object under <paramref name="parent"/> (a Transform); the parent's owner then destroys it.</summary>
    public unsafe void AttachTo(nint parent)
    {
        var keepWorldPosition = (byte)0;
        Il2Cpp.ClassOf(Transform).Method("SetParent", "UnityEngine.Transform", "System.Boolean").Invoke(Transform, parent, (nint)(&keepWorldPosition));
        destroyOnDispose = false; // The parent's owner now destroys this child.
    }

    /// <summary>Destroys the GameObject (unless attached to a parent) and releases the reference.</summary>
    public void Dispose()
    {
        var owned = reference;
        reference = null;
        if (owned is null)
            return;
        try
        {
            if (destroyOnDispose && !Unity.IsNull(owned.Target))
                Unity.Destroy(owned.Target);
        }
        finally
        {
            owned.Dispose();
        }
    }
}
