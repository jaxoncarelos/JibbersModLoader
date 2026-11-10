namespace Jibbers.Sdk;

// Disable entries first, wait for the old native stack to unwind, then release mod resources.
internal sealed class PerformanceSuspensions(Action<string> disableEntries, Action<JibbersMod> unload,
    Func<string, bool> idle, Action<Exception> failed)
{
    readonly List<(JibbersMod Mod, long After)> pending = new();
    public bool Suspend(JibbersMod mod, long frame, string reason)
    {
        if (mod.PerformanceSuspended) return false;
        mod.SuspensionReason = reason; mod.PerformanceSuspended = true;
        try { disableEntries(mod.Id); }
        catch (Exception error)
        {
            mod.PerformanceSuspended = false; mod.SuspensionReason = "";
            failed(error); return false;
        }
        pending.Add((mod, frame + 2));
        return true;
    }
    public void Frame(long frame)
    {
        for (var index = pending.Count - 1; index >= 0; index--)
        {
            var item = pending[index];
            if (frame < item.After || !idle(item.Mod.Id)) continue;
            pending.RemoveAt(index);
            try { unload(item.Mod); } catch (Exception error) { failed(error); }
        }
    }
    public void Shutdown()
    {
        foreach (var item in pending)
            try { unload(item.Mod); } catch (Exception error) { failed(error); }
        pending.Clear();
    }
}
