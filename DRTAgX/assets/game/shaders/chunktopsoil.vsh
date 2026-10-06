#version 330 core
// code will change the version to 430 if USESSBO > 0
#extension GL_ARB_explicit_attrib_location: enable

// SheyderMod: vendored engine chunktopsoil.vsh (terrain vertex stage) with mod hooks:
// a guarded normal unpack, and a compile-time skip of the per-vertex shadow coords in the
// deferred performance-mode variant. Re-sync against the engine copy on updates.

 #if USESSBO > 0
// rgb = block light, a=sun light level
layout(location = 0) in vec4 rgbaLightIn;
layout(location = 1) in vec2 uv2In;
 #else
layout(location = 0) in vec3 xyz;
layout(location = 1) in vec2 uvIn;
layout(location = 2) in vec4 rgbaLightIn;
layout(location = 3) in int renderFlagsIn;   // Check out vertexflagbits.ash for understanding the contents of this data
layout(location = 4) in vec2 uv2In;
layout(location = 5) in int colormapData;
 #endif


uniform vec4 rgbaFogIn;
uniform vec3 rgbaAmbientIn;
uniform float fogDensityIn;
uniform float fogMinIn;
uniform vec3 origin;
uniform mat4 projectionMatrix;
uniform mat4 modelViewMatrix;
uniform int deferredMode;
uniform float subpixelPaddingX;
uniform float subpixelPaddingY;

// SheyderMod: Deferred performance-mode gate (skips per-vertex shadow coords; relit per pixel).
#ifndef SHEYDER_DEFERRED
#define SHEYDER_DEFERRED 0
#endif
#ifndef NORMALVIEW
#define NORMALVIEW 0
#endif


out vec4 rgba;
out vec4 rgbaFog;
out float fogAmount;
out vec2 uv;
out vec2 uv2;
out vec3 normal;
out float drtVoxSunLight;

 #if SSAOLEVEL > 0
out vec4 fragPosition;
out vec4 gnormal;
 #endif

out vec3 vertexPosition;
out vec4 worldPos;

flat out int renderFlags;


#include vertexflagbits.ash
#include shadowcoords.vsh
#define DRT_DYNAMIC_NORMAL_VIEW (modelViewMatrix * vec4(normal, 0.0)).xyz
#define DRT_DEFERRED_TERRAIN 1
#include fogandlight.vsh
#include vertexwarp.vsh
#include colormap.vsh

 #if USESSBO > 0
layout(binding = 3, std430) readonly buffer faceDataBuf  { FaceData faces[]; };
 #endif

const float uvEpsilon = 1.0 / 32768.0;

// SheyderMod: guarded normal unpack, verbatim from chunkopaque.vsh -- full rationale there. Short
// version: unpackNormal() ends in normalize(), so a vertex carrying no normal magnitude bits would
// decode to normalize(vec3(0)) -- a NaN, undefined from there on in every consumer. The zero vector
// is the defined "no orientation" they all handle instead; consumers rely on it, keep it.
const int SM_NORMAL_MAG_BITS = (0x7 << 14) | (0x7 << 18) | (0x7 << 22);

vec3 sm_unpackNormalOrZero(int flags)
{
	return (flags & SM_NORMAL_MAG_BITS) != 0 ? unpackNormal(flags) : vec3(0.0);
}

void main(void)
{
	// Normalize sky access using the same live bounds as opaque terrain for deferred relighting.
	drtVoxSunLight = drtSunAccess(rgbaLightIn.a);
 #if USESSBO > 0
	FaceData vdata = faces[gl_VertexID / 4];
	int vIndex = gl_VertexID & 0x03;
	renderFlags = vdata.flags[vIndex];
	vertexPosition = vdata.xyz + ((vIndex + 1) & 2) * vdata.xyzA + (vIndex & 2) * vdata.xyzB;
 #else
	renderFlags = renderFlagsIn;
	vertexPosition = xyz;
 #endif

	vec4 truePos = vec4(vertexPosition + origin, 1.0);
	worldPos = truePos;
	//worldPos = applyVertexWarping(renderFlags, worldPos);
	worldPos = applyGlobalWarping(worldPos);

	vec4 cameraPos = modelViewMatrix * worldPos;
	// Decode the geometric normal before shared point-light evaluation.
	normal = sm_unpackNormalOrZero(renderFlags);

	gl_Position = projectionMatrix * cameraPos;

	// SheyderMod: skipped in the deferred performance-mode variant (done per pixel in the relight)
#if !(SHEYDER_DEFERRED > 0 && SSAOLEVEL > 0 && NORMALVIEW == 0)
	calcShadowMapCoords(modelViewMatrix, worldPos);
#endif

 #if USESSBO > 0
	calcColorMapUvs(vdata.colormapData, truePos + vec4(playerpos, 1), rgbaLightIn.a, false);
	uv = UnpackUv(vdata, vIndex, subpixelPaddingX, subpixelPaddingY);
 #else
	calcColorMapUvs(colormapData, truePos + vec4(playerpos, 1), rgbaLightIn.a, false);
	uv = uvIn;
 #endif
	uv2 = uv2In * 2.0 - vec2((int(uv2In.x * 0x10000) & 1) * (uvEpsilon + subpixelPaddingX * 2.0), (int(uv2In.y * 0x10000) & 1) * (uvEpsilon + subpixelPaddingY * 2.0));  // uv2In least significant bit is a flag which tells whether this coordinate is (for .x) u1 or u2, or (for .y) v1 or v2

	rgba = applyLight(rgbaAmbientIn, rgbaLightIn, renderFlags, cameraPos);
	// Sheltered topsoil must publish the same clear fog metadata as opaque terrain.
	fogAmount = getFogLevel(worldPos, fogMinIn, fogDensityIn);
	rgbaFog = rgbaFogIn;

	rgbaFog.a = clamp(20 * (1.10 - length(worldPos.xz) / viewDistance) - 5 + max(0.0, worldPos.y * 0.02), 0.0, 1.0);
#ifdef DRT_FOG_TRANSPORT
    // Match continuous opaque boundary fog instead of a height-biased alpha fade.
    if (drtFogColor.w > 0.5) rgbaFog.a = 1.0;
#endif

	// Normal was decoded before applyLight so topsoil receives directional light.

#if SSAOLEVEL > 0
	fragPosition = cameraPos;
	gnormal = modelViewMatrix * vec4(normal, 0);
	gnormal.w=0;
#endif
}
