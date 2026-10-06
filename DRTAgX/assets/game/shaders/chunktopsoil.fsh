#version 330 core
#extension GL_ARB_shader_storage_buffer_object : require
#extension GL_ARB_shading_language_420pack : require

// SheyderMod: vendored engine chunktopsoil.fsh (soil / grass-overlay pass) with mod hooks:
// the deferred G-buffer fill / relight path and the Specular highlight (compositing the dirt
// base under the grass overlay). Mod additions are wrapped in SheyderMod markers; re-sync
// against the engine copy on updates.

uniform sampler2D terrainTex;
uniform sampler2D terrainTexLinear;

uniform float alphaTest = 0.01;
uniform vec2 blockTextureSize;
uniform vec3 sunPosition;

// SheyderMod: Deferred Lighting toggle (0 = vanilla forward).
uniform int deferredMode;

// SheyderMod: Deferred performance-mode compile-time gate.
#ifndef SHEYDER_DEFERRED
#define SHEYDER_DEFERRED 0
#endif

in vec4 rgba;
in vec4 rgbaFog;
in float fogAmount;
in vec2 uv;
in vec2 uv2;
in float glowLevel;
in vec3 blockLight;
in vec4 worldPos;
in vec3 vertexPosition;

flat in int renderFlags;
in vec3 normal;
in float drtVoxSunLight;
in vec4 gnormal;

layout(location = 0) out vec4 outColor;
layout(location = 1) out vec4 outGlow;
#if SSAOLEVEL > 0
in vec4 fragPosition;
layout(location = 2) out vec4 outGNormal;
layout(location = 3) out vec4 outGPosition;
#endif

#include vertexflagbits.ash
#define DRT_SURFACE_FOG_AFTER_LIGHTING
#define DRT_TERRAIN_PLACED_OCCLUSION
#include fogandlight.fsh
#include colormap.fsh
#include noise3d.ash
#include underwatereffects.fsh
#include drtagx_terrain_boundary.fsh
#include drtagx_terrain_placed_occlusion.fsh

// SheyderMod: Specular highlight include
#include drtagx_sun_specular.fsh

// SheyderMod: deferred G-buffer fill (topsoil variant), shared by the runtime and performance-mode paths.
void sm_deferredFill(vec4 texColor, vec3 rawAlbedo)
{
    float aTestD = rgbaFog.a + max(0.0, 1.0 - rgba.a) * min(1.0, rgbaFog.a * 10.0);
#if NORMALVIEW == 0
    if (aTestD < alphaTest || rgbaFog.a < 0.005) discard;
#endif

    float specD = 0.0;
    if (specularStrength > 0.0) {
        if (normal.y >= 0) {
            vec2 grassUv = uv2 + vec2(blockTextureSize.x * normal.y, 0);
            specD = mix(texture(specularTex, uv).r,
                        texture(specularTex, grassUv).r,
                        texture(terrainTex, grassUv).a);
        } else {
            specD = texture(specularTex, uv).r;
        }
    }

    // Carry continuous sunlight in B; keep every alpha/coverage output intact.
    // Topsoil is solid terrain; +64 tags it as a dynamic-shadow caster.
    float encodedB = drtPackDeferredSun(drtVoxSunLight) + 64.0;

    outColor = vec4(rawAlbedo * (drtVoxelLight + drtEmissionLight), rgbaFog.a);
#if SSAOLEVEL > 0
    // outGPosition.rgb carries unlit rawAlbedo (fixes faint striping where spatial coords were read as backlight color)
    outGPosition = vec4(rawAlbedo, fogAmount * 2.0 + glowLevel);
    outGNormal = gnormal;
#endif
    // The -4..-5 deferred-marker interval identifies topsoil as a receiver.
    outGlow = vec4(glowLevel, -(4.0 + specD), encodedB,
        min(1.0, fogAmount + texColor.a));
}

