#version 330 core
#extension GL_ARB_explicit_attrib_location: enable

// SheyderMod: vendored engine standard.fsh (generic block / item / entity pass) with a mod hook:
// the Specular highlight (inlined here, not #included, because FpHands recompiles this shader
// before the mod's shader includes are registered). Re-sync against the engine copy on updates.

// Native first-person items set USEOIT=1 but draw with standard alpha blending
// into the scene's four color/SSAO targets. Keep the native standard outputs;
// USEOIT alone does not identify an OIT framebuffer for this shader.
layout(location = 0) out vec4 outColor;
layout(location = 1) out vec4 outGlow;
#if SSAOLEVEL > 0
in vec4 gnormal;
layout(location = 2) out vec4 outGNormal;
layout(location = 3) out vec4 outGPosition;
#endif

uniform sampler2D tex;
uniform float extraGodray = 0;
uniform float alphaTest = 0.001;
uniform float ssaoAttn = 0;
uniform int applySsao = 1;
uniform int tempGlowMode;
uniform int drtSunSprite = 0;

// Texture overlay "hack"
// We only have the base texture UV coordinates, which, for blocks and items in inventory is the block or item texture atlas, but none uv coords for a dedicated overlay texture
// So lets remove the base offset (baseUvOrigin) and rescale the coords (baseTextureSize / overlayTextureSize) to get useful UV coordinates for the overlay texture
uniform sampler2D tex2dOverlay;
uniform float overlayOpacity;
uniform vec2 overlayTextureSize;
uniform vec2 baseTextureSize;
uniform vec2 baseUvOrigin;
uniform int normalShaded;
uniform int skyShaded;
uniform float damageEffect = 0;
#if defined(ALLOWDEPTHOFFSET)
#if ALLOWDEPTHOFFSET > 0
uniform float depthOffset;
#endif
#endif
uniform vec4 averageColor;

in vec2 uv;
in vec4 color;
in vec4 rgbaFog;
in float fogAmount;
in float glowLevel;
in vec4 rgbaGlow;
in vec4 camPos;
in vec4 worldPos;
in vec3 normal;
in float voxSunLight; 
flat in int renderFlags;


#define DRT_SURFACE_FOG_AFTER_LIGHTING
#include fogandlight.fsh
#include noise2d.ash
#include underwatereffects.fsh

// SheyderMod: sun direction (pushed by SpecularRenderer; stock standard has none)
uniform vec3 sunPosition;

// SheyderMod: Specular highlight -- specular.fsh inlined verbatim (NOT #included: FpHands
// recompiles standard before the mod includes are registered). Keep in sync with specular.fsh.
uniform sampler2D specularTex;
uniform float specularStrength;
uniform vec3 sm_eyeOffset;
const float SM_TINT = 0.5;

void sm_applySpecularValue(inout vec3 color, float specMap, vec3 albedo, vec3 N, vec3 wpos,
                           float shadowB, float fog)
{
    
    if (specMap <= 0.0) return;          // non-specular pixel
    if (fog >= 1.0)     return;          // fully fogged out

    // Sun specular requires normalized sky access and current sky radiance.
    float sunFactor = sqrt(clamp(voxSunLight, 0.0, 1.0));
    specMap *= sunFactor;

    float NdotL = max(dot(N, sunPosition), 0.0);
    if (NdotL <= 0.0) return;            // face away from the sun

    float shadowGate = smoothstep(0.7, 0.95, shadowB);
    if (shadowGate <= 0.0) return;       // shadowed/penumbra

    // wpos is render-origin-relative (origin = player feet), eye sits at sm_eyeOffset.
    vec3 V = normalize(sm_eyeOffset - wpos);
    vec3 H = normalize(sunPosition + V);
    float NdotH = max(dot(N, H), 0.0);

    // pow(NdotH, 64) via repeated squaring (6 muls, no transcendental).
    float e = NdotH * NdotH;
    e *= e;
    e *= e;
    e *= e;
    e *= e;
    e *= e;

    float spec = e * smoothstep(0.0, 0.15, NdotL);
    float amount = spec * specMap * specularStrength * shadowGate;

    // Tint the white glint with the albedo hue only (normalize to brightest channel).
    vec3 tintHue = albedo / max(max(albedo.r, albedo.g), max(albedo.b, 1e-4));
#ifdef DRT_EXPLICIT_SURFACE_LIGHTING
    // The native startup include has no explicit sky component. Specular
    // enhancements begin with the mod lighting interface, preserving its gate.
    color += amount * max(drtSkyLight, vec3(0.0)) * mix(vec3(1.0), tintHue, SM_TINT);
#endif
}

