#version 330 core
#extension GL_ARB_explicit_attrib_location: enable

// Clouds fade four times farther out than terrain; physical weather fog stays native.
#define DRT_BOUNDARY_DISTANCE_SCALE 4.0

in vec4 rgbaCloud;
in vec4 rgbaFog;
in vec3 plightrgb;
in float fogAmountf;
in float nightVisionStrengthv;

in vec3 vertexPos;
in vec3 drtCloudWorldPos;
flat in int flagsf;
in float thinCloudModef;

uniform float fogDensityIn;
uniform float fogMinIn;
uniform vec3 sunPosition;


#include noise3d.ash
#include dither.fsh
#include fogandlight.fsh
#include skycolor.fsh
#include underwatereffects.fsh
#include oit.fsh
#include drtagx_cloud_lighting.fsh

float halfsmooth(float x, float t){
    return x > t ? (x - t / 2.0) : (x * x * x * (1.0 - x * 0.5 / t) / t / t);
}

void main()
{
	float sealevelOffsetFactor = 0.25;
	float dayLight = 1;
	float horizonFog = 0;
	// Due to earth curvature the clouds are actually lower, so we do +100 to not have them dismissed during sunglow coloring
	vec4 skyGlow = getSkyGlowAt(vec3(vertexPos.x, vertexPos.y+100, vertexPos.z), sunPosition, sealevelOffsetFactor, clamp(dayLight, 0, 1), horizonFog, 0.7);
	
	vec4 col = rgbaCloud;
	
	col.a = (col.a)/(col.a +0.5)*1.5;
	
	vec3 ray = normalize(drtCloudWorldPos - drtAtmosphereCamera.xyz);
	vec3 normal = cross(dFdx(drtCloudWorldPos), dFdy(drtCloudWorldPos));
	normal *= inversesqrt(max(dot(normal, normal), 0.000001));
	if (dot(normal, ray) > 0.0) normal = -normal;
	col.rgb *= drtCloudLight() * drtCloudDirection(normal, ray);
	
	float baseBloom = max(0.0, 0.25 - fogAmountf/2);
	#if BLOOM == 1
		col.rgb *= 1 - baseBloom;
	#endif
	
	if (psychedelicStrength > Epsilon) col = applyPsychedelicEffect(col, vertexPos.xyz, 1);
	
	col.rgb += plightrgb;

	col.rgb += vec3(0.1, 0.5, 0.1) * nightVisionStrengthv;

	

	// Seems to give a ~8 FPS boost on an intel hd 620 when looking at the sky at 128 view distance
	if (col.a < 0.005) discard;
	
    // Use physical geometry, never the smoothed OIT pseudo-depth. Shared boundary
    // extinction fades cloud coverage at four times the terrain horizon distance.
    col = drtCloudThroughFog(col, drtSurfaceMediumTransport(drtCloudWorldPos, gl_FragCoord.z, true));
    if (drtFogColor.w > 0.5) col = applySpheresFog(col, 0.0, drtCloudWorldPos - drtAtmosphereCamera.xyz);

    // fake depth for better blending
    float faux = halfsmooth((gl_FragCoord.z * 2.0 - 1.0) / gl_FragCoord.w, 500.0);

    OIT(col, 0.0, faux);

    // Fog visibility attenuates bloom coverage too; solar gain prevents a persistent night bloom source.
    outGlow = vec4(max(0, skyGlow.a/10 + baseBloom) * drtCloudSunlight(), 0, 0, min(1, col.a*5 - (flagsf >= 5 ? thinCloudModef : 0)));

}
