namespace Reshred;

// A cheap topology probe may repeat; a complete binding inventory must not repeat while stable.
internal sealed class BindingRefreshPolicy
{
    object? previous;
    long nextProbe;
    bool invalid = true;
    public void Invalidate() { invalid = true; nextProbe = 0; }
    public bool ShouldRefresh(long now, Func<object> probe)
    {
        if (now < nextProbe) return false;
        nextProbe = now + 750;
        var current = probe();
        var refresh = invalid || !Equals(previous, current);
        previous = current;
        invalid = false;
        return refresh;
    }
    public void RetryAfter(long now) { invalid = true; nextProbe = now + 5000; }
}
