#version 430 core
layout(local_size_x = 8, local_size_y = 8) in;
layout(rgba16f, binding = 0) writeonly uniform image2D target;
uniform sampler2D transmittanceTable;
#include drtagx_atmosphere_math.ash

void main() {
    ivec2 pixel = ivec2(gl_GlobalInvocationID.xy);
    ivec2 size = imageSize(target);
    if (any(greaterThanEqual(pixel, size))) return;
    vec2 uv = (vec2(pixel) + 0.5) / vec2(size);
    float sunMu = uv.x * 2.0 - 1.0;
    vec3 sun = vec3(sqrt(max(0.0, 1.0 - sunMu * sunMu)), sunMu, 0.0);
    vec3 p = vec3(0.0, DRT_PLANET + 0.001 + uv.y * uv.y * 99.998, 0.0);
    vec3 meanLight = vec3(0.0), feedback = vec3(0.0);
    for (int ray = 0; ray < 16; ++ray) {
        float mu = 1.0 - 2.0 * (float(ray) + 0.5) / 16.0;
        float phi = float(ray) * 2.39996323;
        vec3 d = vec3(cos(phi) * sqrt(1.0 - mu * mu), mu, sin(phi) * sqrt(1.0 - mu * mu));
        float stepLength = drtAtmosphereRayLength(p, d) / 16.0;
        vec3 t = vec3(1.0);
        for (int i = 0; i < 16; ++i) {
            vec3 samplePos = p + d * (stepLength * (float(i) + 0.5));
            vec3 r, m, e;
            drtMedium(samplePos, r, m, e);
            vec3 integral = t * drtSegmentIntegral(e, stepLength);
            meanLight += integral * (r + m) * drtSunTransmittance(transmittanceTable, samplePos, sun) / (16.0 * 4.0 * DRT_PI);
            feedback += integral * (r + m) / 16.0;
            t *= exp(-e * stepLength);
        }
    }
    // Isotropic successive scattering orders form a convergent geometric sum.
    vec3 radiance = meanLight / max(vec3(1.0) - feedback, vec3(0.05));
    imageStore(target, pixel, vec4(radiance, 1.0));
}
