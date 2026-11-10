namespace Jibbers.Sdk;

/// <summary>Attributes callbacks correctly when a shared hook dispatches features owned by several mods.</summary>
public static class ModCallbacks
{
    /// <summary>Invokes an enabled owner's callback with its lifetime and performance attribution.</summary>
    public static void Invoke<T>(JibbersMod owner, Action<T> callback, T argument)
    {
        if (!owner.Enabled) return;
        using var scope = ModContext.Enter(owner.Id);
        callback(argument);
    }
}
