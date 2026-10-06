#version 330 core

in vec2 uv;
in vec4 color;
in vec4 rgbaFog;
in float glowLevel;
in vec3 vertexPosition;

layout(location = 0) out vec4 outColor;
layout(location = 1) out vec4 outGlow;
#if SSAOLEVEL > 0
layout(location = 2) out vec4 outGNormal;
layout(location = 3) out vec4 outGPosition;
#endif


uniform sampler2D tex;
uniform float extraGodray = 0;
uniform float alphaTest = 0.001;
uniform float fogDensityIn;
uniform float fogMinIn;
uniform float horizonFog;
uniform vec3 sunPosition;
uniform vec3 moonPosition;
uniform float moonSunAngle;
uniform int weirdMathToMakeMoonLookNicer;
uniform float dayLight;

#include dither.fsh
#include fogandlight.fsh
#include skycolor.fsh
#include underwatereffects.fsh
#include drtagx_celestial_balance.ash

void main () {
    vec4 texColor = applyFog(texture(tex, uv) * color, 0);

    if (texColor.a < alphaTest) discard;

    // Retain the existing lunar texture shaping; sprite gains follow phase.
    bool isMoon = weirdMathToMakeMoonLookNicer > 0;
    texColor.rgb *= isMoon ? 0.5 : 1.0;

    // The lunar phase model has a zero-clamped terminator and must never
    // modulate the self-emitting sun. A zero cannot be rescued by a later gain.
    if (isMoon) {
        // Native phase orientation rounds the angle to zero or pi.
        float msa = int(moonSunAngle * 0.31830989 + 0.5) * 3.1415927;
        float dotp = dot(sunPosition, moonPosition);
        vec3 dirsun = sunPosition - moonPosition * dotp;
        vec2 xyi = (ivec2(uv * 32) - 16) / 32.0;
        float fact = (clamp((xyi.x * cos(msa) - xyi.y * sin(msa) * sign(dirsun.y)), -1, 1) + 1);
        // Retain the native alpha-limited moon phase, including its dark side.
        float celestialLight = 7.1 - fact * 6.2 - dotp * 1.7;
        texColor.rgb *= clamp(celestialLight, 0.0, texColor.a + 0.05);
        // Shape intrinsic lunar radiance before weather; otherwise the nonlinear
        // curve raises atmospheric transmittance to 2.4 and crushes the horizon moon.
        float b = pow(max((texColor.r + texColor.g + texColor.b) / 2.8, 0.0), 1.4);
        texColor.rgb *= b * DRT_MOON_GAIN * DRT_MOON_SPRITE_GAIN * drtMoonDayGain(drtAtmosphereSun.y);
    } else {
        texColor.rgb *= DRT_SUN_SPRITE_GAIN;
    }

    vec4 texGlow = vec4(glowLevel, extraGodray, 0, texColor.a);
    
    vec4 skyColor = vec4(1);
    vec4 skyGlow = vec4(1);
    float sealevelOffsetFactor = 0.06;
    
    getSkyColorAt(vertexPosition, sunPosition, sealevelOffsetFactor, clamp(dayLight, 0, 1), horizonFog, skyColor, skyGlow);
    
    vec3 skyPosNorm = normalize(vertexPosition.xyz);
    DrtFogTransport skyTransport = drtSkyMediumTransport(skyPosNorm);
    float fogAmount = 1.0 - max(max(skyTransport.transmittance.r, skyTransport.transmittance.g), skyTransport.transmittance.b);
    // Fog attenuates sprite radiance; the sky supplies the background independently.
    vec3 spriteContribution = texColor.rgb * skyTransport.transmittance;
    if (drtAtmosphereFlags.x > 0.5) {
        // Dry sprites must write the same exposed/tinted weather background as
        // the sky. Native unscaled fog scatter caused a dark rectangular border.
        bool waterRay = drtAtmosphereView.z > 0.7 || cameraUnderwater > 0.7
            || drtFilteredLiquidDepth() < 0.999999;
        skyColor.rgb = waterRay
            ? drtApplyTransport(vec4(drtClearSkyRadiance(skyPosNorm), 1.0), skyTransport).rgb
            : drtSkyBackground(skyPosNorm);
    }

    outColor = texColor;
    outGlow = texGlow;
    outGlow.rg *= 1.0 - fogAmount;
    outColor.a = texColor.a;


    if (isMoon) {
        // Keep bloom/extraction as faint as the daytime disk itself.
        outGlow.rg *= DRT_MOON_GAIN * drtMoonDayGain(drtAtmosphereSun.y);
    } else {
        outGlow = max(outGlow, skyGlow);
    }

    // Add the sprite over the sky so neither texture gain can be masked by max().
    outColor.rgb = skyColor.rgb + spriteContribution;

    // Sprite and extraction share weather visibility; sky scatter is already
    // accounted for in skyColor. Never fog the sprite a second time.
    if (drtAtmosphereView.z > 0.7) outGlow.rg = vec2(0.0);

#if SSAOLEVEL > 0
    // Celestial sprites belong to the sky, not nearby occluding geometry.
    // Use the native sky's zero sentinel instead of unwritten vertex varyings;
    // SSAO must not darken the disk as fog removes its emissive protection.
    outGPosition = vec4(0.0);
    outGNormal = vec4(0.0);
#endif

}
