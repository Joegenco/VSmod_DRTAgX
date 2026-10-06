#version 330 core
// code will change the version to 430 if USESSBO > 0
#extension GL_ARB_explicit_attrib_location: enable

 #if USESSBO > 0
// rgb = block light, a=sun light level
layout(location = 0) in vec4 rgbaLightIn;
 #else
layout(location = 0) in vec3 xyz;
layout(location = 1) in vec2 uvIn;
layout(location = 2) in vec4 rgbaLightIn;
layout(location = 3) in int renderFlagsIn;
layout(location = 4) in int colormapData;
 #endif



uniform vec4 rgbaFogIn;
uniform vec3 rgbaAmbientIn;
uniform float fogDensityIn;
uniform float fogMinIn;
uniform vec3 origin;
uniform mat4 projectionMatrix;
uniform mat4 modelViewMatrix;
uniform float forcedTransparency;
uniform float subpixelPaddingX;
uniform float subpixelPaddingY;

out vec4 rgba;
out vec4 rgbaFog;
out float fogAmount;
out vec2 uv;
out vec4 worldPos;
// Cached PLS shadows follow the material while the visible geometry sways.
out vec3 drtPlacedRestPos;
// Preserve wind classification when a transparent triangle has fixed roots.
out float drtWindPresence;
out vec3 vertexPos;

flat out int renderFlags;
flat out vec3 normal;
out float normalShadeIntensity;

#include vertexflagbits.ash
#include shadowcoords.vsh
#define DRT_DYNAMIC_NORMAL_VIEW (modelViewMatrix * vec4(normal, 0.0)).xyz
#include fogandlight.vsh
#include vertexwarp.vsh
#include colormap.vsh

// Magnitude bits 14..16, 18..20 and 22..24 distinguish a valid packed normal.
const int DRT_NORMAL_MAG_BITS = (0x7 << 14) | (0x7 << 18) | (0x7 << 22);

 #if USESSBO > 0
layout(binding = 3, std430) readonly buffer faceDataBuf  { FaceData faces[]; };
 #endif


void main(void)
{
 #if USESSBO > 0
	FaceData vdata = faces[gl_VertexID / 4];
	int vIndex = gl_VertexID & 0x03;
	renderFlags = vdata.flags[vIndex];
	vec3 xyz = vdata.xyz + ((vIndex + 1) & 2) * vdata.xyzA + (vIndex & 2) * vdata.xyzB;
 #else
	renderFlags = renderFlagsIn;
	
 #endif

	vertexPos = xyz;
	drtWindPresence = (renderFlags & WindModeBitMask) != 0 ? 1.0 : 0.0;
	
	vec4 truePos = vec4(xyz + origin, 1.0);
	drtPlacedRestPos = applyGlobalWarping(truePos).xyz;
	worldPos = truePos;
	
	worldPos = applyVertexWarping(renderFlags, worldPos);
	worldPos = applyGlobalWarping(worldPos);

	vec4 cameraPos = modelViewMatrix * worldPos;
	normal = (renderFlags & DRT_NORMAL_MAG_BITS) != 0 ? unpackNormal(renderFlags) : vec3(0.0);
	
	gl_Position = projectionMatrix * cameraPos;

	calcShadowMapCoords(modelViewMatrix, worldPos);

 #if USESSBO > 0
	calcColorMapUvs(vdata.colormapData, truePos + vec4(playerpos, 1), rgbaLightIn.a, false);
	uv = UnpackUv(vdata, vIndex, subpixelPaddingX, subpixelPaddingY);
 #else
	calcColorMapUvs(colormapData, truePos + vec4(playerpos, 1), rgbaLightIn.a, false);
	uv = uvIn;
 #endif

	rgba = applyLight(rgbaAmbientIn, rgbaLightIn, renderFlags, cameraPos);
	// Evaluate atmospheric metadata after sunlight publication; glass in caves stays clear.
	fogAmount = getFogLevel(worldPos, fogMinIn, fogDensityIn);
	rgba.a = clamp(20 * (1.10 - length(worldPos.xz) / viewDistance) - 5 + max(0.0, worldPos.y * 0.02), -1.0, 1.0) - forcedTransparency;
#ifdef DRT_FOG_TRANSPORT
    // Retain intentional transparency; distance concealment uses shared RGB fog.
    if (drtFogColor.w > 0.5) rgba.a = 1.0 - forcedTransparency;
#endif
	
	rgbaFog = rgbaFogIn;
	
	// To fix Z-Fighting on blocks over certain other blocks. 
	if (gl_Position.z > 0) {
		int zOffset = (renderFlags & ZOffsetBitMask) >> 8;
		gl_Position.w += zOffset * 0.00025 / max(3, gl_Position.z * 0.05);
	}
	
	// The geometric normal was decoded before applyLight for directional light.
	normalShadeIntensity = 1.0; // Shared sun-access gate is applied in the fragment stage.
}
