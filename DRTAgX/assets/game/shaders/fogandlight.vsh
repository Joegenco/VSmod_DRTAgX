#ifndef DYNLIGHTS
    #define DYNLIGHTS 0
#endif
#ifndef MINBRIGHT
    #define MINBRIGHT 0.0 // Ensure float
#endif
#if SHADOWQUALITY > 0
out float blockBrightness;
#endif

out float glowLevel;
out vec3 blockLight;
out float sm_voxSunLight;

uniform float flatFogDensity;
uniform float flatFogStart;
uniform float viewDistance;
uniform float viewDistanceLod0;
uniform float glitchStrengthFL;
uniform float nightVisionStrength;

#if DYNLIGHTS > 0
uniform vec3 pointLights[DYNLIGHTS];
uniform vec3 pointLightColors[DYNLIGHTS];
uniform int pointLightQuantity;
#endif

#include fogspheres.ash
#include drtagx_fog_transport.ash

#include drtagx_light_balance.ash
out vec3 drtSunLight;
out vec3 drtVoxelLight;
out vec3 drtEmissionLight;
out vec3 drtLocalLight;
out vec3 drtSkyLight;
// Sky/celestial/LOD paths do not call surface lighting and retain full exposure.
// Surface lighting sets this before fog metadata, independently of local lamps.
float drtVertexFogSunlight = 1.0;

#ifdef DRT_FORWARD_PLACED_LIGHTS
#include drtagx_forward_placedlights.ash
#endif

#if DYNLIGHTS > 0
#define DRT_DYNAMIC_CAPACITY DYNLIGHTS
#include drtagx_dynamic_accumulation.ash
#endif

vec4 getPointLightRgbv(vec3 worldPos, vec3 normalView) {
#if DYNLIGHTS == 0
    return vec4(0.0);
#else
    vec3 contributions[DYNLIGHTS];
    int count = clamp(pointLightQuantity, 0, DYNLIGHTS);
    for (int i = 0; i < count; ++i)
        contributions[i] = drtDynamicContribution(pointLights[i] - worldPos,
            pointLightColors[i], normalView, 1.0);
    vec3 rgb = drtAccumulateDynamic(contributions, count) /
        max(1.0, glitchStrengthFL * 2.0);
    return vec4(rgb, dot(rgb, DRT_LUMINANCE));
#endif
}

vec4 applyLightWithoutPointLight(vec4 sunColor, vec4 blockColor, float bGlow) {
    drtSunLight = sunColor.rgb;
    drtVoxelLight = DRT_BASE_LOCAL_GAIN * blockColor.rgb;
    drtEmissionLight = vec3(DRT_EMISSION_GAIN * bGlow);
    drtLocalLight = drtVoxelLight + drtEmissionLight;
    blockLight = drtSunLight + drtLocalLight;
#if SHADOWQUALITY > 0
    blockBrightness = clamp(dot(drtVoxelLight, DRT_LUMINANCE), 0.0, 0.99);
#endif
    return vec4(blockLight, 1.0);
}

vec4 applyLightWithNormal(vec3 ambientColor, vec4 lightColor, int renderFlags, vec4 worldPos, vec3 normalView) {
    float bGlow = float(renderFlags & GlowLevelBitMask) * 0.00390625;
    glowLevel = bGlow / 2.5;
    sm_voxSunLight = drtSunAccess(lightColor.a);
    drtVertexFogSunlight = sm_voxSunLight;
    drtSkyLight = drtAmbientSky(ambientColor);
    vec3 voxel = lightColor.rgb / max(1.0, glitchStrengthFL * 2.0);
    applyLightWithoutPointLight(vec4(drtSunRadiance(sm_voxSunLight, drtSkyLight), 1.0),
        vec4(voxel, 1.0), bGlow);
#ifdef DRT_FORWARD_PLACED_LIGHTS
#ifdef DRT_FORWARD_PLACED_GUARD
    if (DRT_FORWARD_PLACED_GUARD)
#endif
    drtLocalLight = drtForwardPlacedLocal(worldPos.xyz, normalView, drtVoxelLight,
        sm_voxSunLight, bGlow) + drtEmissionLight;
#endif
    // Marked deferred fills select voxel + emission explicitly, while excluded
    // foliage/reflective pixels keep this complete forward fallback illumination.
    drtLocalLight += getPointLightRgbv(worldPos.xyz, normalView).rgb;
    if (nightVisionStrength > 0.0) {
        drtLocalLight += vec3(0.1, 0.5, 0.1) * 0.45 * nightVisionStrength;
        drtSunLight = mix(drtSunLight, vec3(0.1, 0.5, 0.1), nightVisionStrength);
    }
    blockLight = drtSunLight + drtLocalLight;
    return vec4(blockLight, 1.0);
}

// Other native callers keep their existing ABI. Their dynamic light is still
// occluded; a zero normal leaves directionality to the three patched world shaders.
vec4 applyLight(vec3 ambientColor, vec4 lightColor, int renderFlags, vec4 worldPos) {
#ifdef DRT_DYNAMIC_NORMAL_VIEW
    return applyLightWithNormal(ambientColor, lightColor, renderFlags, worldPos, DRT_DYNAMIC_NORMAL_VIEW);
#else
    return applyLightWithNormal(ambientColor, lightColor, renderFlags, worldPos, vec3(0.0));
#endif
}

float getFogLevel(vec4 worldPos, float fogMin, float fogDensity) {
    if (drtFogColor.w > 0.5) return drtFogOpacity(worldPos.xyz, true, drtVertexFogSunlight);
    // Before atmosphere publication, retain bounded native exponential fog.
    float depth = length(worldPos.xyz);
    float flatDepth = max(0.0, (worldPos.y - flatFogStart) * flatFogDensity);
    return clamp(1.0 - exp(-max(0.0, depth * fogDensity + flatDepth)) + fogMin, DRT_MIN_FOG_OPACITY, 1.0);
}

vec4 applyFog(vec4 worldPos, vec4 rgbaPixel, vec4 rgbaFog, float fogMin, float fogDensity) {
    float amount = getFogLevel(worldPos, fogMin, fogDensity);
    // Vertex-only callers keep alpha/material coverage independent of fog.
    vec4 outcolor = vec4(mix(rgbaPixel.rgb, rgbaFog.rgb, amount), rgbaPixel.a);
    return applySpheresFog(outcolor, 0.0, worldPos.xyz);
}
