namespace Jibbers.Sdk.Multiplayer;

/// <summary>A Steam account (64-bit Steam ID). <see cref="None"/> is "nobody".</summary>
public readonly record struct SteamId(ulong Value)
{
    /// <summary>No account.</summary>
    public static SteamId None => default;
    /// <summary>True for a real account.</summary>
    public bool IsValid => Value != 0;
    /// <summary>The decimal Steam ID, as Steam shows it.</summary>
    public override string ToString() => Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
    /// <summary>Parses a decimal Steam ID; false for anything else.</summary>
    public static bool TryParse(string? text, out SteamId id)
    {
        var ok = ulong.TryParse(text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var v) && v != 0;
        id = ok ? new SteamId(v) : None;
        return ok;
    }
}
