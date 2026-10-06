#version 330 core
// code will change the version to 430 if USESSBO > 0
#extension GL_ARB_explicit_attrib_location: enable

// SheyderMod: vendored engine chunkopaque.vsh (terrain vertex stage) with mod hooks:
// the SubSurface wind classification and a guarded normal unpack. Forward
// fallback pixels retain per-vertex shadow coordinates in every variant. Re-sync against the
// engine copy on updates.

 #if USESSBO > 0
// rgb = block light, a=sun light level
layout(location = 0) in vec4 rgbaLightIn;
 #else
layout(location = 0) in vec3 xyz;
layout(location = 1) in vec2 uvIn;
layout(location = 2) in vec4 rgbaLightIn;
layout(location = 3) in int renderFlagsIn;   // Check out vertexflagbits.ash for understanding the contents of this data
layout(location = 4) in int colormapData;
 #endif

uniform vec4 rgbaFogIn;
uniform vec3 rgbaAmbientIn;
uniform float fogDensityIn;
uniform float fogMinIn;
uniform vec3 origin;
uniform mat4 projectionMatrix;
uniform mat4 modelViewMatrix;
uniform float cameraUnderwater;
uniform int deferredMode;
uniform int drtForwardDecalPass = 0;
uniform int haxyFade;

uniform float shadowIntensity = 1;
uniform vec3 lightPosition;
uniform float subpixelPaddingX;
uniform float subpixelPaddingY;

// SheyderMod: Deferred performance-mode gate.
#ifndef SHEYDER_DEFERRED
#define SHEYDER_DEFERRED 0
#endif
#ifndef NORMALVIEW
#define NORMALVIEW 0
#endif

out vec4 rgba;
out vec2 uv;
out vec4 rgbaFog;
out float fogAmount;
out vec3 normal;
out vec3 vertexPosition;
out vec4 worldPos;
out vec4 camPos;
out float lod0Fade;
out float nb;
out float voxSunLight;

 #if SSAOLEVEL > 0
out vec4 gnormal;
 #endif

flat out int renderFlags;
// WindMode occupies bits 25..28. Interpolate its presence so a grass triangle
// remains foliage when its provoking vertex is a fixed, zero-wind root.
out float drtWindPresence;
// Interpolate the material's rest pose independently of its visible wind bend.
out vec3 drtPlacedRestPos;

#include vertexflagbits.ash
// SheyderMod: SubSurface vertex include (tl_isWind / tl_computeWind)
#include subsurface.vsh
#include shadowcoords.vsh
#define DRT_DYNAMIC_NORMAL_VIEW (modelViewMatrix * vec4(normal, 0.0)).xyz
#define DRT_DEFERRED_TERRAIN 1
#include fogandlight.vsh
#include vertexwarp.vsh
#include colormap.vsh

 #if USESSBO > 0
layout(binding = 3, std430) readonly buffer faceDataBuf  { FaceData faces[]; };
 #endif

// SheyderMod: guarded normal unpack. The engine's unpackNormal() ends in normalize(), so a vertex
// carrying no normal magnitude bits decodes to normalize(vec3(0)) -- a NaN. GLSL leaves NaN
// undefined for min/max and for the relational operators, so past that point every consumer is at
// the driver's mercy: the shading, the SSAO/GTAO G-buffer, Specular, Overexposure. Vanilla survives
// only because each of its uses sits in a max() that happens to drop the NaN, and guarding the
// consumers one by one does NOT work -- such a guard needs the very comparisons that are undefined.
//
// Those vertices are real: mods that inject geometry into the tesselator's mesh pools emit them
// (vsecomachina's foliage billboards fill xyz/uv/rgba/indices and leave Flags at zero). The zero
// vector replaces the undefined value with a defined "no orientation" that every consumer handles on
// ordinary finite comparisons -- GTAO's length test rejects it, Specular's NdotL gate returns,
// ox_prepare lands on its sky-lit floor, nb settles on a flat 0.5. Consumers rely on that; keep it.
//
// The mask is the three magnitude fields unpackNormal() reads (bits 14..16, 18..20, 22..24). Sign
// bits 13/17/21 are excluded on purpose, which is what makes NormalBitMask too weak a test here.
// Duplicated in chunktopsoil.vsh rather than shared: vendored files are re-synced one at a time, so
// the guard belongs in the file being re-synced.
const int SM_NORMAL_MAG_BITS = (0x7 << 14) | (0x7 << 18) | (0x7 << 22);

vec3 sm_unpackNormalOrZero(int flags)
{
	return (flags & SM_NORMAL_MAG_BITS) != 0 ? unpackNormal(flags) : vec3(0.0);
}


