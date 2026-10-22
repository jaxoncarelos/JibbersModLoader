// The game-free parts of Reshred: scrub arithmetic, the Rewired key-code table and the category
// and button-name policies. Unit tested (ported from the Rust tests).

namespace Jibbers.Sdk.Features.Rewind;

internal static class Rewind
{
    /// <summary>Seconds of history kept: how far back the rewind can go.</summary>
    public const float History = 6.0f;

    /// <summary>How many history frames to step back this physics step at <paramref name="speed"/> (carrying the remainder).</summary>
    public static int ScrubSteps(ref float acc, float speed)
    {
        acc += MathF.Max(speed, 0);
        var whole = MathF.Floor(acc);
        acc -= whole;
        return (int)whole;
    }

    /// <summary>Index of the frame to show after stepping back <paramref name="k"/> frames from a history of <paramref name="len"/>: never before the oldest.</summary>
    public static int? ScrubTarget(int len, int k) => len == 0 ? null : Math.Max(0, len - 1 - k);

    /// <summary>Frames kept for <see cref="History"/> seconds at physics step <paramref name="dt"/>.</summary>
    public static int Capacity(float dt) => (int)(History / dt) + 1;
}

/// <summary>Rewired's conventions: which maps are gameplay, how keys are coded, which button is "west".</summary>
internal static class RewiredPolicy
{
    static string Normalized(string s) => new(s.Where(char.IsAsciiLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    /// <summary>
    /// An allowlist, not "everything except UI": category ids are not stable, and shared/default
    /// navigation maps must never be masked. This build's categories include Ragdoll, UI, Replay,
    /// Default, Default - Debug and SpawnSelector; only Ragdoll is gameplay-owned.
    /// </summary>
    public static bool GameplayCategory(string name) => Normalized(name) is "gameplay" or "game" or "ingame" or "player" or "playergameplay"
        or "ski" or "skiing" or "skier" or "skiinggameplay" or "ragdoll" or "ragdollcontrols" or "ragdollgameplay" or "gameplayragdoll";

    /// <summary>Win32 virtual key → Rewired.KeyboardKeyCode (Unity's values); null for keys with no safe mapping.</summary>
    public static int? KeyboardCode(int vk) => vk switch
    {
        >= 0x41 and <= 0x5a => vk + 32,
        >= 0x30 and <= 0x39 => vk,
        0x08 => 8, 0x09 => 9, 0x0d => 13, 0x1b => 27, 0x20 => 32,
        0x2e => 127, 0x26 => 273, 0x28 => 274, 0x27 => 275, 0x25 => 276,
        0x2d => 277, 0x24 => 278, 0x23 => 279, 0x21 => 280, 0x22 => 281,
        >= 0x70 and <= 0x7e => vk - 0x70 + 282,
        >= 0x60 and <= 0x69 => vk - 0x60 + 256,
        0x6e => 266, 0x6f => 267, 0x6a => 268, 0x6d => 269, 0x6b => 270,
        0xba => 59, 0xbb => 61, 0xbc => 44, 0xbd => 45, 0xbe => 46,
        0xbf => 47, 0xc0 => 96, 0xdb => 91, 0xdc => 92, 0xdd => 93, 0xde => 39,
        _ => null,
    };

    /// <summary>Whether a controller button is the west face button. Nintendo's X is north: never guess X on unknown devices.</summary>
    public static bool WestName(string name, string hardware)
    {
        var n = Normalized(name);
        var h = Normalized(hardware);
        if (n is "square" or "squarebutton" or "buttonsquare" or "west" or "buttonwest" or "westbutton" or "facebuttonwest"
            or "leftfacebutton" or "actionleft" or "xsquare" or "buttonxsquare")
        {
            return true;
        }
        var xbox = h.Contains("xbox") || h.Contains("xinput") || h.Contains("microsoft");
        return xbox && n is "x" or "buttonx" or "xbutton";
    }
}
