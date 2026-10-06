#version 330 core
#extension GL_ARB_explicit_attrib_location: enable

layout (location = 0) in vec3 vertexPosition;		// Per vertex
layout (location = 1) in vec2 uvIn;					// Per vertex
layout (location = 2) in vec4 baseColor;			// Per vertex

layout (location = 3) in int renderFlags;	 		// Per instance
layout (location = 4) in vec3 particlePosition; 	// Per instance
layout (location = 5) in float scale;					// Per instance
layout (location = 6) in vec4 particleDir; 			// Per instance
layout (location = 7) in vec4 rgbaLightIn; 		// Per instance
layout (location = 8) in vec4 rgbaBlockIn; 		// Per instance

uniform vec4 rgbaFogIn;
uniform vec3 rgbaAmbientIn;	
uniform float fogMinIn;
uniform float fogDensityIn;
uniform mat4 projectionMatrix;
uniform mat4 modelViewMatrix;

out vec4 color;
out vec4 rgbaFog;
out vec3 vexPos;
out vec3 drtParticleWorldPos;
out float fogAmount;
out float extraWeight;
out vec2 uv;

#include vertexflagbits.ash
#include shadowcoords.vsh
#define DRT_FORWARD_PLACED_LIGHTS 1
#include fogandlight.vsh
#include vertexwarp.vsh

mat4 rotationZ( in float angle ) {
	return mat4(	cos(angle),		-sin(angle),	0,	0,
			 		sin(angle),		cos(angle),		0,	0,
							0,				0,		1,	0,
							0,				0,		0,	1);
}


void main()
{
	mat4 mvmat = modelViewMatrix;
	// This makes all snow particles submerged :<
	//vexPos = vertexPosition * scale - 0.125 * scale;
	
	vexPos = vertexPosition * scale;
	
	bool rainParticle = renderFlags < 0; //(renderFlags & (1<<31)) > 0;
	
	extraWeight = (renderFlags & (1<<9)) > 0 ? 1 : 10;

	uv = uvIn;
	if (rainParticle) uv = vec2(0.5, 0.5);
	
	// 1. Translate the particle
	mvmat[3] = mvmat * vec4(particlePosition, 1.0);

	if (rainParticle) {
		vec3 u = particleDir.xyz; // your input vector
		vec3 v = vec3(0.0, -1.0, 0.0); // your other input vector
		
		u = normalize(u);
		
		float zangle = acos( dot( u.xy, v.xy ) );
		mvmat = mvmat * rotationZ(zangle);
	}
	
	
	// 2. Billboard the particle
	mvmat[0].xyz = vec3(1.0, 0.0, 0.0);

	if (!rainParticle) {
		mvmat[1].xyz = vec3(0.0, 1.0, 0.0);
	}

	mvmat[2].xyz = vec3(0.0, 0.0, 1.0);
	
	
	
	// 3. Lighting
	vec4 worldPos = vec4(vexPos, 1.0);
	if (rainParticle) {
		worldPos.y = worldPos.y * 8 - 3;
		worldPos.xz /= 3.5;
	}

	
	worldPos = applyVertexWarping(renderFlags, worldPos);
	worldPos = applyGlobalWarping(worldPos);
	
	vec4 cameraPos = mvmat * worldPos;
	color = baseColor * applyLight(rgbaAmbientIn, rgbaLightIn, renderFlags, cameraPos) * rgbaBlockIn;
	color.a = rgbaBlockIn.a;
	rgbaFog = rgbaFogIn;

	if (rainParticle) {
		color.a = min(1, 1.05*color.a * (1.2 - clamp(1 - 7*vexPos.y, 0, 1)));
	}
	
	calcShadowMapCoords(mvmat, vec4(worldPos.x + particlePosition.x, worldPos.y + particlePosition.y, worldPos.z + particlePosition.z, worldPos.w));
 
	// 4. Done.
	gl_Position = projectionMatrix * cameraPos;
#ifdef DRT_FOG_TRANSPORT
	drtParticleWorldPos = (drtAtmosphereInvView * cameraPos).xyz;
#else
	drtParticleWorldPos = particlePosition + worldPos.xyz;
#endif
	fogAmount = getFogLevel(vec4(particlePosition, 0), fogMinIn, fogDensityIn);
}
