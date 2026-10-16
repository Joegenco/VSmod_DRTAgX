# Changelog

## 2.1.3

- Improved lighting consistency between open and closed chests, doors and other animated blocks.
- Supported containers no longer trigger PLS updates just from opening or closing. Closed containers keep casting shadows.
- Improved PLS cache retention when turning around and returning to nearby areas.
- Moving light shadows are softer, with reduced CPU overhead when several lights are active.
- Moving light shadows stop rendering for sources over 42 blocks from the camera. Shadow depth is limited to 32 blocks from each light.
- Reduced unnecessary PLS rendering work when no placed lights contribute to the scene.
- Updated GhostChunks compatibility for its newer lighting and cloud shaders.
- Weather fog now follows vanilla visibility more closely. Lighter haze reduced, and distant shaded terrain no longer cuts dark holes through the fog.
- Improved fog protection for large enclosed halls.
- Sheyder overexposure now also affects animated entities.
- Leaves facing away from the sun are darker during the day, combined with existing sun shadows.
- Disabled the moon's scattering halo during daytime.
- Updated my default Sheyder settings.
- Additional rendering fixes and performance optimisations.

## 2.1.1

- Reworked dense weather fog. The heaviest mist now leaves roughly 10 blocks of useful visibility and no longer gets the sky's full brightness boost.
- Indoor fog now uses room enclosure, with a fallback for larger halls. Rooms stay clear while fog outside remains visible through doors and windows. Cave protection remains.
- Volumetric fog now respects indoor shelter and fades with dense weather instead of adding another bright layer over it.
- Covered VS Roofing tiles now block Sheyder's blocklight and Directional Sun as full blocks. Bare roof frames stay open.
- Improved PLS updates when turning around, entering rooms and returning to previously lit areas. Reduced the visible lighting fill-in effect.
- Grass and wind-affected foliage now receive cached PLS. Lighting follows the vegetation without being recalculated for every wind movement. Foliage still does not cast PLS shadows.
- Rebalanced animated entities, open doors and chests. Animated entities retain their blocklight brightness, with PLS providing the direction of the light.
- Fixed moving light leaking through the back side of walls.
- VS Roofing tiles and other solid two-sided terrain now cast moving-light shadows.
- Improved Directional Sun lighting around chiseled roofs.
- Reduced moving-light shadow artefacts when a light is close to, or clips into, a surface.
- Improved held-light positioning for other players, using their animated hand positions where available.
- Fixed weak moving light sources, including the temporal gear amulet, being made too bright.
- Particles, including rain and splashes, now follow Sheyder's baked blocklight. Removed extra unshadowed PLS lighting on particles, while keeping their intended glow.
- Fixed the transparency compositor leaving terrain normal and position data undefined behind glass and other transparent effects. Normal glass tint and transparency are preserved.
- Added AstraTerra and AstraExtera compatibility for stars, moons and other celestial objects.

## 2.1.0

- Placed light shadows (PLS) completely overhauled, with cached surface lighting and much less repeated work.
- Visible areas now take priority. Hidden underground lights no longer hog the cache updates for the scene you're looking at.
- Faster block and light placement/removal updates. Improved cache reuse when moving around and returning to an area.
- Reduced movement-related overhead and periodic PLS frame-time spikes.
- Many fixes for patchy shadows, flickering, decal flicker and incomplete lighting after cache updates.
- Rebalanced PLS and voxel lighting across terrain, doors, chests, books and animated entities. Fixed lighting being drawn over some objects.
- Glass no longer casts PLS shadows. Wind-animated foliage is excluded from PLS shadow casting, while foliage receives directional placed lighting.
- Moving light shadows optimised. First-person maps follow the view aspect ratio, light ranges are limited to 16-32 blocks, and the default shadowed light count is 2.
- Animated entities now cast moving-light shadows. The player casts a shadow in third person; held-light positions and entity lighting were adjusted.
- Rain, rain splashes and block particles rebalanced for daytime and night. Rain and splashes no longer produce the unwanted bloom glow.
- Fixed materials such as glacier ice and opaque locust parts looking lit in darkness, while preserving their actual glowing parts.
- Volumetric fog improved, with reduced light leaking through terrain and better depth-aware blending.
- Night vision now works in deferred mode, with a green infrared-style look and a visor light fading over 64 blocks.
- Added GhostChunks and RainCurtains compatibility. Distant terrain lighting and fog integration improved, alongside existing ChunkLod, Distant Vistas and SMAA support.
- My current Sheyder settings are included as defaults. Version checks automatically reset outdated DRTAgX and Sheyder settings; no manual config replacement is needed.
- Settings tidied up. Performance mode has its own tab; redundant PLS controls and Sheyder Bloom menus removed. Deferred is kept enabled in normal play, and vanilla God rays stay enabled.
- Code cleanup and release polish. The public source project is simpler and no longer includes tests or probes.
