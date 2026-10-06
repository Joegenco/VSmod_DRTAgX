#version 330 core
#extension GL_ARB_explicit_attrib_location: enable

layout(location = 0) in vec3 vertexPositionIn;
layout(location = 1) in vec2 uvIn;
layout(location = 2) in vec4 colorIn;
layout(location = 3) in int flags;


uniform vec4 rgbaFogIn;
uniform vec3 rgbaAmbientIn;
uniform mat4 projectionMatrix;
uniform mat4 modelMatrix;
uniform mat4 viewMatrix;
uniform int extraGlow;


out vec3 vertexPosition;
out vec4 rgbaFog;
out vec2 uv;
out vec4 color;

#include vertexflagbits.ash
#include shadowcoords.vsh
#include fogandlight.vsh

void main(void)
{
	vec4 worldPos = modelMatrix * vec4(vertexPositionIn, 1.0);
	vec4 cameraPos = viewMatrix * worldPos;
	uv = uvIn;
	glowLevel = (extraGlow + (flags & 0xff)) / 128.0;
	color = colorIn;	
	color.a *= clamp(1 - getSpheresFogAmount(worldPos.xyz * 10), 0, 1);
	
	rgbaFog = rgbaFogIn;	
	gl_Position = projectionMatrix * cameraPos;	
	
	vertexPosition = worldPos.xyz;
}
