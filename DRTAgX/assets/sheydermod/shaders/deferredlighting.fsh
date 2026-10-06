#version 330 core
#extension GL_ARB_gpu_shader5 : require
#extension GL_ARB_shader_storage_buffer_object : require
#extension GL_ARB_shading_language_420pack : require

// SheyderMod: Deferred terrain lighting - fullscreen relight pass.
// Runs once per screen pixel at the end of the opaque stage over the scratch G-buffer the
// redirected chunk shaders filled. Pixels tagged as deferred (via the glow.g marker) are
// relit with the engine's own lighting math (pulled in from fogandlight.fsh: normal shade,
// shadow-map PCF, fog and weather fog spheres, plus SheyderMod WorldFog), and the SubSurface
// and Specular effects are re-applied from G-buffer data; everything else passes through
// unchanged. Position and normal are reconstructed from depth, underwater murkiness is
// applied once per pixel, and relit vs raw is crossfaded by the deferred weight.

uniform sampler2D gColor;
uniform sampler2D gGlow;
uniform sampler2D gNormal;
uniform sampler2D gDepth;
uniform sampler2D gPositionIn;

uniform mat4 invProjectionMatrix;
uniform mat4 invModelViewMatrix;
uniform vec4 rgbaFog;
uniform int  uDebug;

uniform vec3 sunPosition;

#include drtagx_deferred_uniforms.fsh

in vec2 texcoord;

layout(location = 0) out vec4 outColor;
layout(location = 1) out vec4 outGlow;

layout(location = 3) out vec4 outGPosition;

#define DRT_DEFERRED_LIGHTING
#define DRT_SURFACE_FOG_AFTER_LIGHTING
#include fogandlight.fsh

#include underwatereffects.fsh

#include drtagx_sun_subsurface.fsh
#include drtagx_sun_specular.fsh

// DRTAgX modules use unique filenames because the native registry indexes includes by name.
#include drtagx_deferred_cube.fsh
#include drtagx_deferred_dynamiclights.fsh
#include drtagx_deferred_staticshadows.fsh
#include drtagx_deferred_placedlights.fsh
#include drtagx_deferred_relighting.fsh
#include drtagx_deferred_debug.fsh

float deferredMurkiness(float terrainDepth)
{
    if (cameraUnderwater > 0.7) return 0.0;
    vec4 p = invProjectionMatrix * vec4(texcoord * 2.0 - 1.0, terrainDepth * 2.0 - 1.0, 1.0);
    float distance = length(p.xyz / max(abs(p.w), 0.00001));
    float liquidDistance = drtLiquidRayDistance(drtFilteredLiquidDepth(), distance, terrainDepth);
    return 1.0 - exp(-0.055 * max(distance - liquidDistance, 0.0));
}

#include drtagx_deferred_directional.fsh

