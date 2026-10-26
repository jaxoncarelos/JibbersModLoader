# Rewind

A standalone mod: `Reshred.cs` is the entry point (`ReshredMod`) and this folder holds the
complete implementation. `reshred` remains its saved mod ID.

Read `Reshred.cs`, `RewindRuntime`, `RewindPlayback`, then `RewindHistory`. Those files cover
options, the hold interaction and recording/restoring history without native memory operations.
`Integration` adapts MuJoCo state, rider/replay snapshots, input reservation and the indicator.

See the [source walkthrough](../README.md).
Build with `dotnet build mods/cs/Reshred -c Release -p:JibbersNoInstall=true`; test with
`dotnet run --project mods/cs/Reshred.Tests -c Release -p:JibbersNoInstall=true`.
