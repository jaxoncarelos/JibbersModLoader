# C# gameplay mods

These entry points host the complete open-source implementations in the shared C# SDK:

| Mod | Entry point | Full implementation |
| --- | --- | --- |
| FPV Drone | [FPV](FPV/FPV.cs) | [Flight, input, camera, HUD and audio](../../sdk/dotnet/Jibbers.Sdk/Features/FPV) |
| Atmosphere | [Atmosphere](Atmosphere/Atmosphere.cs) | [Environment effects, presets and configuration](../../sdk/dotnet/Jibbers.Sdk/Features/Atmosphere) |
| Rewind | [Rewind](Reshred/Reshred.cs) | [Physics snapshots, replay and input restoration](../../sdk/dotnet/Jibbers.Sdk/Features/Reshred) |

The small entry-point files delegate to SDK controllers. Their implementation is C# and is included
in this repository. Native loader and runtime bridge code remains in the Rust crates.

Start a new mod from the [C# template](../../sdk/dotnet/template/MyMod), or read the
[authoring guide](../../docs/guides/csharp-authoring.md). Only these three gameplay mods are public.
