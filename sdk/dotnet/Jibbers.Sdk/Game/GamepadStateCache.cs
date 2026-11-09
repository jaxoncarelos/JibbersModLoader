namespace Jibbers.Sdk;

// One connection backoff shared by every mod, including explicitly indexed reads.
internal sealed class GamepadStateCache(Func<int, PadState> poll, Func<long> clock)
{
    readonly object gate = new();
    readonly PadState[] states = new PadState[4];
    readonly long[] nextRead = [long.MinValue, long.MinValue, long.MinValue, long.MinValue];
    int preferred = -1;

    public PadState Read(int index)
    {
        if (index is < 0 or > 3) return default;
        lock (gate) return ReadCore(index);
    }

    PadState ReadCore(int index)
    {
        var now = clock();
        if (now < nextRead[index]) return states[index];
        var state = poll(index);
        states[index] = state;
        nextRead[index] = now + (state.Connected ? 1 : 2000);
        return state;
    }

    public PadState ReadAny()
    {
        lock (gate)
        {
            if (preferred >= 0 && ReadCore(preferred) is { Connected: true } current) return current;
            preferred = -1;
            for (var index = 0; index < states.Length; index++)
                if (ReadCore(index) is { Connected: true } found)
                { preferred = index; return found; }
            return default;
        }
    }
}
