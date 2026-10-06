#version 430 core
layout(local_size_x = 8, local_size_y = 8) in;
layout(rgba16f, binding = 0) writeonly uniform image2D target;
#include drtagx_atmosphere_math.ash

void main() {
    ivec2 pixel = ivec2(gl_GlobalInvocationID.xy);
    ivec2 size = imageSize(target);
    if (any(greaterThanEqual(pixel, size))) return;
    vec2 uv = (vec2(pixel) + 0.5) / vec2(size);
    float mu = uv.x * 2.0 - 1.0;
    vec3 p = vec3(0.0, DRT_PLANET + 0.001 + uv.y * uv.y * (DRT_TOP - DRT_PLANET - 0.002), 0.0);
    vec3 d = vec3(sqrt(max(0.0, 1.0 - mu * mu)), mu, 0.0);
    float distance = drtAtmosphereRayLength(p, d);
    vec3 opticalDepth = vec3(0.0);
    for (int i = 0; i < 64; ++i) {
        vec3 r, m, e;
        drtMedium(p + d * (distance * (float(i) + 0.5) / 64.0), r, m, e);
        opticalDepth += e * (distance / 64.0);
    }
    imageStore(target, pixel, vec4(exp(-opticalDepth), 1.0));
}
