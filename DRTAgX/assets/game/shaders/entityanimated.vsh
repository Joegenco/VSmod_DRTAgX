#version 330 core
#extension GL_ARB_explicit_attrib_location: enable

// SheyderMod: vendored engine entityanimated.vsh (animated entity vertex stage).
// The entity-only light define below selects directional point-light mixing.

layout(location = 0) in vec3 vertexPositionIn;
layout(location = 1) in vec2 uvIn;
layout(location = 2) in vec4 colorIn;
layout(location = 3) in int flags;
layout(location = 4) in float damageEffectIn;
layout(location = 5) in int jointId;

uniform vec3 rgbaAmbientIn;
uniform vec4 rgbaLightIn;
uniform vec4 rgbaFogIn;
uniform float fogMinIn;
uniform float fogDensityIn;
uniform vec4 renderColor;
uniform int addRenderFlags;
uniform float frostAlpha = 0;
uniform mat4 projectionMatrix;
uniform mat4 viewMatrix;
uniform mat4 modelMatrix;
uniform int extraGlow;

// No longer needed but kept to not break mods that still assign these values
uniform int skipRenderJointId;
uniform int skipRenderJointId2;

// UBO:Animation,0,4800
layout (std140) uniform Animation
{
    mat4 values[MAXANIMATEDELEMENTS];	// MAXANIMATEDELEMENTS constant is defined during game engine shader loading.
} ElementTransforms;

out vec2 uv;
out vec4 color;
out vec4 rgbaFog;
out float fogAmount;
out vec3 vertexPosition;
out vec4 worldPos;
out float damageEffect;
out vec4 camPos;
out float fragFrostAlpha;
flat out int renderFlags;
out float voxSunLight;

out vec4 glPos;

out vec3 normal;
#if SSAOLEVEL > 0
out vec4 fragPosition;
out vec4 gnormal;
#endif


#include vertexflagbits.ash
#include shadowcoords.vsh
// Share placed-light falloff with terrain after the startup native compile.
#define DRT_FORWARD_PLACED_LIGHTS 1
// Only animated entities shade their covered voxel/source blend by placed-light
// direction. A normal facing away retains 10% of head-on local illumination.
#define DRT_ANIMATED_PLACED_FACING 1
#define DRT_DYNAMIC_NORMAL_VIEW (viewMatrix * vec4(normal, 0.0)).xyz
#include fogandlight.vsh
#include vertexwarp.vsh

void main(void)
{

#ifdef DRT_EXPLICIT_SURFACE_LIGHTING
    voxSunLight = drtSunAccess(rgbaLightIn.a);
#else
    // Native entity programs can compile before Sheyder installs the shared
    // lighting include. Use its native metadata during that startup compile.
    voxSunLight = clamp(rgbaLightIn.a, 0.0, 1.0);
#endif

	damageEffect = damageEffectIn;
	mat4 animModelMat = modelMatrix * ElementTransforms.values[jointId];
	worldPos = animModelMat * vec4(vertexPositionIn, 1.0);

	renderFlags = flags | addRenderFlags;
	
	if ((renderFlags & WindModeFruitMask) > 0) {
		fragFrostAlpha = frostAlpha / 4;
		renderFlags &= ~WindModeFruitMask;
	} else fragFrostAlpha = frostAlpha;
	
	if ((renderFlags & WindModeWaterMask) > 0) {
		worldPos = applyLiquidWarping(true, worldPos, 5);
	} else {
		worldPos = applyVertexWarping(renderFlags, worldPos);
	}
	worldPos = applyGlobalWarping(worldPos);
	
	vertexPosition = vertexPositionIn.xyz * 1.5;
		
	vec4 cameraPos = camPos = viewMatrix * worldPos;

	uv = uvIn;
	int renderFlags = extraGlow + flags;
	// The animated model transform must rotate the normal before point-light shading.
	normal = unpackNormal(renderFlags);
	normal = (animModelMat * vec4(normal, 0.0)).xyz;
	color = renderColor * colorIn * applyLight(rgbaAmbientIn, rgbaLightIn, renderFlags, cameraPos);
	rgbaFog = rgbaFogIn;
	
	// Distance fade out
	color.a *= clamp(20 * (1.05 - length(worldPos.xz) / viewDistance) - 5, -1, 1);
	
	gl_Position = projectionMatrix * cameraPos;
	calcShadowMapCoords(viewMatrix, worldPos);
	
	
	fogAmount = getFogLevel(worldPos, fogMinIn, fogDensityIn);
	
	
	#if SSAOLEVEL > 0
		fragPosition = cameraPos;
		gnormal = viewMatrix * vec4(normal, 0);
	#endif
}
