# Rewind C# source

`Reshred.cs` registers the SDK's reusable rewind feature. `reshred` remains its saved mod ID.
The complete C# implementation is in
[`sdk/dotnet/Jibbers.Sdk/Features/Reshred`](../../../sdk/dotnet/Jibbers.Sdk/Features/Reshred).

Read `RewindController`, `RewindRuntime`, `RewindPlayback`, then `RewindHistory`. Those files cover
options, the hold interaction and recording/restoring history without native memory operations.
`Integration` adapts MuJoCo state, rider/replay snapshots, input reservation and the indicator.

See the [source walkthrough](../../../sdk/dotnet/Jibbers.Sdk/Features/README.md).
Build with `dotnet build mods/cs/Reshred -c Release -p:JibbersNoInstall=true`.
