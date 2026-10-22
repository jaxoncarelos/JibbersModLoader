# FPV C# source

`FPV.cs` registers the SDK's reusable drone feature. The complete C# implementation is in
[`sdk/dotnet/Jibbers.Sdk/Features/FPV`](../../../sdk/dotnet/Jibbers.Sdk/Features/FPV).

Read `DroneController`, `DroneRuntime`, then `DroneFlightSession` for the gameplay flow.
`Drone` is the game-independent flight model; `PilotInput` converts input into named pilot actions
and controls. Camera, targets, HUD and audio live in separate `Integration` adapters.

See the [source walkthrough](../../../sdk/dotnet/Jibbers.Sdk/Features/README.md).
Build with `dotnet build mods/cs/FPV -c Release -p:JibbersNoInstall=true`.
