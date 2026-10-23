# Alpine Vision 1.1.2

One mod containing Atmosphere weather, Alpine Vision visual filters and optional hardware ray-traced shadows/AO.

The game's **default snow textures, tiling, normals, tracks and material properties are retained**. The replacement texture holder is removed from the shared bundle and the weather surface adapter makes no terrain calls. Valley cloud banks are disabled in every preset and their controls are removed. Falling snow, fog, storms, sky clouds, glitter and sun shafts remain.

## Controls

- Visual style: Natural, Alpine Film, Golden Hour or Bluebird. Existing style selections persist. Golden Hour's editor reference matches the previous filter exactly.
- Atmosphere: Off, Clear, Foggy or Snowstorm, plus imported custom weather presets. This is independent of visual style. F10 cycles weather when Hotkeys is enabled.
- Original Atmosphere controls: fog thickness/start, snowfall, flake size, speed streaks, sun shafts, colour grading, hotkeys/cycle key and optional terrain detail boost. Valley-cloud controls are intentionally excluded.
- Existing quality, hardware RT, atmosphere strength, bloom, sharpening and exposure controls remain.

The original jibbers_atmosphere.json is imported once into alpinevision_weather.json. Subsequent weather edits persist in the latter; the old configuration is not modified. Weather presets reset their sliders as in the original Atmosphere mod and synchronize the displayed values. Switching visual style preserves weather. Weather Off leaves the rest of Alpine Vision available.

Natural keeps the map's lighting and existing Atmosphere grade. Film styles keep the established Alpine Vision filter. The film tone mapper and preceding tone mapper are coordinated; they are not applied together. Colour grading can be disabled separately.

## Installation and validation

Use tools/install_alpinevision.ps1 with the package. It waits for Jibbers to close, backs up replaced files, installs one DLL and one bundle, and keeps the old managed Atmosphere package deselected so it is not reinstalled. Other mods, SDK binaries and the loader are preserved. This update continues the unified mod; it does not re-enable the old mod DLLs.

DX11 supports weather and the visual stack. Add -force-d3d12 for hardware ray-traced sun shadows/AO. alpinevision.status reports weather preset, prefab and renderer state. Submission status is not GPU timing.

Tests cover retained weather controls/persistence, independent weather/style combinations, Weather Off, nonblocking preset callbacks, native snow ownership and optional API failures. Unity tests check capture accuracy, reference images, bundle contents, detachment and DXR output. Editor tests do not substitute for checking live gameplay appearance.

Research and decisions are summarized in AlpineVision-GRAPHICS-RESEARCH.md. This update does not implement path tracing, SSGI, ray-traced reflections, DLSS or rebuilt world models.

## Source

C#: mods/cs/AlpineVision. Unity: mods/JibbersAlpineVision/unity. Weather is a local fork of the repository's Atmosphere engine. Neither old mod DLL is required. Source uses the repository GPL-3.0 license. Legacy CC0 texture credits remain in the source tree; replacement snow maps are not used by this build.

## 1.1.1 preset rendering fix

The live 1.1.0 log showed Material.SetVector throwing after a map/camera transition. The renderer then stopped permanently, while the Mods menu continued saving preset changes. Visual materials are now owned runtime copies with HideAndDontSave; render targets also survive unused-asset cleanup. Map changes detach and rebind the renderer. Missing native materials are recreated, and native submission exceptions get at most three delayed recovery attempts. Applied style logs distinguish a submitted frame from a requested setting.

Regression tests explicitly detach and clear the command buffer, run Unity unused-asset cleanup, verify all six materials remain usable, replace the camera, switch all four profiles, and verify visible pixel changes and restoration. These are editor rendering checks; live gameplay still requires relaunching with this DLL.

## Catalog upgrade and conflicts

Alpine Vision replaces the existing Atmosphere catalog entry under its stable package ID `jibbers_atmosphere`. Existing subscribers upgrade at the next launch. There is one catalog entry, so users cannot select both versions. The signed installer replaces the old managed/native Atmosphere payload before mods load and removes the previous package-owned bundle. Weather configuration is imported and retained.

The SDK also checks mod conflicts in both directions before OnLoad. Alpine Vision conflicts with `atmosphere` and `raytracing`, so stale manual DLLs do not install a second weather/rendering stack. Replacement mods reserve conflicts before legacy hooks start; catalog copies take priority over manual copies of the same mod. Manual installation backs up old Atmosphere and Ray Tracing copies.

F7 toggles hardware ray tracing for comparison; F10 cycles weather. Ray-tracing shaders/materials and render targets now survive Unity unused-asset cleanup. DX12 and compatible hardware are required; check alpinevision.status/logs for failure or unsupported state.
