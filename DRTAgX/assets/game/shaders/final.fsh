// Final composition: native effects, SheyderMod composites, then DRTAgX grading.
#version 330 core

// =========================================================================
// INCLUDES
// =========================================================================
#include fxaa.fsh
#include colorutil.ash
#include noise3d.ash

// =========================================================================
// UNIFORMS
// =========================================================================
uniform sampler2D primaryScene;
uniform sampler2D glowParts;  
uniform sampler2D bloomParts; 
uniform sampler2D godrayParts;
uniform sampler2D ssaoScene;

uniform float gammaLevel;
uniform float brightnessLevel;
uniform float contrastLevel;
uniform float sepiaLevel;
uniform float extraGamma = 1.0;
uniform float ambientBloomLevel;

uniform float windWaveCounter;
uniform float glitchEffectStrength;
uniform float damageVignetting;
uniform float damageVignettingSide;
uniform float frostVignetting;

uniform int horizontalResolution;
uniform float minlight = 0.0;
uniform float maxlight = 1.0;
uniform float minsat = 0.0;
uniform float maxsat = 1.0;

// =========================================================================
// INPUTS & OUTPUTS
// =========================================================================
in vec2 invFrameSize;
in vec2 texCoord;
flat in float godrayIntensity;

layout(location = 0) out vec4 outColor;

// SheyderMod: LensFlare include (lf_* uniforms / lf_apply)
#include lensflare.fsh

// The old bloom path remains available if owned HDR passes cannot initialize.
#include bloom_composite.fsh

// SheyderMod: VolumetricFog composite include (vf_compositeVolumetric)
#include vfscatter_composite.fsh
#include drtagx_atmosphere_sampling.fsh

// =========================================================================
// CONSTANTS
// =========================================================================
const vec3 LUM_VEC = vec3(0.2126, 0.7152, 0.0722);
uniform sampler2D drtBloomTex;
uniform int drtBloomReady = 0;
uniform float drtBloomStrength = 1.0;

// DRTAgX grading code; the include is expanded before main().
#include DRTAgXKraken.fsh

// =========================================================================
// MAIN ENTRY POINT
// =========================================================================
void main(void) {
    vec4 Emissives = texture(glowParts, texCoord);

#if FXAA == 1
    vec4 color = fxaaTexturePixel(primaryScene, texCoord, invFrameSize);
#else
    vec4 color = texture(primaryScene, texCoord);
#endif  
    
    // Glow metadata still protects emissive pixels from SSAO darkening.
    float bloomSub = max(Emissives.r - 0.1, 0.0);

#if SSAOLEVEL > 0
    // OPTIMIZATION: Only fetch the first texture once, then branch.
    float ssao = texture(ssaoScene, texCoord).r;
    #if SSAOLEVEL > 1
        ssao = min(ssao, texture(ssaoScene, texCoord - vec2(0.0, invFrameSize.y)).r);
    #endif        
    color.rgb *= min(1.0, ssao + bloomSub);
#endif
    
#if GODRAYS > 0
    vec4 grc = texture(godrayParts, texCoord);
    //color.rgb += (pow(grc.rgb, vec3(2.0)))*1.3;
    color.rgb += grc.rgb;
    
#endif

	// >>> SheyderMod: VolumetricFog composite (half-res scatter, before ColorGrade)
	// Scatter already follows atmospheric halo radiance. The maintained Sheyder
	// helper adds it in HDR without a display clamp or extra luminance shaping.
	if (drtAtmosphereScreen.z < 0.5) {
		color.rgb = vf_compositeVolumetric(color.rgb, texCoord);
	}
	// <<< SheyderMod

    // Composite emissives / This now happens in bloom pass.
    // color.rgb += color.rgb * Emissives.r * 0.5;
    // color.rgb += color.rgb * Emissives.g * 4.0;

// Threshold-free HDR bloom is composited once, before exposure and AgX.
#if BLOOM == 1
    if (drtBloomReady != 0 && drtBloomStrength > 0.0) {
        float bloomGain = 0.3 * (1.0 + max(ambientBloomLevel * 4.0, 0.0));
        color.rgb += texture(drtBloomTex, texCoord).rgb * bloomGain * drtBloomStrength;
        // Fade the matching native bloom contrast adjustment with the glow.
        color.rgb *= 1.0 - ambientBloomLevel * 0.25 * min(drtBloomStrength, 1.0);
    }
#endif

	// >>> SheyderMod: LensFlare additive (before ColorGrade and vignetting)
	color.rgb += lf_additive(drtCelestialVisibility(drtAtmosphereSun.xyz));
	// <<< SheyderMod

    outColor = ColorGrade(color);

    // OPTIMIZATION: Pre-calculate screen center length to avoid running length() 3 different times
    vec2 position = (gl_FragCoord.xy * invFrameSize.xy) - 0.5;
    float posLength = length(position);
    float grayvignette = 1.0 - smoothstep(1.1, 0.3, posLength); 
    
    // OPTIMIZATION: Pre-calculate gl_FragCoord.xy / 20.0 to avoid repeating the division
    vec2 fc20 = gl_FragCoord.xy * 0.05;
    
    if (frostVignetting > 0.0) {
        float str = -0.05 + 1.05 * clamp(1.0 - smoothstep(1.1 - frostVignetting * 0.25, 0.3, posLength), 0.0, 1.0) - grayvignette;
        
        float wx = gnoise(vec3(fc20.x, str, gl_FragCoord.x * 0.0909 + gl_FragCoord.y * 0.1));
        float wy = gnoise(vec3(fc20.x, str, gl_FragCoord.x * 0.1 - gl_FragCoord.y * 0.1111));
        
        float g = 2.0 * gnoise(vec3(wx * 0.3333, wy * 0.3333, 0.2)) + 0.8;
        g *= gnoise(vec3(fc20.xy, 1.5)) + 0.2;
        g -= gnoise(vec3(wx * 2.0, wy * 2.0, 1.0)) * 0.2;
        g -= str * 2.0;
        g *= frostVignetting;
        
        float v = 0.9 + gnoise(vec3(wx, -wy, 0.0)) * 0.066667;
        vec3 vignetteColor = vec3(v, v, 0.95);
        
        outColor.rgb = mix(outColor.rgb, vignetteColor, max(0.0, str - g) + 0.5 * str);
    }
    
    if (damageVignetting > 0.0) {
        float str = clamp(1.0 - smoothstep(1.1 - damageVignetting * 0.25, 0.3, posLength), 0.0, 1.0) - grayvignette;
        
        float g = gnoise(vec3(fc20.xy, 0.0)) + 0.5;
        g += gnoise(vec3(gl_FragCoord.xy * 0.2, 0.0)) * 0.2;
        g -= str * 2.0;
        g *= damageVignetting;
        
        vec3 vignetteColor = vec3(0.4 * damageVignetting, 0.0, 0.0);
        float centerness = pow(1.0 - abs(damageVignettingSide), 3.0);
        float side = clamp(centerness + pow(mix(texCoord.x, 1.0 - texCoord.x, (1.0 + damageVignettingSide) * 0.5), 1.5), 0.0, 1.0);
        
        outColor.rgb = mix(outColor.rgb, vignetteColor, max(0.0, str - g) * side);
    }

    //outColor.rgb = Emissives.rgb;
    // We only need to assign alpha once at the very end
    outColor.a = 1.0;
}
