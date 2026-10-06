#version 330 core
#extension GL_ARB_explicit_attrib_location: enable

// Generic block/item/entity vertex stage. Native startup and DRTAgX gameplay
// includes expose different lighting interfaces; capability guards cover both.

layout(location = 0) in vec3 vertexPositionIn;
layout(location = 1) in vec2 uvIn;
layout(location = 2) in vec4 colorIn;
layout(location = 3) in int flags;
#if defined(GLOWSUB)
layout(location = 4) in float glowSub;
#endif

uniform vec4 rgbaTint;
uniform vec3 rgbaAmbientIn;
uniform vec4 rgbaLightIn;
uniform vec4 rgbaGlowIn;
uniform vec4 rgbaFogIn;
uniform int extraGlow;
// Scoped by the native sun/moon callback; ordinary standard draws keep zero.
uniform int drtSunSprite = 0;
uniform float fogMinIn;
uniform float fogDensityIn;

uniform mat4 projectionMatrix;
uniform mat4 viewMatrix;
uniform mat4 modelMatrix;

uniform int dontWarpVertices;
uniform int fadeFromSpheresFog;
uniform int addRenderFlags;
uniform float extraZOffset;

out vec2 uv;
out vec4 color;
out vec4 rgbaFog;
out vec4 rgbaGlow;
out float fogAmount;
out vec4 camPos;
out vec4 worldPos;
flat out int renderFlags;
out float voxSunLight;

out vec3 normal;
#if SSAOLEVEL > 0
out vec4 gnormal;
#endif


#include vertexflagbits.ash
#include shadowcoords.vsh
#define DRT_FORWARD_PLACED_LIGHTS 1
// Native unshaded GUI/preview draws share this program with world items.
uniform int normalShaded;
uniform int skyShaded;
#define DRT_FORWARD_PLACED_GUARD (normalShaded > 0 || skyShaded > 0)
#define DRT_DYNAMIC_NORMAL_VIEW (viewMatrix * vec4(normal, 0.0)).xyz
#include fogandlight.vsh
#include vertexwarp.vsh

void main(void)
{
#ifdef DRT_EXPLICIT_SURFACE_LIGHTING
    voxSunLight = drtSunAccess(rgbaLightIn.a);
#else
    // The first engine compile precedes the mod include loader. Retain native
    // light metadata here; normalized world bounds arrive with the shared include.
    voxSunLight = clamp(rgbaLightIn.a, 0.0, 1.0);
#endif
	worldPos = modelMatrix * vec4(vertexPositionIn, 1.0);
	
	if (dontWarpVertices == 0) {
		worldPos = applyVertexWarping(flags | addRenderFlags, worldPos);
		worldPos = applyGlobalWarping(worldPos);
	}
	if (dontWarpVertices == 2) {
		int windMode = ((flags | addRenderFlags) >> WindModePosition) & 0xF;
		vec4 newPos = applyVertexWarping(flags | addRenderFlags, worldPos);
		worldPos = mix(worldPos, newPos, 0.25); // Hardcoded intensity downscale of 4x
		worldPos = applyGlobalWarping(worldPos);
	}
	
	camPos = viewMatrix * worldPos;
	
	uv = uvIn;
	
	float gs = 0.0;
#if defined(GLOWSUB)
	gs = glowSub;
#endif

	int glow = clamp(extraGlow + (flags & GlowLevelBitMask) - int(gs * 255), 0, 255);
	
	renderFlags = glow | (flags & ~GlowLevelBitMask);
	rgbaGlow.rgb = rgbaGlowIn.rgb * max(vec3(0), (1 - vec3(3*gs)));
	rgbaGlow.a = rgbaGlowIn.a;
	
	// Decode before lighting so each face responds to the point-light direction.
	normal = unpackNormal(flags);
	normal = normalize((modelMatrix * vec4(normal, 0.0)).xyz);
	color = rgbaTint * applyLight(rgbaAmbientIn, rgbaLightIn, renderFlags, camPos) * colorIn;
	// Carry tint in the explicit components too, so GLOWSUB emission remains
	// independent of sunlight when the fragment stage shades the mixed color.
#ifdef DRT_EXPLICIT_SURFACE_LIGHTING
	drtSunLight *= rgbaTint.rgb * colorIn.rgb;
	drtLocalLight *= rgbaTint.rgb * colorIn.rgb;
#endif
#if defined(GLOWSUB)
	color.rgb *= 1 - 0.5 * gs;
	float glowMix = clamp(max(0.0, glowLevel - gs) / 2.0, 0.0, 1.0);
	color.rgb = mix(color.rgb, rgbaGlow.rgb, glowMix);
#ifdef DRT_EXPLICIT_SURFACE_LIGHTING
	drtSunLight *= (1.0 - 0.5 * gs) * (1.0 - glowMix);
	drtLocalLight = drtLocalLight * (1.0 - 0.5 * gs) * (1.0 - glowMix) + rgbaGlow.rgb * glowMix;
#endif
#endif	
	

	if (fadeFromSpheresFog > 0) {
		color.a *= clamp(1 - getSpheresFogAmount(vertexPositionIn * 10), 0, 1);
	}

	// Distance fade out
	color.a *= clamp(20 * (1.10 - length(worldPos.xz) / viewDistance) - 5, -1, 1);

	rgbaFog = rgbaFogIn;
	gl_Position = projectionMatrix * camPos;
	calcShadowMapCoords(viewMatrix, worldPos);
	
	fogAmount = getFogLevel(worldPos, fogMinIn, fogDensityIn);
	
	gl_Position.w += extraZOffset;

    if (drtSunSprite != 0) {
        // The sun is a distant emitter, not a lit world item. Preserve native
        // tint/coverage without surface light or view-distance fading.
        color = rgbaTint * colorIn;
        color.a *= clamp(1.0 - getSpheresFogAmount(worldPos.xyz * 10.0), 0.0, 1.0);
    }
	
	
	#if SSAOLEVEL > 0
		gnormal = viewMatrix * vec4(normal, 0);
	#endif
}
