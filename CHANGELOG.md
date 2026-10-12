# Changelog

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
