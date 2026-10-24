# Alpine Vision night lighting investigation

## Status

Alpine Vision 1.2.4 follows the native day/night clock and adds Moonlit and Night
Skiing sky palettes. Daytime styles no longer overwrite the native night sun and
ambient lighting. An opt-in terrain shadow geometry prototype is included;
it is disabled by default pending a live Ruka comparison. No static lighting or
shadow-map bake has been implemented, and the night performance issue is not yet
claimed fixed.

## Ruka 25/26 inventory

- Map ID: `4cf57cd8-bfd7-4efa-9ad1-b5da8af88f9c`.
- 52 floodlight posts, each containing two realtime hard-shadow spotlights with
  75 m range and a 165-degree outer cone.
- One snowcannon prefab contains four more shadow-casting spotlights.
- Potential total: 108 shadow-casting spotlights. This does not mean that all 108
  draw shadows simultaneously.
- Terrain: 1,000 m square, 4,097-square heightmap, 33,570,818 decompressed bytes.
- The native `Custom/PisteSnow` shader has additional-light, additional-shadow
  and clustered-light variants, but its keyword table has no standard
  `LIGHTMAP_ON` variant. Changing Light baking flags alone cannot integrate a bake.
- The live player uses URP RenderGraph and reports reducing punctual-shadow
  resolution to fit 35 shadow maps into an 8,192-square shadow atlas.

`tools/analyze_night_lighting.py` reads original assets without modifying them.
Its local report includes hashes, light properties, transform hierarchies and
map placements. Generated map data stays in `out/`.

## Live measurements

PresentMon 2.6.0 recorded three 30-second captures on October 24, 2026. The user
selected time and shadows manually at the same requested spot. These are
observational captures, not a replay of an identical camera route.

| Condition | Median GPU busy | Median present interval | P95 display interval |
| --- | ---: | ---: | ---: |
| Day, time 0.2, shadows on | 3.418 ms | 3.409 ms | 4.442 ms |
| Night, time 0.9, shadows on | 19.401 ms | 19.400 ms | 30.516 ms |
| Night, time 0.9, shadows off | 2.454 ms | 2.539 ms | 3.729 ms |

Disabling shadows removed about 87% of the observed night GPU cost while the
lights remained on. This identifies shadow generation as the primary measured
bottleneck; it does not establish the cost of any one caster or lamp.

## Opt-in geometry experiment

The preview constructs shadow-only mesh tiles from native terrain heights,
then switches terrain shadow casting off only after every tile is ready. The
visible terrain, snow materials, tracks, native lamps and moving rider shadows
remain on their native paths. Disabling the preview, changing maps, daylight or
an error restores original terrain shadow casting. Preparation advances one
tile per frame. Terrain changes invalidate the geometry.

This caches geometry, not illumination or shadow depth textures. The same
native lights still render shadow maps. Performance and terrain silhouette
accuracy must be measured in Ruka before enabling this by default. Rotated or
scaled terrains are rejected. Terrain holes are not represented by the preview.

Regression tests cover night interpolation, palette transitions and mesh data.
Unity render tests cover dark skies, native-versus-proxy shadow images, moving
caster shadows and restoration. These tests do not replace an in-game A/B capture.

## Remaining acceptance work

1. Verify logged native clock values and night appearance at 0.9 after install.
2. Restore normal game shadows and capture the native path and preview at the
   same Ruka position, view and quality; compare frame-time percentiles.
3. Check terrain silhouettes, moving rider shadows and restoration in game.
4. If geometry substitution is insufficient, investigate per-light static shadow
   caching with separate dynamic casters. Preserve native lamp illumination and
   snow appearance; never disable a lamp without its replacement contribution.

A future lighting cache must include map contents, game/prefab assets, lighting
settings and format version in its identity, invalidate edited/moving lights,
and avoid blocking the riding frame loop. A frozen whole shadow atlas or
unoccluded screen-space glow is not an acceptable replacement.