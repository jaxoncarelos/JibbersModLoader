# Rewind

A standalone mod: `Reshred.cs` is the entry point (`ReshredMod`) and this folder holds the
complete implementation. `reshred` remains its saved mod ID.

Read `Reshred.cs`, `RewindRuntime`, `RewindPlayback`, then `RewindHistory`. Those files cover
options, the hold interaction and recording/restoring history without native memory operations.
`Integration` adapts MuJoCo state, rider/replay snapshots, input reservation and the indicator.

See the [source walkthrough](../README.md).
Build with `dotnet build mods/cs/Reshred -c Release -p:JibbersNoInstall=true`; test with
`dotnet run --project mods/cs/Reshred.Tests -c Release -p:JibbersNoInstall=true`.

## Recording and input cost

History keeps six seconds at the game's full physics rate. Once full, it recycles each
snapshot, scalar buffer and list node. Current rider/replay targets and the scene adapter
are reused while their identities remain unchanged; a rebuilt physics scene still starts
fresh history. Disabling the mod skips native scene discovery entirely.

The complete Rewired binding inventory is refreshed when configuration, controller count,
scene, pause or UI/focus context changes. A small context probe runs every 750 ms; stable
riding does not enumerate all players and controller maps again. Unsupported connection
metadata falls back to a 10-second refresh, and failed inventories back off for five seconds.
Typing is queried only while a rewind input is held, and working Rewired controller coverage
avoids probing unused XInput slots.

Regression tests cover stable binding refresh, controller/context changes, retry backoff,
warm history allocations, independent reused scalar buffers, and existing playback behavior.
These are native-free checks, not an in-game frame-time benchmark.
