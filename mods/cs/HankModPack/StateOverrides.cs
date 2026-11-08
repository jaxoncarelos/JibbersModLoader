namespace HankModPack;

/// <summary>Restores the original value after the last overlapping owner releases a control.</summary>
internal sealed class StateOverrides<TKey, TValue> where TKey : notnull
{
    sealed class State(TValue original, Action<TValue> write)
    {
        public readonly TValue Original = original;
        public readonly Action<TValue> Write = write;
        public readonly List<(object Owner, TValue Value)> Owners = new();
    }

    readonly Dictionary<TKey, State> states = new();

    public IDisposable Acquire(TKey key, Func<TValue> read, Action<TValue> write, TValue value)
    {
        var state = states.TryGetValue(key, out var existing) ? existing : new State(read(), write);
        state.Write(value); // Do not record a lease if applying it failed.
        states[key] = state;
        var owner = new object();
        state.Owners.Add((owner, value));
        return new Lease(() =>
        {
            var index = state.Owners.FindIndex(item => ReferenceEquals(item.Owner, owner));
            if (index < 0) return;
            var wasLast = index == state.Owners.Count - 1;
            state.Owners.RemoveAt(index);
            if (state.Owners.Count == 0) states.Remove(key);
            if (wasLast) state.Write(state.Owners.Count == 0 ? state.Original : state.Owners[^1].Value);
        });
    }

    sealed class Lease(Action release) : IDisposable
    {
        Action? pending = release;
        public void Dispose() => Interlocked.Exchange(ref pending, null)?.Invoke();
    }
}
