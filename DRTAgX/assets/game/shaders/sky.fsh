#version 330 core
#extension GL_ARB_explicit_attrib_location: enable

in vec3 vertexPosition;
in vec4 rgbaFog;
in float nightVisionStrengthv;

uniform float fogDensityIn;
uniform float fogMinIn;
uniform float dayLight;
uniform float horizonFog;
uniform vec3 playerPos;
uniform vec3 sunPosition;

layout(location = 0) out vec4 outColor;
layout(location = 1) out vec4 outGlow;
#if SSAOLEVEL > 0
layout(location = 2) out vec4 outGNormal;
layout(location = 3) out vec4 outGPosition;
#endif

#include dither.fsh
#include fogandlight.fsh
#include skycolor.fsh
#include underwatereffects.fsh

void main()
{
	outColor = vec4(1);
	outGlow = vec4(1);
	float sealevelOffsetFactor = 0.25;
	getSkyColorAt(vertexPosition, sunPosition, sealevelOffsetFactor, clamp(dayLight, 0, 1), horizonFog, outColor, outGlow);
	// Verified native draw blend: SrcAlpha / OneMinusSrcAlpha. Subtract the
	// clear atmosphere already drawn by nightsky, then divide by this overlay's
	// alpha. This composes sky/weather once without attenuating stars twice.
	if (drtAtmosphereFlags.x > 0.5 && drtAtmosphereView.z < 0.7 && outColor.a > 0.00001) {
		outColor.rgb = drtSkyOverlayRadiance(outColor.rgb, vertexPosition, dayLight, outColor.a);
	}
	
	if (psychedelicStrength > Epsilon) outColor = applyPsychedelicEffect(outColor, vertexPosition.xyz/2, 0);
	
	// Water replaces the overlay with completed ordered background transport;
	// using alpha=1 also removes the dry star base behind submerged views.
	if (drtAtmosphereView.z > 0.7 || drtFilteredLiquidDepth() < 0.999999) {
		outColor = drtApplyTransport(vec4(drtClearSkyRadiance(vertexPosition), 1.0),
			drtSkyMediumTransport(vertexPosition));
		outColor.a = 1.0;
		outGlow.rg = vec2(0.0);
	}
	
	outColor.rgb += vec3(0.1, 0.5, 0.1) * nightVisionStrengthv;
	outGlow.y *= clamp((dayLight - 0.05) * 2, 0, 1);
#if SSAOLEVEL > 0
	outGPosition = vec4(0);
	outGNormal = vec4(0);
#endif

}