void main(void)
{
    voxSunLight = drtSunAccess(rgbaLightIn.a);

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
	drtPlacedRestPos = applyGlobalWarping(truePos).xyz;
	bool isLeaves = ((renderFlags & WindModeBitMask) > 0);
	int windMode = renderFlags & WindModeBitMask;
	drtWindPresence = windMode != 0 ? 1.0 : 0.0;

	// SheyderMod: SubSurface wind classification
	tl_computeWind(windMode);

	worldPos = applyVertexWarping(renderFlags, truePos);
	worldPos = applyGlobalWarping(worldPos);
	
	camPos = modelViewMatrix * worldPos;
	// Dynamic point lights need the actual face normal before applyLight runs.
	normal = sm_unpackNormalOrZero(renderFlags);

	gl_Position = projectionMatrix * camPos;

	// Blended and reflective pixels may need the forward fallback even in the
	// deferred variant; its sunlight-shadow coordinates must remain valid.
	calcShadowMapCoords(modelViewMatrix, worldPos);
 #if USESSBO > 0
	calcColorMapUvs(vdata.colormapData, truePos + vec4(playerpos, 1.0), rgbaLightIn.a, isLeaves);
	uv = UnpackUv(vdata, vIndex, subpixelPaddingX, subpixelPaddingY);
 #else
	calcColorMapUvs(colormapData, truePos + vec4(playerpos, 1.0), rgbaLightIn.a, isLeaves);
	uv = uvIn;
 #endif

	rgba = applyLight(rgbaAmbientIn, rgbaLightIn, renderFlags, camPos);
	// Lighting publishes native sunlight access before fog/AO metadata is evaluated.
	fogAmount = getFogLevel(worldPos, fogMinIn, fogDensityIn);
	
	// Distance fade out
	rgba.a = clamp(17.0 - 20.0 * length(worldPos.xz) / viewDistance + max(0.0, worldPos.y * 0.02), -1.0, 1.0);
#ifdef DRT_FOG_TRANSPORT
    // Shared boundary fog replaces only this radial fade. LOD alpha-test
    // and material cutouts remain native; startup retains the expression above.
    if (drtFogColor.w > 0.5) rgba.a = 1.0;
#endif
	
	rgbaFog = rgbaFogIn;
	
	// SheyderMod: guarded unpack, never a NaN (see sm_unpackNormalOrZero)

#if SSAOLEVEL > 0
	gnormal = modelViewMatrix * vec4(normal.xyz, 0);
	gnormal.w = isLeaves ? 1 : 0; // Cheap hax to make SSAO on leaves less bad looking;
#endif


	// To fix Z-Fighting on blocks over certain other blocks
	if (gl_Position.z > -1) {
		int zOffset = (renderFlags & ZOffsetBitMask) >> 8;
		gl_Position.w += zOffset * 0.00025 / ((gl_Position.z + 3) * 0.05);   // The offset helps decors not to become invisible nearby
	}
	


	//  11.3.24: For performance, we now pre-calculate the LOD0 fade alpha value in the vertex shader, and pass it to the fragment shader; this reduces conditionality in the fragment shader

	// Lod 0 fade
	// This makes the lod fade more noticable, actually O_O
	if ((renderFlags & Lod0BitMask) != 0) {
		
		// We made this transition smoother, because it looks better,
		// if you notice chunk popping, revert to the old, harsher transition
		// Radfast and Tyron, May 28 2021 ^_^
		float b = clamp(10 * (1.05 - length(worldPos.xz) / viewDistanceLod0) - 2.5, 0.0, 1.0);
		//float b = clamp(20 * (1.05 - length(worldPos.xz) / viewDistanceLod0) - 5, 0.0, 1.0);
				
		lod0Fade = 1 - b;
	}
	else    lod0Fade = 0.0;
	

	//  14.3.24: We can also pre-calculate nb

#if SHADOWQUALITY > 0
	float intensity = 0.34 + (1 - shadowIntensity)/8.0; // this was 0.45, which makes shadow acne visible on blocks
#else
	float intensity = 0.45;
#endif
	float facing = dot(normal, lightPosition);
	nb = max(max(intensity, 0.5 + 0.5 * facing), normal.y * 0.95);
#if SHADOWQUALITY > 0
    // Opaque forward terrain computes face shade per vertex. Use the same
    // CSM-matched grazing term as the deferred/fragment sunlight path.
    nb = max(intensity, drtSunFaceBrightness(nb, facing,
        shadowIntensity * DRT_SUN_SHADOW_GAIN));
#endif
	nb = clamp(1.0 - (1.0 - nb) * 1.0, 0.0, 1.0);
}
