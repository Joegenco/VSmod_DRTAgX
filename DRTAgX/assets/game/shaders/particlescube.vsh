#version 330 core
#extension GL_ARB_explicit_attrib_location: enable

layout (location = 0) in vec3 vertexPosition;		// Per vertex
layout (location = 1) in vec4 normalv;				// Per vertex
// Location 2 preserves the native mesh binding; the distinct name allows the
// native fragment's `uv` input to have a matching vertex output on strict drivers.
layout (location = 2) in vec2 uvIn;					// Per vertex
layout (location = 3) in int renderFlags; 			// Per instance

layout (location = 4) in vec3 particlePosition; 	// Per instance (=per particle)
#if defined(VEC3SCALE)
layout (location = 5) in vec3 scale;					// Per instance
#else
layout (location = 5) in float scale;					// Per instance
#endif
layout (location = 6) in vec4 particleDir; 			// Per instance
layout (location = 7) in vec4 rgbaLightIn; 		// Per instance
layout (location = 8) in vec4 rgbaBlockIn; 		// Per instance

uniform vec4 rgbaFogIn;
uniform vec3 rgbaAmbientIn;	
uniform float fogMinIn;
uniform float fogDensityIn;
uniform mat4 projectionMatrix;
uniform mat4 modelViewMatrix;

out vec4 color;
out vec2 uv;
out vec4 rgbaFog;
out vec3 normal;
out float fogAmount;
out vec4 worldPos;
#if SSAOLEVEL > 0
out vec4 fragPosition;
out vec4 gnormal;
#endif

#include vertexflagbits.ash
#include shadowcoords.vsh
// Native particle samples receive the baked block-light visibility on the CPU.
// Do not add unshadowed placed-source radiance through surrounding walls.
#define DRT_DYNAMIC_NORMAL_VIEW (modelViewMatrix * vec4(normal, 0.0)).xyz
#include fogandlight.vsh
#include vertexwarp.vsh

#define M_PI 3.1415926535897932384626433832795

mat4 rotation3d(vec3 axis, float angle) {
  axis = normalize(axis);
  float s = sin(angle);
  float c = cos(angle);
  float oc = 1.0 - c;

  return mat4(
    oc * axis.x * axis.x + c,           oc * axis.x * axis.y - axis.z * s,  oc * axis.z * axis.x + axis.y * s,  0.0,
    oc * axis.x * axis.y + axis.z * s,  oc * axis.y * axis.y + c,           oc * axis.y * axis.z - axis.x * s,  0.0,
    oc * axis.z * axis.x - axis.y * s,  oc * axis.y * axis.z + axis.x * s,  oc * axis.z * axis.z + c,           0.0,
    0.0,                                0.0,                                0.0,                                1.0
  );
}

float atan2(in float y, in float x)
{
    bool s = (abs(x) > abs(y));
    return mix(M_PI/2.0 - atan(x,y), atan(y,x), s);
}


void main()
{
	uv = uvIn;
#if defined(VEC3SCALE)
	mat4 rotMat = rotation3d(vec3(0,1,0), atan2(particleDir.z, particleDir.x) + particleDir.w);
	worldPos = rotMat * (vec4(vertexPosition,1.0) * vec4(scale,1.0)) + vec4(particlePosition, 1.0);
	worldPos.w=1;		
	normal = mat3(rotMat) * normalv.xyz; // Cube face directions follow the instance rotation.
#else
	worldPos = vec4(vertexPosition * scale + particlePosition, 1.0);
	normal = normalv.xyz;
#endif

	worldPos = applyVertexWarping(renderFlags, worldPos);
	worldPos = applyGlobalWarping(worldPos);
	vec4 cameraPos = modelViewMatrix * worldPos;

	gl_Position = projectionMatrix * cameraPos;
	
	int flags = min(255, 2 * (renderFlags & 0xff)); // increase the glow on cube particles
	vec4 particleLight = applyLight(rgbaAmbientIn, rgbaLightIn, flags, cameraPos);
#ifdef DRT_EXPLICIT_SURFACE_LIGHTING
	// Block debris already carries the native atlas/tint material convention.
	// Match terrain's mapped material directly; a cube-only transfer curve
	// darkens these samples relative to the block they came from.
	color = vec4(max(rgbaBlockIn.rgb, vec3(0.0)), rgbaBlockIn.a);
#else
	color = particleLight * rgbaBlockIn;
#endif
	
	fogAmount = getFogLevel(vec4(particlePosition, 0), fogMinIn, fogDensityIn);
	rgbaFog = rgbaFogIn;
	
	calcShadowMapCoords(modelViewMatrix, worldPos);
	
#if SSAOLEVEL > 0
	
	fragPosition = cameraPos;
	// A normal is a direction (w=0); camera translation must not change AO.
	gnormal = vec4((modelViewMatrix * vec4(normal, 0.0)).xyz, 0.25);
#endif
}
