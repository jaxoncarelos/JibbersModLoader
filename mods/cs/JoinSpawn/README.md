# Join Spawn

A mod for **Jibbers Playtest**, built on [JibbersModLoader](https://github.com/jaxoncarelos/JibbersModLoader).
It makes you spawn next to a player in your session instead of at the normal spawn point.

## Features
- **Pick a player** from a list of online players, with the distance to each one.
- **Adjustable offset**: forward/back, left/right and height.
- **Motion copy**: you spawn facing the player's direction of travel and with their speed (can be turned off).
- **Two references**: "Their direction" or "World axes".
- **Far-away players**: you can also pick a distant player, using the last known position of their nameplate.
- **Custom controls**: two keyboard keys and one gamepad button to open the panel, all changeable in the mod menu.
- **In-game panel**: navigate with the arrow keys or the D-pad.
- **Bilingual** (English / French): the panel follows the Windows language, or pick it in the "Language / Langue" setting. Mod menu labels show both languages.

## Installation
1. Install JibbersModLoader in the game.
2. Copy `JoinSpawn.dll` into `<game folder>\JibbersMods\cs\`.
3. Restart the game.

The mod shows a "not from the signed catalog" warning when it loads. This is normal for a personal mod.

## Usage
- **F6** (or **Insert**): open/close the panel.
- Up/Down: choose, Left/Right: adjust, Enter: confirm.
- **F10**: writes a diagnostic to `jibbers_hotload.log`.

## Building
Requirements: .NET 8 SDK and the game with the modloader installed.

    dotnet build -c Release

The DLL is copied to `<game>\JibbersMods\cs\` if the game is found. For a different game folder:

    dotnet build -c Release -p:JibbersGame="D:\Games\Jibbers Playtest"

Close the game before building (it locks the DLL).

## Known limitations
- Players are found through their nameplates, not their characters.
- Their direction is inferred from recent movement, not from their real orientation.
- The position of a far-away player may be out of date.
- The copied speed is applied as a world-space velocity; not verified in game.
- Automatic respawn together with another player was removed because it did not work.
