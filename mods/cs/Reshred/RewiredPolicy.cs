using Jibbers.Sdk;

// The game-free parts of Reshred: scrub arithmetic and the Rewired category
// and button-name policies. Unit tested.
namespace Reshred;

/// <summary>Rewired's conventions: which maps are gameplay and which button is "west".</summary>
internal static class RewiredPolicy
{
    static string Normalized(string text) => new(text.Where(char.IsAsciiLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
    /// <summary>
    /// An allowlist, not "everything except UI": category ids are not stable, and shared/default
    /// navigation maps must never be masked. This build's categories include Ragdoll, UI, Replay,
    /// Default, Default - Debug and SpawnSelector; only Ragdoll is gameplay-owned.
    /// </summary>
    public static bool GameplayCategory(string name) => Normalized(name)is "gameplay" or "game" or "ingame" or "player" or "playergameplay" or "ski" or "skiing" or "skier" or "skiinggameplay" or "ragdoll" or "ragdollcontrols" or "ragdollgameplay" or "gameplayragdoll";
    /// <summary>Whether a controller button is the west face button. Nintendo's X is north: never guess X on unknown devices.</summary>
    public static bool WestName(string name, string hardware)
    {
        var buttonName = Normalized(name);
        var hardwareName = Normalized(hardware);
        if (buttonName is "square" or "squarebutton" or "buttonsquare" or "west" or "buttonwest" or "westbutton" or "facebuttonwest" or "leftfacebutton" or "actionleft" or "xsquare" or "buttonxsquare")
        {
            return true;
        }

        var xbox = hardwareName.Contains("xbox") || hardwareName.Contains("xinput") || hardwareName.Contains("microsoft");
        return xbox && buttonName is "x" or "buttonx" or "xbutton";
    }
}
