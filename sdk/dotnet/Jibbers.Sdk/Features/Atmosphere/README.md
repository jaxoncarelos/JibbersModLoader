# Atmosphere implementation

`AtmosphereRuntime.ApplyFrame` coordinates the frame without packing shader channels or reading JSON.

- `AtmosphereConfigurationStore` owns persistence, presets, menu translation and snapshot caching.
- `AtmosphereConfiguration` supplies document defaults and transformations. Saved keys are preserved.
- `AtmosphereRenderSettings` takes an isolated, typed snapshot after configuration changes. Frames reuse it.
- `AtmosphereShaderParameters` translates fog, snow, clouds, lighting and motion into shader vectors.
- `Integration/AtmosphereColorGrading` restores and applies the game's lighting overrides.
- `Integration/CharacterSparkleMask` tracks character bounds so glitter stays on terrain.
- `Integration/AtmosphereShader` owns the native shader calls and property ID cache.

Shader calculation tests cover the original 21-vector frame, camera fallbacks, clamps, frost blending,
legacy array shapes, disable behavior and settings snapshot invalidation. The native integration still
requires in-game acceptance after rendering changes; these tests do not replace gameplay checks.
