using Jibbers.Sdk.Interop;

namespace Jibbers.Sdk;
/// <summary>Backend ownership of a created Unity GameObject, including its rooted reference.</summary>
internal sealed class OwnedGameObject : IDisposable
{
    GameRef? reference;
    bool destroyOnDispose = true;
    public nint Target => reference?.Target ?? 0;
    public nint Transform => Unity.Transform(Target);

    OwnedGameObject(nint gameObject) => reference = new GameRef(gameObject);
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

    public nint AddComponent(string typeName) => Il2Cpp.ClassOf(Target).Method("AddComponent", "System.Type").Invoke(Target, Unity.Class(typeName).SystemType);
    public unsafe void AttachTo(nint parent)
    {
        var keepWorldPosition = (byte)0;
        Il2Cpp.ClassOf(Transform).Method("SetParent", "UnityEngine.Transform", "System.Boolean").Invoke(Transform, parent, (nint)(&keepWorldPosition));
        destroyOnDispose = false; // The parent's owner now destroys this child.
    }

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
