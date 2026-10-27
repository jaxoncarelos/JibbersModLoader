# Slow Motion

Click the left stick (L3) to slow the game down, and click again to return to normal speed. The
keyboard key (V by default) does the same. In **Options > Mods > Slow Motion** you can switch to
hold-to-slow, set the speed (0.25x by default) and the ease time, and turn off the on-screen label.

`SlowMo.cs` is the entry point (`SlowMoMod`). Each rendered frame it reads the controller and the
key and advances `SlowMotion`, which owns the toggle/hold state and eases the speed over real
time. The physics slows by skipping whole MuJoCo `FixedUpdate` calls in proportion to the speed
(`StepLimiter`), the way the game's own pause stops them. Two seconds into each slow motion the log
says how many physics steps actually ran. Presses in the pause menu are ignored, and disabling the mod returns the
game to normal speed at once.

Every controller is read: all XInput slots (Xbox and compatible pads, and PlayStation pads through
Steam Input), since virtual controllers can take the first slot, and every controller the game's
input system (Rewired) knows, which covers PlayStation and other pads without Steam Input
(`RewiredSticks`, which finds the stick-click buttons by name and logs what it found). The log records controllers connecting and slow motion turning on and off. A click counts when you release it, and not when both sticks are clicked together
(the FPV drone's toggle). The game still receives the stick click for whatever it binds there.

Build with `dotnet build mods/cs/SlowMo -c Release -p:JibbersNoInstall=true`; test with
`dotnet run --project mods/cs/SlowMo.Tests -c Release -p:JibbersNoInstall=true`.
