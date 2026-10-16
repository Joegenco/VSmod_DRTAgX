#version 330 core
#extension GL_ARB_explicit_attrib_location: enable

layout(location = 0) in vec3 vertexPosition;

uniform mat4 projectionMatrix;
uniform mat4 modelMatrix;
uniform mat4 viewMatrix;

out vec3 texCoords;
out vec3 drtStarDirection;
// Keep the native fragment interface complete even when its height is unused.
out float worldPosY;
out float nightVisionStrengthv;

#include vertexflagbits.ash
#include shadowcoords.vsh
#include fogandlight.vsh

void main () {
  texCoords = vertexPosition;
  vec4 worldPos = modelMatrix * vec4(vertexPosition, 1.0);
  worldPosY = worldPos.y;
  drtStarDirection = normalize(worldPos.xyz);
  nightVisionStrengthv = nightVisionStrength * 0.33;
  
  gl_Position = projectionMatrix * viewMatrix * worldPos;
}
