# Alpine Vision 1.2.5

Atmosphere extended with sky, lighting styles, native snow, fog, snowfall, storms, sun shafts and a cinematic finish that integrates with the game's URP renderer.

Hardware ray tracing and all mod shadow/AO overrides have been removed. The mod has no ray-tracing setting or hotkey. F9 remains Drone's toggle; F10 cycles weather. Game shadow rendering stays in charge.

## Options

- Visual style: Natural, Alpine Film, Golden Hour, Bluebird.
- Weather: Off, Clear, Foggy, Snowstorm and imported custom Atmosphere presets.
- Original Atmosphere controls, bloom, sharpening, exposure and quality remain.
- Default game snow textures, normals, tiling and tracks remain. Valley cloud banks are removed.

Visual style and weather are independent. Weather Off only disables weather. The original jibbers_atmosphere.json is imported once into alpinevision_weather.json and retained. Settings persist; removed RT settings are ignored.

The sky's sun enhancement follows the game's directional light. Visual styles change colour and intensity without rotating the sun; a missing light disables the extra disc instead of drawing one at a fixed fallback angle.

Ctrl+L toggles your rider's headlamp. The Headlamp option in the Mods menu follows the same saved preference. Turning it back on restores native day/night activation; other riders and map lamps are untouched.

## Night

Native time of day controls night lighting. Night style selects Moonlit or Night Skiing; the palette follows the game's night/twilight state. At night, visual styles stop forcing daytime sun/ambient values, the added sun fades out, and clouds use the dark horizon palette.

Efficient night shadows (preview) is opt-in. It prepares tiled shadow-only meshes from the live terrain heightfield while retaining the visible terrain, snow textures/tracks, lamps and moving-object shadows. Native terrain shadow casting switches only after preparation completes; disabling the option, a map change or an error restores it. This caches terrain geometry, not baked lighting or shadow maps. Ruka performance/appearance still needs live comparison; the option defaults off.

## Rendering

The film finish uses the player's existing FullScreenPassRendererFeature, its active renderer feature list, URP color-copy input and RenderGraph fullscreen pass. It does not attach a camera command buffer in URP. A built-in renderer fallback is kept for compatible environments. Runtime materials/features are owned, protected from unused-asset cleanup, detached on transitions/disable and destroyed on unload.

Logs report feature configuration, not GPU execution. Editor tests render through a real URP pipeline, compare profiles and test removal/restoration. Live gameplay still needs separate verification.

## Upgrade and conflicts

The catalog upgrades Atmosphere's existing slot under stable package ID jibbers_atmosphere. Subscribers automatically receive Alpine Vision on the next updating launch. There is one selectable catalog entry for the weather/visual stack.

SDK conflict checks run before OnLoad. Alpine Vision takes priority over stale manual Atmosphere or Ray Tracing copies, which are skipped before they hook the game. Manual installation backs up and removes those legacy DLLs; settings and unrelated mods remain.

Source: mods/cs/AlpineVision and mods/JibbersAlpineVision/unity. GPL-3.0-only. No replacement terrain textures or ray-tracing assets are packaged.