void main()
{
    drtCaptureTerrainPlacedPlane(worldPos.xyz);
    // Match the opaque base's fully concealed endpoint, preserving solid depth.
    drtDiscardTerrainBoundary(worldPos.xyz);
    vec4 rawBrownSoil   = texture(terrainTex, uv);
    vec4 brownSoilColor = rawBrownSoil * rgba;

    vec4 texColor;
    vec3 rawAlbedo;

    if (normal.y >= 0) {
        // Top (normal.y == 1) or Sides (normal.y == 0)
        vec2 grassUv     = uv2 + vec2(blockTextureSize.x * normal.y, 0);
        vec4 grassTex    = texture(terrainTex, grassUv);
        float grassAlpha = grassTex.a;

        vec4 rawGrass   = getColorMapped(terrainTexLinear, grassTex);
        vec4 grassColor = rawGrass * rgba;

        texColor  = mix(brownSoilColor, grassColor, grassAlpha);
        rawAlbedo = mix(rawBrownSoil.rgb, rawGrass.rgb, grassAlpha);
    } else {
        // Bottom
        texColor  = brownSoilColor;
        rawAlbedo = rawBrownSoil.rgb;
    }

    texColor.a = 1.0;
    vec3 smAlbedo = texColor.rgb;

    vec4 material = vec4(rawAlbedo, 1.0);
    if (psychedelicStrength > Epsilon) material = applyPsychedelicEffect(material, vertexPosition*2, 0);
    if (glitchStrength > Epsilon) material = applyRustEffect(material, normal, vertexPosition, 1);
    rawAlbedo = material.rgb;
    texColor.rgb = rawAlbedo * rgba.rgb;

    // >>> SheyderMod: Deferred Lighting (raw G-buffer fill; relit by the fullscreen pass)
#if SHEYDER_DEFERRED > 0 && SSAOLEVEL > 0 && NORMALVIEW == 0
    sm_deferredFill(texColor, rawAlbedo);
#else
    if (deferredMode > 0) {
        bool smDeferrable = true;
#if SHINYEFFECT > 0
        smDeferrable = (renderFlags & ReflectiveBitMask) == 0;
#endif
        if (smDeferrable) {
            sm_deferredFill(texColor, rawAlbedo);
            return;
        }
    }
    // <<< SheyderMod


    drtPrepareTerrainPlacedVisibility(worldPos.xyz);
    outColor = texColor;

    float murkiness = getUnderwaterMurkiness();

    #if SHADOWQUALITY > 0
    float intensity = 0.34 + (1 - shadowIntensity)/8.0;
    ox_prepare(normal, blockBrightness);
    #else
    float intensity = 0.45;
    ox_prepare(normal, 0.0);
    #endif

    outColor = applyFogAndShadowWithNormal(outColor, fogAmount, normal, 1, intensity, worldPos.xyz);


    // SheyderMod: bloom mask accumulator (Specular glint -> outGlow.b)
    float bloomMask = 0.0;

    // SheyderMod: apply Specular highlight (dirt base composited under grass overlay by grass alpha)
    if (specularStrength > 0.0) {
        float specMap;
        if (normal.y >= 0) {
            vec2 grassUv = uv2 + vec2(blockTextureSize.x * normal.y, 0);
            specMap = mix(texture(specularTex, uv).r,
                          texture(specularTex, grassUv).r,
                          texture(terrainTex, grassUv).a);
        } else {
            specMap = texture(specularTex, uv).r;
        }

        if (specMap > 0.0 && dot(normal, sunPosition) > 0.0 && murkiness < 1.0) {
            getBrightnessFromShadowMap();
            sm_applySpecularValue(outColor.rgb, bloomMask, specMap, smAlbedo, normal, worldPos.xyz, sm_sunShadowBright, 0.0);
        }
    }

    outColor.a = rgbaFog.a;

    float aTest = outColor.a;
    aTest += max(0.0, 1 - rgba.a) * min(1, outColor.a * 10);
#if NORMALVIEW == 0    
    if (aTest < alphaTest || outColor.a < 0.005) discard;
#endif

    float glow = 0;

#if SHINYEFFECT > 0
    if ((renderFlags & ReflectiveBitMask) > 0) {
        vec3 worldVec = normalize(worldPos.xyz);
        float angle = 2 * dot(normalize(normal), worldVec);
        angle += gnoise(vec3(uv.x*500, uv.y*500, worldVec.z/10)) / 7.5;        
        outColor.rgb = drtSunOnlyMultiplier(outColor.rgb, max(vec3(1), vec3(1) + 3*drtSunEffectColor() * gnoise(vec3(worldVec.x/10 + angle, worldVec.y/10 + angle, worldVec.z/10 + angle))));
    }
    
    glow = pow(max(0.0, dot(normal, lightPosition)), 6) * 0.1 * shadowIntensity * (1 - fogAmount) * clamp(dot(drtSunEffectColor(), DRT_LUMINANCE), 0.0, 1.0);
#endif    

    // Fog attenuates the completed HDR surface once.
    outColor = drtApplySurfaceFog(outColor, worldPos.xyz, gl_FragCoord.z, true, drtVoxSunLight);

#if SSAOLEVEL > 0
    outGPosition = vec4(fragPosition.xyz, fogAmount * 2 + glowLevel);
    outGNormal = gnormal;
#endif

#if NORMALVIEW > 0
    outColor = vec4((normal.x + 1) / 2, (normal.y + 1)/2, (normal.z+1)/2, 1);    
#endif

    // bloomMask += ox_glare;

    // outGlow = vec4(glowLevel + glow, 0, clamp(bloomMask, 0.0, 1.0), outColor.a);
#endif
}
