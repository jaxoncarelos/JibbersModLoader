using Jibbers.Sdk;
using Jibbers.Sdk.Interop;

namespace HankModPack;

/// <summary>Hide only the requested UI and restore its exact scale, including non-unit layouts.</summary>
internal sealed class UiVisibility : IDisposable
{
    readonly Dictionary<nint, (GameRef Root, Vector3 Scale)> hidden = new();
    public int Count => hidden.Count;

    public void Hide(nint transform)
    {
        if (transform == 0 || Unity.IsNull(transform)) return;
        if (!hidden.ContainsKey(transform))
            hidden.Add(transform, (new GameRef(transform), Unity.Call<Vector3>(transform, "get_localScale")));
        UnityProperties.Set(transform, "set_localScale", new Vector3(0, 0, 0));
    }

    public void Dispose()
    {
        foreach (var (root, scale) in hidden.Values)
        {
            try
            {
                if (!Unity.IsNull(root.Target))
                {
                    var current = Unity.Call<Vector3>(root.Target, "get_localScale");
                    if (current.X == 0 && current.Y == 0 && current.Z == 0)
                        UnityProperties.Set(root.Target, "set_localScale", scale);
                }
            }
            catch { /* A destroyed panel must not prevent other panels from restoring. */ }
            finally { root.Dispose(); }
        }
        hidden.Clear();
    }
}
