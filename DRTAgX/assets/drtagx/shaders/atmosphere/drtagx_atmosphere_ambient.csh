#version 430 core
layout(local_size_x = 64) in;
#define DRT_AMBIENT_SKY_SAMPLING
#include drtagx_atmosphere_sampling.fsh
// Twenty-six vec4s preserve the existing CPU/UBO prefix; write only byte offset 416.
layout(std430, binding = 11) writeonly buffer DrtAmbientOutput { vec4 framePrefix[26]; vec4 ambientResult; };
shared vec3 hemisphere[64];
shared vec3 solarRegion[64];
const vec3 DRT_AMBIENT_LUMA = vec3(0.2126, 0.7152, 0.0722);
// This native carrier shades the total sun/sky mixture, rather than diffuse blue sky alone.
// Peak daylight reaches 6x above 20 degrees; a separate twilight lift supports the still-bright sky.
const float DRT_AMBIENT_DAY_GAIN = 6.0;
const float DRT_AMBIENT_TWILIGHT_GAIN = 3.0; // +1.585 stops while the horizon sky is still illuminated.
const float DRT_AMBIENT_DAY_NEUTRALITY = 0.98;
const float DRT_AMBIENT_SUNSET_TINT = 0.85;
// Rec.709 linear chromaticities: D65 daylight white (~6500 K), and a 2200 K Planckian tint.
// The warm vector is normalized below so temperature edits do not change luminance.
const vec3 DRT_AMBIENT_DAY_WHITE = vec3(1.0);
const vec3 DRT_AMBIENT_SUNSET_WHITE = vec3(2.31242, 0.70426, 0.06579);
const float DRT_SKY_AMBIENT_WARMTH = 0.35;

void main() {
    uint i = gl_LocalInvocationIndex;
    float mu = (float(i) + 0.5) / 64.0;
    // Zenith has no azimuth; do not rely on undefined atan(0,0).
    float sunAzimuth = abs(drtAtmosphereSun.x) + abs(drtAtmosphereSun.z) > 0.000001
        ? atan(drtAtmosphereSun.z, drtAtmosphereSun.x) : 0.0;
    float phi = sunAzimuth + float(i) * 2.39996323;
    vec3 direction = vec3(cos(phi) * sqrt(1.0 - mu * mu), mu, sin(phi) * sqrt(1.0 - mu * mu));
    // E/pi = 2 * average(L * cos(theta)) for uniform upper-hemisphere samples.
    // Use clear atmospheric radiance: native weather overlays are replayed once by the surface helper.
    hemisphere[i] = drtClearSkyRadiance(direction) * (2.0 * mu / 64.0);
    float azimuth = sunAzimuth + radians(60.0) * ((float(i % 8u) + 0.5) / 8.0 - 0.5);
    float elevation = radians(20.0) * (float(i / 8u) + 0.5) / 8.0;
    direction = vec3(cos(azimuth) * cos(elevation), sin(elevation), sin(azimuth) * cos(elevation));
    // A broad 60 x 20 degree solar horizon region, never the sprite or a single peak texel.
    solarRegion[i] = drtClearSkyRadiance(direction) / 64.0;
    barrier();
    for (uint stride = 32u; stride > 0u; stride >>= 1u) {
        if (i < stride) { hemisphere[i] += hemisphere[i + stride]; solarRegion[i] += solarRegion[i + stride]; }
        barrier();
    }
    if (i != 0u) return;
    vec3 sky = hemisphere[0];
    float energy = dot(sky, DRT_AMBIENT_LUMA);
    float warmEnergy = dot(solarRegion[0], DRT_AMBIENT_LUMA);
    float twilight = 1.0 - smoothstep(0.0, 0.25, abs(drtAtmosphereSun.y));
    float cloud = clamp(drtAmbientNative.w, 0.0, 1.0);
    // Extra sunset chroma is energy-neutral; dense clouds suppress the angular bias.
    vec3 warm = solarRegion[0] * (energy / max(warmEnergy, 0.000001));
    float bias = DRT_SKY_AMBIENT_WARMTH * twilight * (1.0 - cloud) * step(0.000001, warmEnergy);
    vec3 ambient = mix(sky, warm, bias);
    float solarY = drtAtmosphereSun.y;
    // Solar elevation owns both ramps: dawn/dusk remain continuous without changing the LUT or sky.
    float dayGain = smoothstep(sin(radians(6.0)), sin(radians(20.0)), solarY);
    float daylightWhite = smoothstep(0.0, sin(radians(10.0)), solarY);
    float sunTint = smoothstep(sin(radians(-6.0)), 0.0, solarY);
    vec3 sunsetWhite = DRT_AMBIENT_SUNSET_WHITE / dot(DRT_AMBIENT_SUNSET_WHITE, DRT_AMBIENT_LUMA);
    vec3 mixtureWhite = mix(sunsetWhite, DRT_AMBIENT_DAY_WHITE, daylightWhite);
    // Keep a little LUT chroma for daily variation; dense clouds reduce the warm solar tint.
    float tint = sunTint * mix(DRT_AMBIENT_SUNSET_TINT * (1.0 - 0.5 * cloud),
        DRT_AMBIENT_DAY_NEUTRALITY, daylightWhite);
    ambient = mix(ambient, mixtureWhite * energy, tint);
    float twilightLift = smoothstep(sin(radians(-6.0)), sin(radians(-2.0)), solarY) *
        (1.0 - smoothstep(sin(radians(10.0)), sin(radians(20.0)), solarY));
    // Add twilight support, fading out by full daylight and deep night; the minimum floor stays exact.
    ambient *= mix(1.0, DRT_AMBIENT_DAY_GAIN, dayGain) + (DRT_AMBIENT_TWILIGHT_GAIN - 1.0) * twilightLift;
    ambientResult = vec4(max(ambient, vec3(0.0)), 1.0);
}
