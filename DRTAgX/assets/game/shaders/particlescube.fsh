#version 330 core
#extension GL_ARB_explicit_attrib_location: enable

in vec4 color;
in vec2 uv;
in float glowLevel;
in float fogAmount;
in vec4 rgbaFog;
in vec3 normal;
in vec4 worldPos;

layout(location = 0) out vec4 outColor;
layout(location = 1) out vec4 outGlow;
#if SSAOLEVEL > 0
in vec4 fragPosition;
in vec4 gnormal;
layout(location = 2) out vec4 outGNormal;
layout(location = 3) out vec4 outGPosition;
#endif

#define DRT_SURFACE_FOG_AFTER_LIGHTING
#include fogandlight.fsh
#include underwatereffects.fsh

void main()
{
	#if SHADOWQUALITY > 0
	float intensity = 0.34 + (1 - shadowIntensity)/8.0; // this was 0.45, which makes shadow acne visible on blocks
	#else
	float intensity = 0.45;
	#endif

	

#ifdef DRT_FOG_TRANSPORT
    outColor = applyFogAndShadowWithNormal(color, fogAmount, normal, 1.0, intensity, worldPos.xyz);
    outColor = drtApplySurfaceFog(outColor, worldPos.xyz, gl_FragCoord.z, true, sm_voxSunLight);
#else
    // Native includes are still in use during the earliest startup compile.
    float murkiness = getUnderwaterMurkiness();
    outColor = applyFogAndShadowWithNormal(color, murkiness > 0.0 ? 0.0 : fogAmount, normal, 1.0, intensity, worldPos.xyz);
    if (murkiness > 0.0) outColor.rgb = applyUnderwaterEffects(outColor.rgb, murkiness);
#endif

	outGlow = vec4(glowLevel, 0, 0, outColor.a);
	//outColor = vec4((normal.x + 1) / 2.0, (normal.y + 1) / 2.0, (normal.z + 1) / 2.0, 1);
	
#if SSAOLEVEL > 0
	outGPosition = vec4(fragPosition.xyz, fogAmount + glowLevel);
	outGNormal = vec4(gnormal.xyz, outColor.a);
#endif
}