void sm_applySpecular(inout vec3 color, vec2 sampleUv, vec3 albedo, vec3 N, vec3 wpos,
                      float shadowB, float fog)
{
    if (specularStrength <= 0.0) return;
    sm_applySpecularValue(color, texture(specularTex, sampleUv).r, albedo, N, wpos, shadowB, fog);
}
// SheyderMod end

#ifdef DRT_FOG_TRANSPORT
void drtComposeSunSprite(vec4 texColor) {
    vec3 direction = normalize(worldPos.xyz);
    DrtFogTransport transport = drtSkyMediumTransport(direction);
    bool waterRay = drtAtmosphereView.z > 0.7 || cameraUnderwater > 0.7
        || drtFilteredLiquidDepth() < 0.999999;
    vec3 background = waterRay
        ? drtApplyTransport(vec4(drtClearSkyRadiance(direction), 1.0), transport).rgb
        : drtSkyBackground(direction);
    // Match celestialobject: sky background plus self-emitted HDR, with weather
    // extinction once. Surface shading/fog would dim the disk and its bloom.
    outColor = vec4(background + texColor.rgb * DRT_SUN_SPRITE_GAIN * transport.transmittance, texColor.a);
    float visibility = max(max(transport.transmittance.r, transport.transmittance.g), transport.transmittance.b);
    outGlow = vec4(glowLevel * visibility, extraGodray * visibility, 0.0, texColor.a);
    if (drtAtmosphereView.z > 0.7) outGlow.rg = vec2(0.0);
#if SSAOLEVEL > 0
    // Zero is the native sky sentinel; a sun quad must not occlude or receive AO.
    outGPosition = vec4(0.0);
    outGNormal = vec4(0.0);
#endif
}
#endif

