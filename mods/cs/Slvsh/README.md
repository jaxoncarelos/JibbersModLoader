# SLVSH

S.K.A.T.E. on snow, with your party.

- Players take turns **setting**. The setter **calls a line** first: one or more tricks, picked part
  by part on a radial menu. Then they have to land it.
- Everyone else gets **one try** to land the same line. Miss it (bail, a different trick, or run
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
4. **Call your set.** On your set the wheel opens by itself (**Ctrl+G**, or a right-stick click,
   opens and closes it). Pick each part with the number keys **1-9**, or point the right stick and press
   **A**. **Backspace** or **B** goes back a step. You have 90 seconds to call; the clock for landing
   it starts when you do.
5. **Watch every turn.** While someone takes their turn, your camera follows their rider, so you
   see the whole line. It comes back to you for your own turn. This only works when they're on the
   same server as you; otherwise the scoreboard says "Not on your server".
6. **Vote.** After each try, everyone else votes: **Y** it counted, **N** it didn't. The
   scoreboard shows what the game detected ("game's guess"). The majority decides; a tie, or
   nobody voting, goes with the game's guess. You can change your vote until time's up.
7. **Ctrl+Q** leaves the game. If the starter ends it, it ends for everyone.

## Calling a line

Each trick is built one part at a time, and every part has **Don't care**:

- **Jump**: stance (regular / switch), spin (left / right / no spin), rotation (flat, cork, misty,
  bio, lincoln, frontflip, backflip), how many (single, dub, trip, quad), then degrees (180 up to
  2160, on two pages).
- **Rail**: stance, spin on (left / right / straight on, then degrees), the slide (slide,
  frontslide, backslide, 50-50, 5-0), then spin off (left, right, front, back or straight off, then
  its rotation and degrees).

- **Type it** (keyboard): type the trick in your own words ("360 nose tap the snow blower") and
  press Enter. Detection can't check words, so your next real trick (or one sharing a word, like
  "nose tap" and the game's "Nosetap") fills its place, and the vote decides if it was really that.

After each trick: **Add a trick**, **Call it**, or **Undo last**. A line has up to 8 tricks, for
example "Left 270 On - Slide - Right 270 Out", then "Any way Dub Cork 1080". Grabs are never part
of a call: they differ by moveset.

## How a try is detected

- Land the called tricks **in order, in one run**. Small stuff in between (straight airs, zero
  spins, butters, small spins) doesn't count. Every other trick takes the next place in your line,
  and the try is decided once you've done as many as the line has, so a misread trick never ends
  your run early (and the vote has the last word). A trick
  counts once you ride away clean (no respawn, no more combo for about two seconds), or start the
  next one. Rails count when you come off.
- You're placed at the spawn when your turn starts (and frozen there between turns), so you never
  need to reset during a try: **any reset ends it**, so a bail is a miss (the vote still decides).
- Flips and spins off a rail count as how you came off it, even when the game reports them as a
  trick of their own right after the rail ("... - Slide - To Regular" then "Backflip" becomes
  "... - Backflip Out"). The spin-off wheel has Frontflip and Backflip.
- Not finishing before the timer runs out is a miss, with no vote.
- A set where the game saw no trick at all isn't voted on.

## Settings (Mods > SLVSH)

- Keys: start / join / begin (Ctrl+Enter), leave (Ctrl+Q), call your set (Ctrl+G), vote it counted (Y)
  and it didn't (N). The vote keys and the wheel's number keys only work while they're open. No
  F-keys, so nothing clashes with the drone or Alpine Vision.
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
| [SlvshMod.cs](SlvshMod.cs) | Entry point: party channel, keys, the game thread, the wheel, camera and overlay |
| [SlvshRules.cs](SlvshRules.cs) | The rules and votes (`SlvshHost`) and the state the host sends everyone |
| [Calls.cs](Calls.cs) | A called trick, reading the game's trick names, and matching one against the other |
| [CallMenu.cs](CallMenu.cs) | The radial menu's steps (pure, tested) |
| [CallWheel.cs](CallWheel.cs) | Draws the radial menu |
| [TrickJudge.cs](TrickJudge.cs) | Judges a run against the called line from the game's trick and respawn events |
| [Scoreboard.cs](Scoreboard.cs) | Draws the overlay with `Jibbers.Sdk.UI.Overlay` |
| [Integration/SpectateCamera.cs](Integration/SpectateCamera.cs) | Finds the rider to watch and flies your camera behind them, then gives it back |
| [Integration/ChaseCamera.cs](Integration/ChaseCamera.cs) | The chase camera's motion (pure, tested) |
| [Integration/GamePlayers.cs](Integration/GamePlayers.cs) | The other riders in your session, from the game's player manager |
| [Integration/LineStart.cs](Integration/LineStart.cs) | Records and loads where a line begins (physics state) |

Other players' riders are puppets on your PC (the game draws them from network snapshots), listed
by the game's player manager. Spectating finds the attempter's puppet there (by Steam ID, the
game's name for them, or as the one nearest to where they report they are) and points your
camera at it. The game's follow camera only ever follows your own rider (changing its `target` does
nothing), so while you watch it is switched off and SLVSH flies the camera itself every frame:
behind the rider, turning to their heading, pulled in front of the slope when it would go into the
ground. It's switched back on when the turn and its vote end. If the rider can't be found, the log
says which riders it saw; while watching, the log says which rider and body it follows and how far
they moved in the first few seconds. This reads other riders in
your session, an exception to the SDK's usual scope; it only moves your own camera.

A line is your rider's MuJoCo integration state (every body's position, orientation and velocity),
recorded every 0.1 s while you set, as the Rewind mod does. The snapshot from the run-in before your
landed set goes to the others (compressed, at most 6000 values), who load it at the start of their
turn. Only your own rider is simulated on your PC, so loading a line moves nobody else.

`SLVSH_PREVIEW=<dir> dotnet run` in `Slvsh.Tests` saves every scoreboard state to `<dir>/slvsh.html`.
The SDK side is described in the [multiplayer guide](../../../docs/guides/multiplayer.md).
