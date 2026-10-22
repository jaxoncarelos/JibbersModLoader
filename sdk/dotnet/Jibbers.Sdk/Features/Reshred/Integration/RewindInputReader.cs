namespace Jibbers.Sdk.Features.Rewind;

/// <summary>Reserves gameplay bindings while polling keyboard, Rewired and Xbox-compatible controllers.</summary>
internal sealed class RewindInputReader : IRewindInput
{
    readonly RewiredReservation reservation = new();
    readonly bool[] connectedControllers = new bool[4];
    uint frameNumber;
    public RewindInputStatus Status => new(reservation.MaskedCount, reservation.PadCount, reservation.LastError);

    public RewindInputState Read(RewindOptions options)
    {
        reservation.Update(options.Enabled, options.KeyboardKey, options.UseController);
        var held = options.Enabled && Input.GameFocused && ((options.KeyboardKey != 0 && Input.IsDown(options.KeyboardKey)) || (options.UseController && (reservation.PadDown() || WestButtonHeld())));
        return new(held, Input.Typing);
    }

    bool WestButtonHeld()
    {
        frameNumber++;
        var probeEmptySlots = frameNumber == 1 || frameNumber % 180 == 0;
        var held = false;
        for (var index = 0; index < connectedControllers.Length; index++)
        {
            if (!connectedControllers[index] && !probeEmptySlots)
                continue;
            var state = Gamepad.Read(index);
            connectedControllers[index] = state.Connected;
            held |= state.Connected && state.Held(PadButton.X);
        }

        return held;
    }

    public void RestoreBindings() => reservation.Restore();
    public void Dispose() => RestoreBindings();
}
