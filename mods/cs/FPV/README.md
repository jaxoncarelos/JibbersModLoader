# FPV

A standalone mod: `FPV.cs` is the entry point (`FpvMod`) and this folder holds the complete
implementation.

Read `FPV.cs`, `DroneRuntime`, then `DroneFlightSession` for the gameplay flow.
`Drone` is the game-independent flight model; `PilotInput` converts input into named pilot actions
and controls. Camera, targets, HUD and audio live in separate `Integration` adapters.

Audio uses Unity `AudioSource`s from `jibbers_fpv.bundle`. Game build 26596007 moved to FMOD and
ships with Unity audio disabled (`m_DisableAudio`), and IL2CPP stripped `AudioSource.Play`:
`DroneAudio` falls back to the engine's internal call, and logs once whether the motor actually
produces sound. On that build it does not, so the drone is silent until its sounds are played
through FMOD.

See the [source walkthrough](../README.md).
Build with `dotnet build mods/cs/FPV -c Release -p:JibbersNoInstall=true`; test with
`dotnet run --project mods/cs/FPV.Tests -c Release -p:JibbersNoInstall=true`.
