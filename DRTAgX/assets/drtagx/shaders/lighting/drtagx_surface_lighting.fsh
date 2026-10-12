#include drtagx_fog_transport.ash
#include drtagx_light_balance.ash

// Deferred has no native lighting vertex stage; its surface binding publishes
// gameplay strength. Forward models carry the native per-draw value, including
// first-person copies that are absent from the engine shader enumeration.
#ifdef DRT_DEFERRED_LIGHTING
uniform float drtNightVisionStrength = 0.0;
float drtMinimumSurfaceLight(vec3 receiverView) {
    return drtNightVisionIllumination(drtNightVisionStrength, receiverView);
}
#else
flat in float drtVertexNightVisionStrength;
in vec3 drtNightVisionViewPosition;
float drtMinimumSurfaceLight() {
    return drtNightVisionIllumination(drtVertexNightVisionStrength, drtNightVisionViewPosition);
}
#endif

#ifdef DRT_DEFERRED_LIGHTING
// Deferred initializes these explicit components from its G-buffer and native sky.
vec3 drtSunLight = vec3(0.0);
vec3 drtLocalLight = vec3(0.0);
vec3 drtSkyLight = vec3(0.0);
float drtSurfaceSunAccess = 0.0;
#else
in vec3 drtSunLight;
in vec3 drtLocalLight;
in vec3 drtVoxelLight;
in vec3 drtEmissionLight;
in vec3 drtSkyLight;
#define drtSurfaceSunAccess sm_voxSunLight
#endif

#ifdef DRT_TERRAIN_PLACED_OCCLUSION
float drtTerrainPlacedVisibility = 1.0;
// Wind foliage can prepare calibrated PLS at its rest-pose fragment before
// the G-buffer discards that position. Moving lights and emission stay separate.
vec3 drtTerrainPlacedRadiance = vec3(0.0);
vec3 drtTerrainPlacedSelfLight = vec3(0.0);
#endif

vec3 drtVisibleLocalLight() {
#ifdef DRT_TERRAIN_PLACED_OCCLUSION
    if (drtTerrainPlacedVisibility >= 1.0 && all(equal(drtTerrainPlacedRadiance, vec3(0.0))) &&
        all(equal(drtTerrainPlacedSelfLight, vec3(0.0)))) return drtLocalLight;
    // Select cached source/self light independently, then add the retained
    // voxel share. Self-light must not erase that baseline on emitter blocks.
    vec3 placedLocal = drtVoxelLight * drtTerrainPlacedVisibility +
        max(drtTerrainPlacedRadiance, drtTerrainPlacedSelfLight);
    return max(drtLocalLight - drtVoxelLight, vec3(0.0)) + placedLocal;
#else
    return drtLocalLight;
#endif
}

// Sky access alone stays high outdoors at night; sun effects also need sky radiance.
vec3 drtSunEffectColor() {
    return sqrt(clamp(drtSurfaceSunAccess, 0.0, 1.0)) * max(drtSkyLight, vec3(0.0));
}

vec3 drtComposeRadiance(vec3 sun, vec3 local, float visibility, vec3 boost) {
    return local + sun * max(visibility, 0.0) * boost;
}

vec3 drtAppliedSunScale = vec3(1.0);

vec3 drtSunOnlyMultiplier(vec3 litColor, vec3 multiplier) {
    // Reflective materials run after diffuse shading. Reuse its actual sun
    // scale so their directional glitter cannot amplify local light/emission.
    vec3 current = drtVisibleLocalLight() + drtSunLight * drtAppliedSunScale;
    vec3 changed = drtVisibleLocalLight() + drtSunLight * drtAppliedSunScale * multiplier;
    return litColor * vec3(current.r > 0.0 ? changed.r / current.r : 1.0,
        current.g > 0.0 ? changed.g / current.g : 1.0,
        current.b > 0.0 ? changed.b / current.b : 1.0);
}

vec3 drtShadeSurface(vec3 litColor, float sunVisibility, vec3 sunBoost) {
    drtAppliedSunScale = max(sunVisibility, 0.0) * sunBoost;
#ifdef DRT_SHARED_DECAL_IRRADIANCE
    vec3 sharedIrradiance;
    if(drtReadDecalIrradiance(sharedIrradiance))
        return litColor*(max(sharedIrradiance,vec3(drtMinimumSurfaceLight()))+drtEmissionLight);
#endif
#ifdef DRT_DEFERRED_LIGHTING
    return drtComposeRadiance(drtSunLight, drtLocalLight, sunVisibility, sunBoost);
#elif defined(DRT_ALBEDO_SURFACE_LIGHTING)
    // Transparent terrain and particles pass unlit material. Compose explicit
    // light components directly, including a truly dark result at zero light.
    // PLS can replace the voxel share after vertex lighting. Enforce the
    // minimum again on this final, untinted illumination before albedo.
    vec3 local = drtVisibleLocalLight();
    local = drtEmissionLight + max(local - drtEmissionLight, vec3(drtMinimumSurfaceLight()));
    return litColor * drtComposeRadiance(drtSunLight, local, sunVisibility, sunBoost);
#else
    // Native callers already multiply albedo/tint by vertex light. This ratio
    // uses explicit components, leaving local light and emission independent.
    vec3 total = drtSunLight + drtLocalLight;
    vec3 shaded = drtComposeRadiance(drtSunLight, drtVisibleLocalLight(), sunVisibility, sunBoost);
    return litColor * vec3(total.r > 0.0 ? shaded.r / total.r : 1.0,
        total.g > 0.0 ? shaded.g / total.g : 1.0,
        total.b > 0.0 ? shaded.b / total.b : 1.0);
#endif
}
