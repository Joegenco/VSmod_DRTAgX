#version 330 core
#extension GL_ARB_shader_storage_buffer_object : require
#extension GL_ARB_shading_language_420pack : require
#extension GL_ARB_explicit_attrib_location: enable

// ============================================================================
// DRTAgX / SheyderMod: Chunk Opaque Fragment Shader (G-Buffer & Forward Pass)
// ----------------------------------------------------------------------------
// G-Buffer Output Layout:
//   outColor     (location 0) : Albedo times voxel blocklight + emission for marked pixels (RGBA).
//   outGlow      (location 1) : [r: glowLevel, g: -(2.0 + specD + contactClass), b: encodedB, a: fogAmount + alpha].
//                               - encodedB: continuousSunAccess * 63.0 + (isSolidCaster ? 64.0 : 0.0)
//                               - contactClass: 8.0 = static no-cull, 6.0 = grass receiver, 4.0 = wind foliage, 2.0 = solid terrain, 0.0 = alpha-test
//   outGNormal   (location 2) : View normal (actual plane for no-cull); no-cull W packs sunlight shade/backlight class.
//   outGPosition (location 3) : Raw unlit albedo for subsurface scattering and depth reconstruction.
// ============================================================================

uniform sampler2D terrainTex;
uniform sampler2D terrainTexLinear;

in vec4 rgba;
in vec4 rgbaFog;
in float fogAmount;
in vec2 uv;
in float glowLevel;
flat in int renderFlags;
in float drtWindPresence;
in vec3 drtPlacedRestPos;
in vec3 normal;
in vec4 worldPos;
in vec3 vertexPosition;
in vec3 blockLight;
in vec4 gnormal;
in vec4 camPos;
in float lod0Fade;
in float nb;
in float voxSunLight;

uniform float alphaTest;
uniform float fogDensityIn;
uniform float fogMinIn;
uniform float horizonFog;
uniform vec3 sunPosition;
uniform float dayLight;
uniform int haxyFade;
uniform mat4 modelViewMatrix;

// SheyderMod: Deferred Lighting toggle (0 = vanilla forward, 1 = raw G-buffer fill for the relight pass).
uniform int deferredMode;
// The terrain bridge sets this after relight for Decor replay and late native
// OpaqueWaterPlant draws, then clears it before the next G-buffer fill. Both need
// complete forward radiance, including in the compile-time performance variant.
uniform int drtForwardDecalPass = 0;

// SheyderMod: Deferred performance-mode compile-time gate.
#ifndef SHEYDER_DEFERRED
#define SHEYDER_DEFERRED 0
#endif

layout(location = 0) out vec4 outColor;
layout(location = 1) out vec4 outGlow;
#if SSAOLEVEL > 0
layout(location = 2) out vec4 outGNormal;
layout(location = 3) out vec4 outGPosition;
#endif

#include vertexflagbits.ash
#define DRT_SURFACE_FOG_AFTER_LIGHTING
#define DRT_TERRAIN_PLACED_OCCLUSION
#include fogandlight.fsh
#include dither.fsh
#include skycolor.fsh
#include colormap.fsh
#include underwatereffects.fsh
#include drtagx_terrain_boundary.fsh
#include drtagx_terrain_placed_occlusion.fsh

// SheyderMod: SubSurface backlight include
#include drtagx_sun_subsurface.fsh

// SheyderMod: Specular highlight include
#include drtagx_sun_specular.fsh

