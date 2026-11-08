# Render Distance

Uncaps the game's own **Terrain Render Distance** and **Object Render Distance** options. The game
offers 100 to 1500 m; with this mod the same options in the normal Options menu continue with 2000,
2500, 3000, 4000, 5000, 7500, 10000, 15000, 20000, 30000 and 50000 m. **Options > Mods > Render
Distance** sets the highest choice (3 km to 100 km). Farther distances cost more GPU.

Both options are `ArrowSelection` rows bound to the game settings `cull-terrains-distance` and
`cull-objects-distance`. At startup and on every map load the mod finds those rows (including the
hidden Options window and menu prefabs, via `Resources.FindObjectsOfTypeAll`, never per frame) and
gives them the longer list: `defaultOptions`, the labels (through the game's own `SetOptions`) and
the values array together, keeping the current distance selected. The distance you pick is saved
in `JibbersMods/render_distance.state.json` and put back if the game snaps a far saved value back to
1500 while its menu loads before mods attach. The game's terrain culler scales its detail tiers by
the terrain distance, so on large maps made of many terrain chunks the far chunks stay drawn too.
Disabling the mod restores the 1500 m list.

Build with `dotnet build mods/cs/RenderDistance -c Release -p:JibbersNoInstall=true`; test with
`dotnet run --project mods/cs/RenderDistance.Tests -c Release -p:JibbersNoInstall=true`.
