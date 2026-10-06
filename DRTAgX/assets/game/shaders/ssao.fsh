#version 330 core

uniform sampler2D gPosition;
uniform sampler2D gNormal;
uniform sampler2D texNoise;
uniform vec3[64] samples;
uniform vec2 screenSize;
uniform sampler2D revealage;

in vec2 texcoord;
out vec4 outOcclusion;

#if SSAOLEVEL == 2
int kernelSize = 24;
float radius = 0.9;
#else
int kernelSize = 20;
float radius = 0.9;
#endif

float bias = 0.01;

uniform mat4 projection;


void main()
{
	float wboitatn = max(0.0, 1 - texture(revealage, texcoord).r) * 0.75;
	
	vec4 texVal = texture(gPosition, texcoord); 
	
	vec3 fragPos = texVal.xyz;
	float attenuate = texVal.w + wboitatn;

	// Native sky and celestial sprites write a zero position/normal sentinel.
	// Return before normalize(0): its undefined result can poison fragPos and AO.
	if (all(equal(fragPos, vec3(0.0)))) {
		outOcclusion = vec4(1.0);
		return;
	}
	
	texVal = texture(gNormal, texcoord);
	vec3 normal = normalize(texVal.xyz);
	bool leavesHack = texVal.w > 0;
	
	// This seems to completely fix any distant ssao flickering artifacts while perservering everything else
	// Tyron Mar 9: Completely borks fragments behind leaves, during heavy rain
	// Tyron Mar10: Breaks distant cliff walls, changed 90 to 150
	if (!leavesHack) {
		fragPos += normal * clamp(-fragPos.z/150 - 0.05, 0, 10);
	}


	float distanceFade = clamp(1.2 - (-fragPos.z) / 250, 0, 1);
	
	if (fragPos.x == 0 || distanceFade == 0) {
		outOcclusion = vec4(1);
		return;
	}
	
    // Use a deterministic normal-aligned kernel without screen-space jitter.
    // Choose a nonparallel axis so floor/wall normals never normalize zero.
    vec3 helperAxis = abs(normal.z) < 0.999 ? vec3(0.0, 0.0, 1.0) : vec3(0.0, 1.0, 0.0);
    vec3 tangent = normalize(cross(helperAxis, normal));
    vec3 bitangent = cross(normal, tangent);
    mat3 TBN = mat3(tangent, bitangent, normal);
	
    float occlusion = 0.0;
	
    for( int i = 0; i < kernelSize; ++i)
    {
        vec3 sample = TBN * samples[i];
        sample = fragPos + sample * radius;

        vec4 offset = vec4(sample, 1.0);
        offset = projection * offset;
        offset.xyz /= offset.w;
        offset.xyz = offset.xyz * 0.5 + 0.5;
		
		offset.x = clamp(offset.x, texcoord.x - 0.04,  texcoord.x + 0.04);
		offset.y = clamp(offset.y, texcoord.y - 0.04,  texcoord.y + 0.04);

        float sampleDepth = texture(gPosition, offset.xy).z;
		float depthDiff = sampleDepth - (sample.z + bias);
		float rangeCheck = 0;
		
		if (leavesHack) {

			if (depthDiff >= 0.02 && depthDiff < 0.2 && abs(dot(texture(gNormal, offset.xy).rgb, normal) - 1) > 0.25) {
				rangeCheck = smoothstep(0.0, 1.0, radius / abs(fragPos.z - sampleDepth));		
			}

		} else {
		
			if (depthDiff > 0 && depthDiff < 0.2) {
				rangeCheck = smoothstep(0.0, 1.0, radius / abs(fragPos.z - sampleDepth));		
			}
			
		}
		
		occlusion += rangeCheck;
    }

	float occ = clamp(1.0 - min(1, occlusion / kernelSize * distanceFade) * (1-attenuate), 0, 1);
	
	// Some distant geometry gets overly dark, lets clamp the lower limit
	#if SSAOLEVEL == 2
		occ = max(occ, 0.5);
	#else
		occ = max(occ, 0.7);
	#endif
	
	// We need MOAR SSAO >:D
	if (!leavesHack) {
		occ = 1 - (1-occ) * 1.4;
	}
	
	
    outOcclusion = vec4(occ, occ, occ, 1);
}