// SheyderMod: deferred G-buffer fill (blend-safe channel packing), shared by the runtime
// branch and the performance-mode variant of main().
vec3 drtTerrainSunPlaneNormal = vec3(0.0);
void sm_deferredFill(vec4 texColor, vec3 rawAlbedo)
{
    // gnormal is a vertex OUTPUT only under SSAOLEVEL>0 (chunkopaque.vsh). Reading it here
    // unconditionally makes it a statically-used fragment INPUT with no matching vertex output
    // when SSAO is off -- Mesa/RadeonSI rejects that as a hard link error and the game crashes on
    // the SSAO-off shader recompile (NVIDIA silently tolerates it). gnw is consumed only inside the
    // SSAOLEVEL>0 block below, so guard the read to keep the input inactive when SSAO is off.
    float gnw = 0.0;
#if SSAOLEVEL > 0
    gnw = gnormal.w;
#endif
#if SHADOWQUALITY > 0
    gnw = tl_isWind;
#endif
    // One cheap R8 read; pages without specular data are a shared black, and the strength
    // gate keeps it free while the Specular effect is off.
    float specD = specularStrength > 0.0 ? texture(specularTex, uv).r : 0.0;

    // Use B's full payload for continuous sunlight; blocklight RGB is already
    // in outColor. Alpha remains native coverage, including while blending.
    float encodedB = drtPackDeferredSun(voxSunLight);
    // Bit-like +64 tag in the existing float G-buffer: only solid, static
    // opaque terrain may occlude a dynamic light. Wind and no-cull foliage do not.
    bool drtSolidCaster = haxyFade <= 0.0 && (renderFlags & WindModeBitMask) == 0;
    encodedB += drtSolidCaster ? 64.0 : 0.0;
    // No-cull is a raster pass, not a foliage material: stationary doors and
    // paintings need solid placed-light receiving/compositing. Interpolated wind
    // presence includes fixed roots in a triangle with moving grass tips.
    bool drtStaticNoCull = haxyFade > 0 && drtWindPresence <= 0.0;
    int windMode = renderFlags & WindModeBitMask;
    bool drtGrassReceiver = haxyFade > 0 && windMode != 0 && windMode != WindModeLeavesMask;
    // -8..-9 identifies non-leaf wind foliage for receiver-only shadow bias.
    // It remains a foliage contact caster and keeps its normal shading.
    float contactClass = drtStaticNoCull ? 8.0 : drtGrassReceiver ? 6.0 : haxyFade > 0 ? 4.0 :
        (alphaTest < 0.01 ? 2.0 : 0.0);

    // Opaque-pass pixels have already passed alpha testing and own their depth.
    // Keep genuine no-cull fades separate so they can retain authored alpha.
    // The fullscreen relight has depth, but no undeformed wind position.
    // Prepare PLS here so self-occlusion stays attached to the swaying card.
    bool placedPrepared = drtWindPresence > 0.0 && drtTerrainPlacedCount > 0;
    if (placedPrepared) drtPrepareTerrainPlacedVisibility(drtPlacedRestPos, true, glowLevel);
    outColor = vec4(rawAlbedo * (drtVoxelLight * drtTerrainPlacedVisibility +
        drtTerrainPlacedRadiance + drtEmissionLight), drtSolidCaster ? 1.0 : texColor.a);
#if SSAOLEVEL > 0
    // gposition.xyz carries the RAW albedo (SubSurface backlight base), NOT camPos: the relight
    // rebuilds view position from the depth buffer for marked pixels, so these three channels
    // would otherwise go unread. .w keeps the vertex-interpolated fog packing.
    outGPosition = vec4(rawAlbedo, fogAmount * 2 + glowLevel);
    // Negative W tags prepared PLS without adding an attachment or reducing
    // sunlight precision. Relight restores the native nonnegative fog payload.
    if (placedPrepared) outGPosition.w = -1.0 - outGPosition.w;
    outGNormal = vec4(gnormal.xyz, gnw);
    if (haxyFade > 0) {
        // The bent plane supplies sunlight receiving. Wind PLS uses its rest pose.
        // W preserves the authored vertex sunlight shade used in forward mode.
        outGNormal.xyz = mat3(modelViewMatrix) * drtTerrainSunPlaneNormal;
        outGNormal.w = drtSunPackFoliageLighting(nb, gnw);
    }
#endif
    outGlow = vec4(glowLevel, -(2.0 + specD + contactClass), encodedB,
        min(1.0, fogAmount + texColor.a));
}

