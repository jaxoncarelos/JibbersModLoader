# SLVSH

S.K.A.T.E. on snow, with your party.

- Players take turns **setting**. The setter lands any trick.
- Everyone else gets **one try** to land the same trick. Miss it (bail, a different trick, or run
  out of time) and you get the next letter: S, L, V, S, H.
- The setter keeps setting until they miss their own set. Then the next player sets, and missing
  your own set gives no letter.
- Spell SLVSH and you're out. The last rider standing wins.

Everyone who plays needs this mod. It replaces the original native SLVSH mod (`jibbers_slvsh.dll`);
don't install both.

## How to play

1. Get everyone into a **party**: press **Ctrl+M** for the Party panel, make a party, and share its
   code (or invite friends from the panel).
2. One rider presses **Ctrl+Enter** to start a game. Everyone else in the party sees an invite and
   presses **Ctrl+Enter** to join.
3. When everyone's in, the starter presses **Ctrl+Enter** again to begin (2 to 8 players).
4. **Watch every turn.** While someone takes their turn, your camera follows their rider, so you
   see the whole line and trick. It comes back to you for your own turn. This only works when
   they're on the same server as you; otherwise the scoreboard says "Not on your server".
5. **Vote.** After each try, everyone else votes: **Y** it counted, **N** it didn't. The
   scoreboard shows what the game detected ("game's guess"). The majority decides; a tie, or
   nobody voting, goes with the game's guess. You can change your vote until time's up.
6. **Ctrl+Q** leaves the game. If the starter ends it, it ends for everyone.

## How a try is detected

- Your **first trick** after your turn starts is your try. It counts once you ride away clean:
  no respawn and no more combo for about two seconds. Rails count when you come off.
- Bailing (the game gives no trick), or respawning after your trick, is a miss. Respawning
  *before* your trick is fine, so you can get back up to the jump.
- No trick before the timer runs out is a miss, with no vote.
- A set where the game saw no trick at all isn't voted on: there's no trick to match.

## Settings (Mods > SLVSH)

- Keys: start / join / begin (Ctrl+Enter), leave (Ctrl+Q), vote it counted (Y) and it didn't (N),
  which only work while a vote is open. No F-keys, so nothing clashes with the drone or Alpine Vision.
- **A match counts when**: exact trick (default), same spins with any grab, or any landed trick.
- **Time for each try**: 45 seconds by default.
- **Vote on every try** and **time to vote** (12 seconds). Turn voting off to let trick detection
  decide everything.
- **Start every try on the setter's line** (on by default) and **run-in before the trick** (6
  seconds). When it's your turn to match, you start where the setter's run-in began, at their speed;
  respawning before your trick puts you back there.
- **Watch each player's turn**: turn off to keep your own camera.

The starter's rules are used for the whole game; the camera setting is your own.

## For modders

| File | What it does |
| --- | --- |
| [SlvshMod.cs](SlvshMod.cs) | Entry point: party channel, keys, the game thread, camera and overlay |
| [SlvshRules.cs](SlvshRules.cs) | The rules and votes (`SlvshHost`) and the state the host sends everyone |
| [TrickJudge.cs](TrickJudge.cs) | Detects an attempt from the game's trick and respawn events |
| [Scoreboard.cs](Scoreboard.cs) | Draws the overlay with `Jibbers.Sdk.UI.Overlay` |
| [Integration/SpectateCamera.cs](Integration/SpectateCamera.cs) | Points the game's follow camera at another rider and back |
| [Integration/GamePlayers.cs](Integration/GamePlayers.cs) | The other riders in your session, from the game's player manager |
| [Integration/LineStart.cs](Integration/LineStart.cs) | Records and loads where a line begins (physics state) |

Other players' riders are puppets on your PC (the game draws them from network snapshots), listed
by the game's player manager. Spectating finds the attempter's puppet there (by Steam ID, the
game's name for them, or as the one nearest to where they report they are) and points your follow
camera's `target` at it. If it can't, the log says which riders it saw. This reads other riders in
your session, an exception to the SDK's usual scope; it only moves your own camera.

A line is your rider's MuJoCo integration state (every body's position, orientation and velocity),
recorded every 0.1 s while you set, as the Rewind mod does. The snapshot from the run-in before your
landed set goes to the others (compressed, at most 6000 values), who load it at the start of their
turn. Only your own rider is simulated on your PC, so loading a line moves nobody else.

`SLVSH_PREVIEW=<dir> dotnet run` in `Slvsh.Tests` saves every scoreboard state to `<dir>/slvsh.html`.
The SDK side is described in the [multiplayer guide](../../../docs/guides/multiplayer.md).
