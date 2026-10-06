#version 330 core
#extension GL_ARB_shader_storage_buffer_object : require
#extension GL_ARB_shading_language_420pack : require
#extension GL_ARB_explicit_attrib_location: enable

uniform sampler2D terrainTex;

in vec4 rgba;
in vec4 rgbaFog;
in float fogAmount;
in vec2 uv;
in float glowLevel;
in vec4 worldPos;
in vec3 drtPlacedRestPos;
in float drtWindPresence;
in vec3 blockLight;
in vec3 vertexPos;

in float normalShadeIntensity;
flat in int renderFlags;
flat in vec3 normal;

#include vertexflagbits.ash
// RGB is unlit material color until the shared sun/local compositor runs.
#define DRT_ALBEDO_SURFACE_LIGHTING
#define DRT_SURFACE_FOG_AFTER_LIGHTING
#define DRT_TERRAIN_PLACED_OCCLUSION
#include fogandlight.fsh
#include noise3d.ash
#include colormap.fsh
#include underwatereffects.fsh
#include drtagx_terrain_boundary.fsh
#include drtagx_terrain_placed_occlusion.fsh
#include oit.fsh

void main() 
{
    // Drop only fully hidden terrain; retain material opacity and native OIT.
    drtCaptureTerrainPlacedPlane(drtPlacedRestPos);
    drtDiscardTerrainBoundary(worldPos.xyz);
    drtPrepareTerrainPlacedVisibility(drtPlacedRestPos, drtWindPresence > 0.0, glowLevel);
	// When looking through tinted glass you can clearly see the edges where we fade to sky color
	// Using this discard seems to completely fix that
	if (rgba.a < 0.005) discard;

	vec4 texColor = getColorMapped(terrainTex, texture(terrainTex, uv));
	// Preserve native vertex fade/forced transparency independently of radiance.
	// Do not decode or multiply light metadata as if it were material color.
	texColor.a *= rgba.a;

	if (psychedelicStrength > Epsilon) texColor = applyPsychedelicEffect(texColor, vertexPos.xyz, 0);

    float murkiness = getUnderwaterMurkiness();
#ifdef DRT_FOG_TRANSPORT
    texColor = applyFogAndShadowWithNormal(texColor, fogAmount, normal, normalShadeIntensity, 0.45, worldPos.xyz);
#else
	// Use the murkiness declared above for the native startup fallback.
	if (murkiness > 0) {
		texColor = applyFogAndShadowWithNormal(texColor, 0, normal, normalShadeIntensity, 0.45, worldPos.xyz);
		texColor.rgb = applyUnderwaterEffects(texColor.rgb, murkiness);	
	}
else {	
		texColor = applyFogAndShadowWithNormal(texColor, fogAmount, normal, normalShadeIntensity, 0.45, worldPos.xyz);
	}	
	

#endif

#if SHINYEFFECT > 0
	float glow=0;
	texColor = mix(applyReflectiveEffect(texColor, glow, renderFlags, uv, normal, worldPos, worldPos, blockLight), texColor, min(1, 2 * fogAmount));
#endif	

#ifdef DRT_FOG_TRANSPORT
    // Preserve OIT revealage; only completed radiance receives transport.
    texColor = drtApplySurfaceFog(texColor, worldPos.xyz, gl_FragCoord.z, true, sm_voxSunLight);
#endif

    OIT(texColor, glowLevel);

}
