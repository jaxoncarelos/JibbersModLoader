# Atmosphere implementation

`AtmosphereEffects.Frame` coordinates the frame without packing shader channels or reading JSON.

- `AtmosphereConfiguration` owns the saved document, defaults and presets. Existing file keys are preserved.
- `AtmosphereRenderSettings` takes an isolated, typed snapshot after configuration changes. Frames reuse it.
- `AtmosphereShaderParameters` translates fog, snow, clouds, lighting and motion into shader vectors.
- `AtmosphereColorGrading` restores and applies the game's lighting overrides.
- `CharacterSparkleMask` tracks character bounds so glitter stays on terrain.
- `AtmosphereShader` owns the native shader calls and property ID cache.

Shader calculation tests cover the original 21-vector frame, camera fallbacks, clamps, frost blending,
legacy array shapes, disable behavior and settings snapshot invalidation. The native integration still
requires in-game acceptance after rendering changes; these tests do not replace gameplay checks.
