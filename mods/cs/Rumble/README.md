# Rumble

Controller vibration that follows the game's own sounds, so you feel each moment on the same frame
you hear it:

| Moment | What you feel |
| --- | --- |
| Takeoff | A small pop (the game's `SkiJumpSnow`) |
| Landing | A short thump (`SkiLandSnow`), deeper and softer in powder (`SkiLandPowder`), scaled by how hard you hit the slope: a hop is a tick, a flat landing from a big drop is everything |
| Rails and boxes | A clank onto the rail (`SkiRailHit`), then a buzz for exactly as long as the rail sound plays, stronger when it's louder |
| Crash | A hit for every body impact (`BodyHitHard`, `BodyHitSoft`), heavy for a knockout, a click when a ski comes off |
| Really hard carving | The side you're turning to: left for a left carve, right for a right one. Normal turns are still |
| Snow (off by default) | A faint texture that grows with speed |
| In the air | Nothing, so landings hit harder |

Every kind can be switched off in **Options > Mods > Rumble**, and **Strength** scales them all.
Vibration stops while paused, in replays, and when the game isn't focused. Levels are lifted past
the point where the motors start spinning, so light effects are felt rather than lost.

Every controller is driven: XInput pads directly, and anything the game's input system (Rewired)
knows, which covers PlayStation and other pads without Steam Input.

`Rumble.cs` is the entry point (`RumbleMod`). `RumbleDirector` turns the game's sounds and what the
rider is doing into two motor levels, without the game. `Integration/GameAudio` follows the game's
AudioManager: a detour of `AudioManager.Play`, which every one-shot sound goes through, and its array
of loop volumes for the rail slide. `RiderProbe` measures the rider (how high the lowest part of the
body is, the slope under the feet, how upright the spine is) and `ControllerMotors` drives the pads.
If the sounds can't be followed, landings, crashes and grinds fall back to being measured (and to
slides the game scores, `TrickPerformed`). The log names each game sound the first time it's heard,
the rail sound starting, and hard landings.

Build with `dotnet build mods/cs/Rumble -c Release -p:JibbersNoInstall=true`; test with
`dotnet run --project mods/cs/Rumble.Tests -c Release -p:JibbersNoInstall=true`.
