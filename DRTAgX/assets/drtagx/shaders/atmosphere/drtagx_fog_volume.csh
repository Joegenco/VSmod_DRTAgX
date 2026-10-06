#version 430 core
layout(local_size_x = 8, local_size_y = 4) in;
layout(r16f, binding = 0) writeonly uniform image3D target;
uniform sampler2DShadow farShadow;
uniform mat4 toShadow;
uniform vec3 cameraInShadow;
uniform float shaftStrength;
#include drtagx_fog_transport.ash

void main() {
    ivec2 pixel = ivec2(gl_GlobalInvocationID.xy);
    ivec3 size = imageSize(target);
    if (any(greaterThanEqual(pixel, size.xy))) return;
    vec2 uv = (vec2(pixel) + 0.5) / vec2(size.xy);
    vec4 view = drtAtmosphereInvProjection * vec4(uv * 2.0 - 1.0, 1.0, 1.0);
    vec3 direction = normalize(mat3(drtAtmosphereInvView) * view.xyz);
    float sum = 0.0, weight = 0.0, previousDistance = 0.0;
    float opticalDepth = 0.0;
    for (int z = 0; z < size.z; ++z) {
        float distance = exp((float(z) + 0.5) / float(size.z) * log(1.0 + drtAtmosphereFlags.z)) - 1.0;
        vec3 start = direction * previousDistance, end = direction * distance;
        float deltaTau = drtAirOpticalDepth(start, end)
            - log(max(drtWorldFogSegmentTransmittance(start, end), 0.000001));
        float segmentWeight = exp(-opticalDepth) * (1.0 - exp(-deltaTau));
        opticalDepth += deltaTau;
        vec3 mid = direction * (0.5 * (distance + previousDistance));
        vec3 coords = (toShadow * vec4(mid, 0.0)).xyz + cameraInShadow;
        float visibility = 1.0;
        if (all(greaterThanEqual(coords, vec3(0.0))) && all(lessThanEqual(coords, vec3(1.0))))
            visibility = texture(farShadow, vec3(coords.xy, coords.z - 0.00003));
        sum += segmentWeight * mix(1.0, visibility, shaftStrength);
        weight += segmentWeight;
        imageStore(target, ivec3(pixel, z), vec4(weight > 0.00001 ? sum / weight : 1.0));
        previousDistance = distance;
    }
}
