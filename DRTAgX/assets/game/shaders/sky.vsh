#version 330 core
#extension GL_ARB_explicit_attrib_location: enable

layout(location = 0) in vec3 vertexPositionIn;
layout(location = 1) in vec4 vertexColor;

uniform vec4 rgbaFogIn;
uniform vec3 rgbaAmbientIn;
uniform mat4 projectionMatrix;
uniform mat4 modelViewMatrix;

out vec3 vertexPosition;
out vec4 rgbaFog;
out float nightVisionStrengthv;

#include vertexflagbits.ash
#include shadowcoords.vsh
#include fogandlight.vsh

void main()
{
	vertexPosition = vertexPositionIn;
	rgbaFog = rgbaFogIn;
	nightVisionStrengthv = nightVisionStrength;
	vec4 cameraPos = modelViewMatrix * vec4(vertexPosition, 1.0);
	
    gl_Position = projectionMatrix * cameraPos;
}