void main()
{
    drtCaptureTerrainPlacedPlane(drtPlacedRestPos);
    int windMode = renderFlags & WindModeBitMask;
    bool drtGrassReceiver = haxyFade > 0 && windMode != 0 && windMode != WindModeLeavesMask;
    // Capture primitive derivatives before any terrain/alpha discard or per-pixel
    // deferred return. The authored normal still controls ordinary material lighting.
    drtTerrainSunPlaneNormal = normal;
    drtSunFoliageReceiver = haxyFade > 0;
    if (haxyFade > 0)
        drtTerrainSunPlaneNormal = drtSunPrimitiveNormal(worldPos.xyz, normal);

    // Release fully hidden terrain before G-buffer/forward draws; no pixel mask.
    drtDiscardTerrainBoundary(worldPos.xyz);
    // Keep mappedTex separate: smooth per-pixel texture color WITHOUT vertex-rgba.
    // Used as the backlight base so quad triangulation seams don't bleed through.
    vec4 mappedTex = getColorMapped(terrainTexLinear, texture(terrainTex, uv));
    vec4 texColor  = mappedTex * rgba;

    // >>> SheyderMod: early alpha-test discard (hoisted from the post-shading test)
#if NORMALVIEW == 0
    float aTest = texColor.a + max(0.0, 1.0 - rgba.a) * min(1, texColor.a * 10) - lod0Fade;
    if ((renderFlags & WindModeBitMask) == WindModeWeakLowAlphaTest) aTest *= 4;
    if (aTest < alphaTest || rgba.a < 0.005) discard;
#endif
    // <<< SheyderMod

    // Apply material effects before illumination so both deferred and forward
    // paths retain the same altered albedo and explicit lighting components.
    if (psychedelicStrength > Epsilon) mappedTex = applyPsychedelicEffect(mappedTex, vertexPosition*2, 0);
    if (glitchStrength > Epsilon) mappedTex = applyRustEffect(mappedTex, normal, vertexPosition, 1);
    texColor = mappedTex * rgba;

    // >>> SheyderMod: Deferred Lighting (raw G-buffer fill; relit by the fullscreen pass)
    // The performance variant must honor the same exclusion as the runtime
    // path: blended no-cull and reflective pixels need their forward shader.
    // Native OpaqueNoCull cuts at .42; BlendNoCull cuts at .25 and enables GL_BLEND.
    // Keep every blended batch forward even when vertex alpha is one, so normal-W
    // receiver metadata never participates in source-alpha attachment blending.
    bool smDeferrable = !(haxyFade > 0 && (rgba.a < 0.999 || alphaTest < 0.4));
#if SHINYEFFECT > 0
    smDeferrable = smDeferrable && (renderFlags & ReflectiveBitMask) == 0;
#endif
    if (drtForwardDecalPass == 0 && (deferredMode > 0 || SHEYDER_DEFERRED > 0) && smDeferrable) {
        sm_deferredFill(texColor, mappedTex.rgb);
        return;
    }
    // <<< SheyderMod

    drtPrepareTerrainPlacedVisibility(drtPlacedRestPos, drtWindPresence > 0.0, glowLevel);
    float murkiness = getUnderwaterMurkiness();

    // SheyderMod: Overexposure per-fragment inputs (consumed inside applyFogAndShadowFromBrightness).
    // Free here -- normal and blockBrightness are already fragment inputs of this shader.
#if SHADOWQUALITY > 0
    ox_prepare(normal, blockBrightness);
#else
    ox_prepare(normal, 0.0);
#endif

    drtSunReceiverBiasScale = drtGrassReceiver ? 4.0 : 1.0;
    drtPrepareSunGrid(worldPos.xyz, drtTerrainSunPlaneNormal);
    // This plane is geometric, independently of the grid toggle or material shade.
    drtForwardSunPlaneNormal = drtTerrainSunPlaneNormal;
    float b = getBrightnessFromShadowMap();
    outColor = applyFogAndShadowFromBrightness(texColor, fogAmount, drtCombineSunDarkening(b, nb), worldPos.xyz);

    float glow = 0;
    float godrayLevel = 0;

    if (haxyFade > 0) {           // test the uniform first, for higher performance
        if (rgba.a < 0.999) {
            vec4 skyColor = vec4(1);
            vec4 skyGlow = vec4(1);
            float sealevelOffsetFactor = 0.25;

            getSkyColorAt(worldPos.xyz, sunPosition, sealevelOffsetFactor, clamp(dayLight, 0, 1), horizonFog, skyColor, skyGlow);
            godrayLevel = skyGlow.g;
            outColor.rgb = mix(skyColor.rgb, outColor.rgb, max(1-dayLight, max(0.0, rgba.a)));
        }
    }



    // SheyderMod: bloom mask accumulator (SubSurface + Specular -> outGlow.b for the effect bloom)
    float bloomMask = 0.0;

    // SheyderMod: SubSurface backlight
#if SHADOWQUALITY > 0
    tl_applyBacklight(outColor.rgb, bloomMask, mappedTex.rgb, worldPos.xyz, b);
#endif

    // Complete specular/reflections before shared transport applies extinction.
    sm_applySpecular(outColor.rgb, bloomMask, uv, mappedTex.rgb, normal, worldPos.xyz, sm_sunShadowBright, 0.0);


#if SHINYEFFECT > 0
    if ((renderFlags & ReflectiveBitMask) != 0) {
        outColor = mix(applyReflectiveEffect(outColor, glow, renderFlags, uv, normal, worldPos, camPos, blockLight), outColor, clamp(2*(1-b), 0, 1));
    }
    //glow += pow(max(0.0, dot(normal, lightPosition)), 6) * 0.125 * shadowIntensity * (1 - fogAmount - murkiness);
#endif




    // Fog attenuates the completed HDR surface once.
    outColor = drtApplySurfaceFog(outColor, worldPos.xyz, gl_FragCoord.z, true, voxSunLight);

#if SSAOLEVEL > 0
    outGPosition = vec4(camPos.xyz, fogAmount * 2 + glowLevel + murkiness);
    outGNormal = gnormal;
#endif

#if NORMALVIEW > 0
    outColor = vec4((normal.x + 1) / 2, (normal.y + 1)/2, (normal.z+1)/2, 1);
#endif

    // SheyderMod: Overexposure glare. It clipped its own output on purpose, so the amount it
    // clipped by is passed on directly -- measuring it back out of an RGBA8 colour is impossible.
    // bloomMask += ox_glare;

    // SheyderMod: outGlow.b = bloom mask
    // Fading no-cull pixels can take this forward branch. Keep their depth
    // identifiable to the deferred contact ray only while relight is active.
    outGlow = vec4(glowLevel + glow, godrayLevel,
        clamp(bloomMask, 0.0, 1.0) + ((deferredMode > 0 || SHEYDER_DEFERRED > 0) && haxyFade > 0 ? 128.0 : 0.0),
        min(1, fogAmount + outColor.a));

//	outColor = vec4(1);
}
