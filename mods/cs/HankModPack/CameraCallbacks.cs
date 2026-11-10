using Jibbers.Sdk;

namespace HankModPack;

/// <summary>One native camera hook for the pack; each feature has an independent lifetime.</summary>
internal sealed class CameraCallbacks
{
    internal sealed record Entry(object Owner, Func<bool> Enabled, Action<nint>? Before,
        Action<nint>? After, Action<Exception> Report, int Order = 0)
    {
        public bool Failed { get; set; }
    }

    readonly List<Entry> entries = new();
    Entry[] ordered = Array.Empty<Entry>();
    public static CameraCallbacks Follow { get; } = new();
    public static CameraCallbacks Recorder { get; } = new();

    internal void Register(Entry entry)
    {
        lock (entries)
        {
            entries.Add(entry);
            Volatile.Write(ref ordered, entries.OrderBy(item => item.Order).ToArray());
        }
    }
    public void Register(PackPart owner, Action<nint>? before, Action<nint>? after, int order = 0) =>
        Register(new(owner, () => owner.Enabled, before, after, owner.CameraFailed, order));

    public void Remove(object owner)
    {
        lock (entries)
        {
            entries.RemoveAll(entry => ReferenceEquals(entry.Owner, owner));
            Volatile.Write(ref ordered, entries.OrderBy(item => item.Order).ToArray());
        }
    }

    public void Invoke(nint camera, Action original)
    {
        var snapshot = Volatile.Read(ref ordered);
        foreach (var entry in snapshot) Run(entry, entry.Before, camera);
        // A feature cannot swallow, defer, or accidentally call the game's update twice.
        original();
        foreach (var entry in snapshot) Run(entry, entry.After, camera);
    }

    static void Run(Entry entry, Action<nint>? callback, nint camera)
    {
        if (entry.Failed) return;
        try
        {
            if (entry.Enabled() && callback is not null)
            {
                if (entry.Owner is JibbersMod mod) ModCallbacks.Invoke(mod, callback, camera);
                else callback(camera);
            }
        }
        catch (Exception error)
        {
            entry.Failed = true;
            try { entry.Report(error); } catch { }
        }
    }
}
