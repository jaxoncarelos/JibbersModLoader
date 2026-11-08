using Jibbers.Sdk;

namespace HankModPack;

/// <summary>One native camera hook for the pack; each feature has an independent lifetime.</summary>
internal sealed class CameraCallbacks
{
    internal sealed record Entry(object Owner, Func<bool> Enabled, Action<nint>? Before,
        Action<nint>? After, Action<Exception> Report, int Order = 0);

    readonly List<Entry> entries = new();
    public static CameraCallbacks Follow { get; } = new();
    public static CameraCallbacks Recorder { get; } = new();

    internal void Register(Entry entry) { lock (entries) entries.Add(entry); }
    public void Register(PackPart owner, Action<nint>? before, Action<nint>? after, int order = 0) =>
        Register(new(owner, () => owner.Enabled, before, after, error => owner.Log("camera callback: " + error.Message), order));

    public void Remove(object owner) { lock (entries) entries.RemoveAll(entry => ReferenceEquals(entry.Owner, owner)); }

    public void Invoke(nint camera, Action original)
    {
        Entry[] snapshot;
        lock (entries) snapshot = entries.OrderBy(entry => entry.Order).ToArray();
        foreach (var entry in snapshot) Run(entry, entry.Before, camera);
        // A feature cannot swallow, defer, or accidentally call the game's update twice.
        original();
        foreach (var entry in snapshot) Run(entry, entry.After, camera);
    }

    static void Run(Entry entry, Action<nint>? callback, nint camera)
    {
        try { if (entry.Enabled()) callback?.Invoke(camera); }
        catch (Exception error) { try { entry.Report(error); } catch { } }
    }
}
