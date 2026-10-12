#version 330 core
// code will change the version to 430 if USESSBO > 0
#extension GL_ARB_explicit_attrib_location: enable
#extension GL_ARB_shading_language_420pack : require

// ============================================================================
// DRTAgX / Vintage Story: Chunk Shadow Map Vertex Shader
// ----------------------------------------------------------------------------
// Renders terrain shadow maps; placed lights can opt into every terrain pass.
// Supports both conventional VBO vertex attribute stream and packed SSBO quads
// (FaceData layout, 4 vertices per face).
// Computes drtShadowLocalPos for emitter exclusion and cubemap projection.
// ============================================================================

// Liquid pools use the native unpacked stream even when solid terrain uses
// packed SSBO quads. These attributes are read only for the unpacked route.
layout(location = 0) in vec3 xyz;
layout(location = 1) in vec2 uvIn;
layout(location = 2) in vec4 rgbaLightIn;
layout(location = 3) in int renderFlagsIn;
layout(location = 6) in int waterFlagsIn;

uniform vec3 origin;
uniform mat4 mvpMatrix;
uniform int drtShadowTerrainPass = -1;
// Only cached placed maps set emitter exclusion. Their wind geometry and
// receivers share a rest pose; native sun/moving maps retain live deformation.
uniform int drtExcludeEmitter;
// Native and placed maps default to disabled. Both shadow drawing and deferred
// sampling use the same GPU-written held projection at UBO binding 5.
uniform int drtHeldProjectionIndex = -1;
uniform mat4 drtHeldCameraMatrix;
layout(std140, binding = 5) uniform DrtHeldProjections {
    mat4 drtHeldMatrices[2];
    vec4 drtHeldStatus[2];
};
uniform float subpixelPaddingX;
uniform float subpixelPaddingY;

#ifdef DRT_POINT_SHADOWS
// Only the owned point-shadow program adds a geometry gate. Native sun draws
// keep their original two-stage interface and do not pay geometry-stage cost.
out vec2 drtPointUv;
out vec3 drtPointLocalPos;
flat out int drtPointWindMode;
flat out int drtPointWindCaster;
#else
out vec2 uv;
out vec3 drtShadowLocalPos;
flat out int drtShadowWindMode;
#endif

#include vertexflagbits.ash
#include vertexwarp.vsh

 #if USESSBO > 0
layout(binding = 3, std430) readonly buffer faceDataBuf  { FaceData faces[]; };
 #endif


void main(void)
{
    bool liquidPass = drtShadowTerrainPass == 4;
    vec3 position = xyz;
    int flags = renderFlagsIn;
 #if USESSBO > 0
	FaceData vdata;
	int vIndex = gl_VertexID & 0x03;
    if (!liquidPass) {
        vdata = faces[gl_VertexID / 4];
        position = vdata.xyz + ((vIndex + 1) & 2) * vdata.xyzA + (vIndex & 2) * vdata.xyzB;
        flags = vdata.flags[vIndex];
    }
 #endif

    int windMode = (flags >> WindModePosition) & 15;
#ifdef DRT_POINT_SHADOWS
    // Liquid upper bits are a different ABI: lava alone is not wind geometry.
    bool windCaster = liquidPass
        ? ((waterFlagsIn & 1) != 0 || (waterFlagsIn & LiquidWeakWaveBitMask) != 0)
        : windMode != 0;
 #if USESSBO > 0
    if (!liquidPass) {
        // Packed quads may have stationary roots and animated tips. Exclude
        // both triangles when any of the four authored corners animates.
        windCaster = any(notEqual((vdata.flags >> WindModePosition) & ivec4(15), ivec4(0)));
    }
 #endif
    drtPointWindMode = windMode;
    drtPointWindCaster = windCaster ? 1 : 0;
#else
    drtShadowWindMode = windMode;
#endif

	vec4 worldPos = vec4(position + origin, 1.0);
    if (liquidPass && drtExcludeEmitter == 0) {
        // Native water flags: bit 0 animates, bits 2..9 are oceanity,
        // bit 27 selects lava and bit 29 selects weak waves.
        if ((waterFlagsIn & 1) != 0) {
            float div = (waterFlagsIn & LiquidWeakWaveBitMask) != 0 ? 90.0 : 5.0;
            float oceanity = float((waterFlagsIn >> 2) & 255) / 255.0;
            worldPos = applyLiquidWarping((waterFlagsIn & LiquidIsLavaBitMask) == 0,
                worldPos, div * max(0.2, 1.0 - oceanity));
        } else if ((waterFlagsIn & LiquidWeakWaveBitMask) != 0) {
            worldPos = applyLiquidWarping((waterFlagsIn & LiquidIsLavaBitMask) == 0, worldPos, 90.0);
        }
    } else if (!liquidPass) {
        if (drtExcludeEmitter == 0) worldPos = applyVertexWarping(flags, worldPos);
        worldPos = applyGlobalWarping(worldPos);
    }
	// Light-relative position is stable at large world coordinates and lets
	// the static atlas omit only the block that owns this light source.
#ifdef DRT_POINT_SHADOWS
    drtPointLocalPos = worldPos.xyz;
#else
	drtShadowLocalPos = worldPos.xyz;
#endif
	
	gl_Position = mvpMatrix * worldPos;
	if (drtHeldProjectionIndex >= 0) {
	    int hand = drtHeldProjectionIndex;
	    gl_Position = drtHeldStatus[hand].x > 0.0
	        ? drtHeldMatrices[hand] * drtHeldCameraMatrix * worldPos
	        : vec4(2.0, 2.0, 2.0, 1.0); // empty fit: no rasterization
	}
	
    vec2 shadowUv = uvIn;
 #if USESSBO > 0
    if (!liquidPass) shadowUv = UnpackUv(vdata, vIndex, subpixelPaddingX, subpixelPaddingY);
 #endif
#ifdef DRT_POINT_SHADOWS
    drtPointUv = shadowUv;
#else
    uv = shadowUv;
#endif
	
	// We could use this to fix peter panninng on tall grass, but needs an extra render pass or extra vertex data for grass
	//gl_Position.w += 1 * 0.00025 / max(0.1, gl_Position.z * 0.05);
}
