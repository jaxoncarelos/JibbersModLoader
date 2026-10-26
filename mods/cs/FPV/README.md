# FPV

A standalone mod: `FPV.cs` is the entry point (`FpvMod`) and this folder holds the complete
implementation.

Read `FPV.cs`, `DroneRuntime`, then `DroneFlightSession` for the gameplay flow.
`Drone` is the game-independent flight model; `PilotInput` converts input into named pilot actions
and controls. Camera, targets, HUD and audio live in separate `Integration` adapters.

See the [source walkthrough](../README.md).
Build with `dotnet build mods/cs/FPV -c Release -p:JibbersNoInstall=true`; test with
`dotnet run --project mods/cs/FPV.Tests -c Release -p:JibbersNoInstall=true`.
