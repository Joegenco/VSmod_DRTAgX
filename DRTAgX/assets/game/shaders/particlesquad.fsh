#version 330 core
#extension GL_ARB_explicit_attrib_location: enable

in vec4 color;
in vec2 uv;
in vec4 rgbaFog;
in vec3 vexPos;
in vec3 drtParticleWorldPos;
in float fogAmount;
in float glowLevel;
in float extraWeight;



uniform sampler2D particleTex;

#include fogandlight.fsh
#include underwatereffects.fsh
#include oit.fsh

void main()
{
	vec4 outColor;
	
#ifdef DRT_FOG_TRANSPORT
    // Billboard receiving plane: view Z rotated into world coordinates.
    // Grid the existing sun comparison without adding particle shadow work.
    drtPrepareSunGrid(drtParticleWorldPos, drtAtmosphereInvView[2].xyz);
    float sun = mix(1.0, getBrightnessFromShadowMap(), sqrt(clamp(drtSurfaceSunAccess, 0.0, 1.0)));
    outColor = vec4(drtShadeSurface(color.rgb, sun, vec3(1.0)), color.a);
    outColor = drtApplySurfaceFog(outColor, drtParticleWorldPos, gl_FragCoord.z, true, sm_voxSunLight);
#else
    outColor = applyFogAndShadow(color, fogAmount);
#endif

	vec2 uvdist = vec2(
		max(max(0.0, 0.1 - uv.x), max(0.0, uv.x - 0.9)),
		max(max(0.0, 0.1 - uv.y), max(0.0, uv.y - 0.9))
	);
	
	outColor.a *= 1 - length(uvdist)*10;	

    OIT(clamp(outColor, vec4(0.0), vec4(10.0, 10.0, 10.0 ,1.0)), glowLevel);

}

