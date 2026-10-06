#version 330 core
#extension GL_ARB_explicit_attrib_location: enable

// SheyderMod: vendored engine entityanimated.fsh (animated entity pass) with a mod hook: the
// Specular highlight (inlined here, not #included, because FpHands recompiles this shader before
// the mod's shader includes are registered). Re-sync against the engine copy on updates.

in vec2 uv;
in vec4 color;
in vec4 rgbaFog;
in float fogAmount;
in float glowLevel;
in vec3 vertexPosition;
flat in int renderFlags;
in vec3 normal;
in vec4 worldPos;
in vec3 blockLight;
in vec4 camPos;
in float damageEffect;
in float fragFrostAlpha;
in float voxSunLight;

// Our include system is dumb and does not do conditional includes
// So we add a OIT preprocceor test to oit.fsh as well
#include oit.fsh

#if USEOIT==0
    layout(location = 0) out vec4 outColor;
    layout(location = 1) out vec4 outGlow;
    #if SSAOLEVEL > 0
    in vec4 fragPosition;
    in vec4 gnormal;
    layout(location = 2) out vec4 outGNormal;
    layout(location = 3) out vec4 outGPosition;
    #endif
#endif

uniform sampler2D entityTex;
uniform float alphaTest = 0.001;
uniform float glitchEffectStrength;
uniform int entityId;
uniform int glitchFlicker;
#if defined(ALLOWDEPTHOFFSET)
#if ALLOWDEPTHOFFSET > 0
uniform float depthOffset;
#endif
#endif

#include vertexflagbits.ash
#define DRT_SURFACE_FOG_AFTER_LIGHTING
#include fogandlight.fsh
#include noise3d.ash
#include noise2d.ash
#include underwatereffects.fsh

// SheyderMod: sun direction (pushed by SpecularRenderer; stock entityanimated has none)
uniform vec3 sunPosition;

// SheyderMod: Specular highlight -- specular.fsh inlined verbatim (NOT #included: FpHands
// recompiles entityanimated before the mod includes are registered). Keep in sync with specular.fsh.
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
    // Explicit sky-gated specular starts once the mod include is available;
    // the earlier native shader has no separate sky-radiance interface.
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

void main() {
    float b = 1;
    
    if (damageEffect > 0) {
        float f = cnoise2(floor(vec2(uv.x, uv.y) * 4096) / 4);
        if (f < damageEffect - 1.3) discard;
        b = min(1, f * 1.5 + 0.65 + (1-damageEffect));
    }

    vec4 texColor = texture(entityTex, uv);

    // SheyderMod: raw albedo for the specular tint (before frost/psychedelic/rust/color)
    vec3 smAlbedo = texColor.rgb;

    #if SHADOWQUALITY > 0
    float intensity = 0.34 + (1 - shadowIntensity)/8.0;
    #else
    float intensity = 0.45;
    #endif
    
    int eidfloor = (entityId / 100) * 100;
    float seed = (entityId - eidfloor) / 5.0;
        
    texColor = applyFrostEffect(fragFrostAlpha, texColor, normal, vertexPosition + vec3(seed));
    if (psychedelicStrength > Epsilon) texColor = applyPsychedelicEffect(texColor, vertexPosition, 0);
    if (glitchStrength > Epsilon) texColor = applyRustEffect(texColor, normal, vertexPosition + vec3(seed), 0);
    
    texColor *= color;
    texColor.rgb *= b;

#if USEOIT>0
    vec4 outColor;
#endif
    
    float murkiness = getUnderwaterMurkiness();
#ifdef DRT_FOG_TRANSPORT
    outColor = applyFogAndShadowWithNormal(texColor, fogAmount, normal, 1, intensity, worldPos.xyz);
#else
    // Use the murkiness declared above for the native startup fallback.
    if (murkiness > 0) {
        outColor = applyFogAndShadowWithNormal(texColor, 0, normal, 1, intensity, worldPos.xyz);
        outColor.rgb = applyUnderwaterEffects(outColor.rgb, murkiness);    
    }
else {    
        outColor = applyFogAndShadowWithNormal(texColor, fogAmount, normal, 1, intensity, worldPos.xyz);
    }

#endif

    // SheyderMod: Specular highlight (opaque pass only; the _Oit variant carries no sun glint)
#if USEOIT == 0
    if (specularStrength > 0.0) {
    #if SHADOWQUALITY > 0
        float smSunB = clamp(getBrightnessFromShadowMap(), 0.0, 1.0);
    #else
        float smSunB = 1.0;
    #endif
        sm_applySpecular(outColor.rgb, uv, smAlbedo, normalize(normal), worldPos.xyz, smSunB, 0.0);
    }
#endif

    if (glitchFlicker >0 && glitchEffectStrength > 0) {
        float g = gnoise(vec3(gl_FragCoord.y / 2.0, gl_FragCoord.x / 2.0, windWaveCounter*30 + entityId * 3));
        outColor.a *= mix(1, clamp(0.7 + g / 2, 0, 1), glitchEffectStrength);
        
        float b = gnoise(vec3(0, 0, windWaveCounter*60 + entityId * 3));
        outColor.a *= mix(1, clamp(b * 10 + 2, 0, 1), glitchEffectStrength);
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
#endif

#if USEOIT==0 && SSAOLEVEL > 0
    outGPosition = vec4(fragPosition.xyz, fogAmount + glowLevel);
    outGNormal = vec4(gnormal.xyz, 0);
#endif

#ifdef DRT_FOG_TRANSPORT
    // Preserve OIT revealage; only completed radiance receives transport.
    outColor = drtApplySurfaceFog(outColor, worldPos.xyz, gl_FragCoord.z, true, voxSunLight);
#endif

#if NORMALVIEW > 0
    outColor = vec4((normal.x + 1) / 2, (normal.y + 1)/2, (normal.z+1)/2, 1);    
#endif

#if USEOIT > 0
    OIT(outColor, glowLevel+glow);
#else
    outGlow = vec4(glowLevel + glow, 0, 0, color.a);
#endif

#if defined(ALLOWDEPTHOFFSET) && ALLOWDEPTHOFFSET > 0
    gl_FragDepth = gl_FragCoord.z + depthOffset;
    
    #if USEOIT==0 && SSAOLEVEL > 0
        outGPosition.w=1;
    #endif
#endif
}