void main(void)
{
    vec4 color   = texture(gColor, texcoord);
    vec4 glowVec = texture(gGlow, texcoord);

    float mark = clamp(-glowVec.g - 1.0, 0.0, 1.0);

    vec4 ptColor = color;
    // Deferred contact classes occupy glow.g intervals (-4..-5 receiver,
    // -6..-7 foliage, -8..-9 grass, -10..-11 static no-cull).
    // Forward fading foliage uses +128 in glow.b only.
    float receiverTag = step(3.5, -glowVec.g) - step(5.5, -glowVec.g);
    float grassTag = step(7.5, -glowVec.g) - step(9.5, -glowVec.g);
    float foliageTag = step(5.5, -glowVec.g) - step(7.5, -glowVec.g);
    float staticNoCullTag = step(9.5, -glowVec.g);
    // Every no-cull class retains its existing sun-plane/normal-W contract.
    float noCullTag = grassTag + foliageTag + staticNoCullTag;
    float forwardFoliageTag = step(128.0, glowVec.b);
    float withoutForwardTag = glowVec.b - 128.0 * forwardFoliageTag;
    float casterTag = step(64.0, withoutForwardTag);
    float rawB = withoutForwardTag - 64.0 * casterTag;
    vec4 ptGlow  = vec4(glowVec.r, max(glowVec.g, -1.0), rawB,
        clamp(glowVec.a, 0.0, 1.0));
    vec4 ptGPos  = texture(gPositionIn, texcoord);
    // Wind receivers prepared PLS at their interpolated rest pose. Restore
    // ordinary fog packing before any output; every other deferred pass stays live.
    bool placedPrepared = ptGPos.w < 0.0;
    if (placedPrepared) ptGPos.w = -1.0 - ptGPos.w;

    if (mark <= 0.001) {
        outColor     = ptColor;
        outGlow      = ptGlow;
        outGPosition = ptGPos;
        return;
    }

    // B's tag-stripped payload retains continuous sunlight. Coverage channels
    // are unchanged; block brightness is recovered from independent local RGB.
    float actualVoxSun = drtUnpackDeferredSun(rawB);
    float voxSun = actualVoxSun;
    float blockBright = 0.0;

    vec3  albedo    = color.rgb;
    vec3  rawAlbedo = ptGPos.rgb;
    float specMap = clamp(-glowVec.g - 2.0 - 2.0 * receiverTag -
        4.0 * foliageTag - 6.0 * grassTag - 8.0 * staticNoCullTag, 0.0, 1.0);

    if (uDebug > 0) {
        outColor     = vec4(albedo, 1.0);
        outGlow      = vec4(glowVec.r, 0.0, 0.0, 1.0);
        outGPosition = ptGPos;
        return;
    }

    float projZ  = texture(gDepth, texcoord).r;
    vec4 ndcPos  = vec4(vec3(texcoord, projZ) * 2.0 - 1.0, 1.0);
    vec4 viewPos = invProjectionMatrix * ndcPos;
    viewPos.xyz /= viewPos.w;
    vec4 worldPos = invModelViewMatrix * vec4(viewPos.xyz, 1.0);

    vec4 nrm4   = texture(gNormal, texcoord);
    vec3  nrmWorld = (invModelViewMatrix * vec4(nrm4.xyz, 0.0)).xyz;
    float nrmLen2  = dot(nrmWorld, nrmWorld);
    vec3  normal   = nrmLen2 > 1e-8 ? nrmWorld * inversesqrt(nrmLen2) : vec3(0.0);

    if (drtDebugView != 0) {
        outColor = vec4(drtDeferredDebugColor(viewPos.xyz, nrm4.xyz, voxSun, glowVec.r), 1.0);
        outGlow = vec4(0.0);
        outGPosition = ptGPos;
        return;
    }

    // Only wind foliage uses the unshadowed/additive local-light route. Static
    // no-cull surfaces receive the solid terrain's shadowed maximum blend.
    drtDeferredRelight(albedo, blockBright, rawAlbedo, viewPos.xyz, nrm4.xyz, normal,
        voxSun, actualVoxSun, glowVec.r, placedPrepared ? -1.0 : grassTag + foliageTag);

    float fogAmount = clamp((ptGPos.w - glowVec.r) * 0.5, 0.0, 1.0);
    float murkiness = deferredMurkiness(projZ);

    // Square-root curve (inverse-square behavior): stays strong in mid daylight, drops steeply near 0
    float sunFactor = sqrt(voxSun);

    // Sun effects require both normalized sky access and native sky radiance
    // Shared sunlight effects apply access and sky radiance exactly once.

#if SHADOWQUALITY > 0
    // Directional filtering and contact occlusion remain owned shader helpers.
    drtSunFoliageReceiver = noCullTag > 0.5;
    float b = drtDeferredDirectionalBrightness(worldPos, blockBright, grassTag,
        receiverTag, sunFactor, viewPos.xyz, normal, nrm4.xyz);
    float intensity = 0.34 + (1.0 - shadowIntensity) / 8.0;

    sm_sunShadowBright = df_sunShadow;
    ox_prepare(normal, blockBright);
#else
    float b = 1.0;
    float intensity = 0.45;
    ox_prepare(normal, 0.0);
#endif

    // 2. Scale normal shading using square-root sunFactor
    // Foliage normal XYZ describes its shadow plane. W retains the authored
    // material sunlight shade, preventing the plane from darkening the card again.
    vec2 foliageLighting = drtSunUnpackFoliageLighting(nrm4.w);
    float baseNb = noCullTag > 0.5 ? foliageLighting.x : getBrightnessFromNormal(normal, 1.0, intensity);
    float nb = baseNb; // Common compositor applies access to sunlight only.

    outColor = applyFogAndShadowFromBrightness(vec4(albedo, 1.0),
        fogAmount, drtCombineSunDarkening(b, nb), worldPos.xyz);


    float bloomMask = 0.0;

#if SHADOWQUALITY > 0
    tl_applyBacklightValue(outColor.rgb, bloomMask, rawAlbedo, worldPos.xyz, df_sunShadow, 0.0,
        noCullTag > 0.5 ? foliageLighting.y : nrm4.w);
#endif

    if (specularStrength > 0.0) {
        float sunShadow = 1.0;
#if SHADOWQUALITY > 0
        sunShadow = df_sunShadow;
#endif
        sm_applySpecularValue(outColor.rgb, bloomMask, specMap, rawAlbedo, normal, worldPos.xyz, sunShadow, 0.0);
    }

    //bloomMask += ox_glare;

    float glow = 0.0;
#if SHINYEFFECT > 0
    glow = pow(max(0.0, dot(normal, lightPosition)), 6.0) * 0.125 * shadowIntensity * (1.0 - fogAmount - murkiness) * clamp(dot(drtSunEffectColor(), DRT_LUMINANCE), 0.0, 1.0);
#endif

    // Camera-relative reconstructed geometry; never fog packed G-buffer RGB.
    // Packed terrain is unfogged. Apply continuous boundary/weather transport
    // once after relighting, matching the forward path without depth dithering.
    outColor = drtApplySurfaceFog(outColor, worldPos.xyz, projZ, true, actualVoxSun);
    vec3 litRgb = outColor.rgb;
    outColor     = vec4(mix(ptColor.rgb, litRgb, mark), color.a);
    // Restore the emissive/bloom attachment for deferred terrain. Leaving
    // this output unwritten erased torch tips from glowParts downstream.
    outGlow = mix(ptGlow, vec4(glowVec.r + glow, 0.0,
        clamp(bloomMask, 0.0, 1.0), color.a), mark);
    //outColor.rgb = vec3(bloomMask);
    outGPosition = vec4(viewPos.xyz, ptGPos.w + murkiness * mark);
}
