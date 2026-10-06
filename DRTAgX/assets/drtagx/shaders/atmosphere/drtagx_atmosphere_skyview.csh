#version 430 core
layout(local_size_x = 8, local_size_y = 8) in;
layout(rgba16f, binding = 0) writeonly uniform image2D target;
uniform sampler2D transmittanceTable;
uniform sampler2D multiscatterTable;
uniform vec4 sunDirection;
uniform vec4 moonDirection;
uniform float cameraAltitude;
#include drtagx_atmosphere_math.ash
#include drtagx_celestial_balance.ash

void main() {
    ivec2 pixel = ivec2(gl_GlobalInvocationID.xy);
    ivec2 size = imageSize(target);
    if (any(greaterThanEqual(pixel, size))) return;
    vec2 uv = (vec2(pixel) + 0.5) / vec2(size);
    float phi = (uv.x - 0.5) * (2.0 * DRT_PI);
    float elevation = (uv.y - 0.5) * DRT_PI;
    // Below-horizon background uses the horizon sky, matching boundary haze.
    vec3 d = vec3(cos(phi) * cos(max(elevation, 0.0)), sin(max(elevation, 0.0)), sin(phi) * cos(max(elevation, 0.0)));
    vec3 p = vec3(0.0, DRT_PLANET + max(cameraAltitude * 0.001, 0.001), 0.0);
    float distance = drtAtmosphereRayLength(p, d);
    vec3 radiance = vec3(0.0), t = vec3(1.0);
    for (int i = 0; i < 32; ++i) {
        // Quadratic steps resolve near-ground aerosols without more samples.
        float a = float(i) / 32.0, b = float(i + 1) / 32.0;
        float stepLength = (b * b - a * a) * distance;
        vec3 samplePos = p + d * (0.5 * (a * a + b * b) * distance);
        vec3 r, m, e;
        drtMedium(samplePos, r, m, e);
        vec3 integral = t * drtSegmentIntegral(e, stepLength);
        for (int light = 0; light < 2; ++light) {
            vec4 source = light == 0 ? sunDirection : moonDirection;
            float cosine = dot(d, source.xyz);
            // A second, normalized aerosol lobe spreads spectrally reddened
            // sunlight across the dawn/dusk horizon without a large daytime halo.
            float twilight = light == 0 ? 1.0 - smoothstep(0.0, 0.25, abs(source.y)) : 0.0;
            float aerosolPhase = mix(drtMiePhase(cosine), drtAerosolPhase(cosine, 0.6), drtScatteringProfile.w * twilight);
            vec3 direct = drtSunTransmittance(transmittanceTable, samplePos, source.xyz) *
                (r * drtRayleighPhase(cosine) + m * aerosolPhase);
            vec2 msUv = vec2(dot(normalize(samplePos), source.xyz) * 0.5 + 0.5,
                sqrt(clamp((length(samplePos) - DRT_PLANET) / 100.0, 0.0, 1.0)));
            vec3 multiple = texture(multiscatterTable, msUv).rgb * (r + m);
            // Native strength is a ground-light gain. The upper atmosphere
            // remains sunlit after the ground enters shadow; extinction and
            // planet occlusion supply the sunset color and spatial fade.
            float strength = max(source.w, 0.0);
            if (light == 0)
                strength = max(strength, smoothstep(sin(radians(-8.0)), sin(radians(-2.0)), source.y));
            else strength *= DRT_MOON_GAIN; // Dim the complete lunar halo, including multiple scattering.
            radiance += integral * (direct + multiple) * strength;
        }
        t *= exp(-e * stepLength);
    }
    imageStore(target, pixel, vec4(max(radiance, vec3(0.0)), 1.0));
}
