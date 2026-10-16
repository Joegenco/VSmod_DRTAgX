#version 330 core
#extension GL_ARB_explicit_attrib_location: enable

// SheyderMod: vendored engine entityanimated.vsh (animated entity vertex stage).
// The entity-only light define below selects blocklight with placed directionality.

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
// Scoped only around native animated block draws; creatures and hands keep
// their inexpensive voxel-light brightness with placed-light directionality.
uniform int drtAnimatedBlockDraw = 0;

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
// Fragment USEOIT selects the translucent material pass; the native vertex
// prefix does not carry that option. Keep draw-wide glow separate from faces.
out vec3 drtAnimatedInheritedEmission;
out float drtAnimatedInheritedGlow;
// Preserve unlit material tint so the fragment receiver can add PLS even when
// a native voxel-light channel is zero, without touching coverage alpha.
out vec3 drtAnimatedMaterialTint;

out vec3 normal;
#if SSAOLEVEL > 0
out vec4 fragPosition;
out vec4 gnormal;
#endif


#include vertexflagbits.ash
#include shadowcoords.vsh
// Creatures retain cheap unshadowed vertex lighting. Animated placed blocks
// instead use the same completed source/depth snapshot as other block models.
#define DRT_FORWARD_PLACED_LIGHTS 1
// Keep native blocklight brightness/color; placed sources only shape its direction.
#define DRT_ANIMATED_PLACED_FACING 1
#define DRT_FORWARD_PLACED_GUARD (drtAnimatedBlockDraw == 0)
// Block PLS replaces voxel light in the fragment; enforce its visor minimum
// there too so source radiance is not added on top of an already raised floor.
#define DRT_VERTEX_NIGHTVISION_GUARD (drtAnimatedBlockDraw == 0)
// Opening a placed block switches from terrain to this shared model shader.
// Keep the same sun/voxel gains: a renderer-wide dimmer creates a brightness
// pop even on unchanged faces. Directional shading stays in the shared helpers.
// Open doors/chests share this shader with creatures. Keep the ordinary local
// light response so switching from a terrain mesh does not double facing light.
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
	drtAnimatedMaterialTint = renderColor.rgb * colorIn.rgb;
	int lightingFlags = extraGlow + flags;
	drtAnimatedInheritedEmission = vec3(0.0);
	drtAnimatedInheritedGlow = 0.0;
#ifdef DRT_EXPLICIT_SURFACE_LIGHTING
    // Entity-wide glow (native locusts: 16/32) must not make opaque legs emit.
    // Face-authored lamp/eye glow stays in ordinary lighting for every material.
    // Saturation prevents extra glow carrying into geometry/normal flag bits.
    int authoredGlow = flags & GlowLevelBitMask;
    int fullGlow = authoredGlow + clamp(extraGlow, 0, GlowLevelBitMask);
    fullGlow = min(fullGlow, GlowLevelBitMask);
    lightingFlags = (flags & ~GlowLevelBitMask) | authoredGlow;
    float inheritedGlow = float(fullGlow - authoredGlow) * 0.00390625;
    drtAnimatedInheritedEmission = vec3(DRT_EMISSION_GAIN * inheritedGlow) * renderColor.rgb * colorIn.rgb;
    drtAnimatedInheritedGlow = inheritedGlow / 2.5;
#endif
	// The animated model transform must rotate the normal before point-light shading.
	normal = unpackNormal(renderFlags);
	normal = (animModelMat * vec4(normal, 0.0)).xyz;
	color = renderColor * colorIn * applyLight(rgbaAmbientIn, rgbaLightIn, lightingFlags, cameraPos);
#ifdef DRT_EXPLICIT_SURFACE_LIGHTING
    // Match the tint already carried by prelit color. Interpolating untinted
    // components would repartition sunlight/local light incorrectly on models
    // with varying vertex colors; coverage alpha stays entirely native.
    drtSunLight *= renderColor.rgb * colorIn.rgb;
    drtLocalLight *= renderColor.rgb * colorIn.rgb;
#endif
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
