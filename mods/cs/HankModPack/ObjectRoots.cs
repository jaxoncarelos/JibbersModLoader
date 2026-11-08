using Jibbers.Sdk;

namespace HankModPack;

/// <summary>Keep cached IL2CPP handles alive until their scene or cache is released.</summary>
internal sealed class ObjectRoots : IDisposable
{
    readonly Dictionary<nint, GameRef> roots = new();
    public void Keep(nint pointer)
    {
        if (pointer != 0 && !roots.ContainsKey(pointer)) roots.Add(pointer, new GameRef(pointer));
    }
    public void Dispose()
    {
        foreach (var root in roots.Values) root.Dispose();
        roots.Clear();
    }
}