void main() {
#ifdef DRT_FOG_TRANSPORT
    if (drtSunSprite != 0 && drtAtmosphereFlags.x > 0.5) {
        vec4 texColor = texture(tex, uv) * color;
        if (texColor.a < alphaTest) discard;
        drtComposeSunSprite(texColor);
        return;
    }
#endif
    float b = 1;
    
    if (damageEffect > 0) {
       float f = cnoise2(floor(vec2(uv.x, uv.y) * 4096) / 4);
       if (f < damageEffect - 1.3) discard;
       b = min(1, f * 1.5 + 0.65 + (1-damageEffect));
    }

    if (overlayOpacity > 0) {
       vec2 uvOverlay = (uv - baseUvOrigin) * (baseTextureSize / overlayTextureSize);

       vec4 col1 = texture(tex2dOverlay, uvOverlay);
       vec4 col2 = texture(tex, uv);

       float a1 = overlayOpacity * col1.a  * min(1, col2.a * 100);
       float a2 = col2.a * (1 - a1);

       outColor = vec4(
         (a1 * col1.r + col2.r * a2) / (a1+a2),
         (a1 * col1.b + col2.g * a2) / (a1+a2),
         (a1 * col1.g + col2.b * a2) / (a1+a2),
         a1 + a2
       ) * color;

    } else {
       outColor = texture(tex, uv) * color;
    }

    // Shade only the explicit sunlight before adding thermal/authored emission.
#ifdef DRT_EXPLICIT_SURFACE_LIGHTING
    drtPrepareSunGrid(worldPos.xyz, normal);
#endif
    float sunVisibility = skyShaded > 0 ? getBrightnessFromShadowMap() : 1.0;
    if (normalShaded > 0) sunVisibility = min(sunVisibility, getBrightnessFromNormal(normal, 1.0, 0.45));
#ifdef DRT_EXPLICIT_SURFACE_LIGHTING
    sunVisibility = mix(1.0, sunVisibility, sqrt(clamp(sm_voxSunLight, 0.0, 1.0)));
    ox_prepare(normal, 0.0);
    vec3 sunBoost = vec3(1.0);
    ox_apply(sunBoost, worldPos.xyz);
    sunVisibility *= 1.0 + max(0.0, shadowIntensity * 2.0 - 1.66) / 1.5;
    outColor.rgb = drtShadeSurface(outColor.rgb, sunVisibility, sunBoost);
#else
    // Before mod startup, apply the native include's brightness to its prelit
    // color. The later shared-include compile retains sun-only composition above.
    outColor.rgb *= sunVisibility;
#endif

    // SheyderMod: raw albedo for the specular tint
    vec3 smAlbedo = texture(tex, uv).rgb;

#if BLOOM == 0
    outColor.rgb *= 1 + glowLevel;
#endif

    if (tempGlowMode == 1) {
       float f = (averageColor.r+averageColor.g+averageColor.b) / (rgbaGlow.r+rgbaGlow.g+rgbaGlow.b);
       f=max(f,0.6);
       outColor.rgb = mix(outColor.rgb, outColor.rgb * rgbaGlow.rgb / f, min(1.5, glowLevel*2));
       
    } else {
       outColor.rgb = mix(outColor.rgb, rgbaGlow.rgb, glowLevel * rgbaGlow.a);
    }

    float murkiness = skyShaded > 0 ? getSkyMurkiness() : getUnderwaterMurkiness();
#ifndef DRT_FOG_TRANSPORT
    outColor = applyFog(outColor, murkiness > 0.0 ? 0.0 : fogAmount);
    if (murkiness > 0.0) outColor.rgb = applyUnderwaterEffects(outColor.rgb, murkiness);
#endif

    // SheyderMod: Specular highlight (held/dropped items, placed-BE renderers, firepit pot)
    if (specularStrength > 0.0) {
    #if SHADOWQUALITY > 0
       float smSunB = clamp(getBrightnessFromShadowMap(), 0.0, 16.0);
    #else
       float smSunB = 1.0;
    #endif
       sm_applySpecular(outColor.rgb, uv, smAlbedo, normalize(normal), worldPos.xyz, smSunB, 0.0);
    }

#if NORMALVIEW == 0
    if (outColor.a < alphaTest) discard;
#endif
    
    float glow = 0;
#if SHINYEFFECT > 0 
#ifdef DRT_FOG_TRANSPORT
    // Reflection is part of surface radiance; extinction is applied once below.
    outColor = applyReflectiveEffect(outColor, glow, renderFlags, uv, normal, worldPos, camPos, vec3(1));
#else
    outColor = mix(applyReflectiveEffect(outColor, glow, renderFlags, uv, normal, worldPos, camPos, vec3(1)), outColor, min(1, 2 * fogAmount));
#endif
    glow = pow(max(0.0, dot(normal, lightPosition)), 6) / 8 * shadowIntensity * (1 - fogAmount);
#ifdef DRT_EXPLICIT_SURFACE_LIGHTING
    glow *= clamp(dot(drtSunEffectColor(), DRT_LUMINANCE), 0.0, 1.0);
#endif
#endif

#if SSAOLEVEL > 0
    if (applySsao > 0) {
       outGPosition = vec4(camPos.xyz, fogAmount + glowLevel);
    } else {
       outGPosition = vec4(camPos.xyz, 1);
    }
    outGNormal = vec4(gnormal.xyz, ssaoAttn);
    
#endif

#if NORMALVIEW > 0
    outColor = vec4((normal.x + 1) / 2, (normal.y + 1)/2, (normal.z+1)/2, 1);  
#endif

    outColor.rgb *= b;
#ifdef DRT_FOG_TRANSPORT
    outColor = drtApplySurfaceFog(outColor, worldPos.xyz, gl_FragCoord.z, true, voxSunLight);
#endif
    outGlow = vec4(glowLevel + glow, extraGodray - fogAmount, 0, outColor.a);

#if defined(ALLOWDEPTHOFFSET)
#if ALLOWDEPTHOFFSET > 0
    gl_FragDepth = gl_FragCoord.z + depthOffset;

    #if SSAOLEVEL > 0
       outGPosition.w=1;
    #endif
#endif
#endif
}
