// Atmospheric integration uses kilometers to avoid precision loss at Earth radius.
// Game-space block distances are converted by 0.001 at the LUT boundary.
const float DRT_PLANET = 6360.0;
const float DRT_TOP = 6460.0;
const vec3 DRT_RAYLEIGH = vec3(0.005802, 0.013558, 0.033100);
const vec3 DRT_MIE_SCATTER = vec3(0.003996);
const vec3 DRT_MIE_EXTINCT = vec3(0.004440);
const float DRT_PI = 3.14159265359;
// Native daily SunsetMod drives bounded aerosol/ozone transport; defaults retain the reference model.
uniform vec4 drtScatteringProfile = vec4(1.0, 1.0, 1.0, 0.35);

vec2 drtSphereRoots(vec3 p, vec3 d, float radius) {
    float b = dot(p, d);
    float delta = b * b - dot(p, p) + radius * radius;
    if (delta < 0.0) return vec2(-1.0);
    float q = sqrt(delta);
    return vec2(-b - q, -b + q);
}

float drtAtmosphereRayLength(vec3 p, vec3 d) {
    vec2 top = drtSphereRoots(p, d, DRT_TOP);
    vec2 ground = drtSphereRoots(p, d, DRT_PLANET);
    float distance = max(top.y, 0.0);
    if (ground.x > 0.0001) distance = min(distance, ground.x);
    return distance;
}

void drtMedium(vec3 p, out vec3 rayleigh, out vec3 mie, out vec3 extinction) {
    float height = max(length(p) - DRT_PLANET, 0.0);
    rayleigh = DRT_RAYLEIGH * exp(-height / 8.0);
    float aerosol = drtScatteringProfile.x * exp(-height / (1.2 * drtScatteringProfile.y));
    mie = DRT_MIE_SCATTER * aerosol;
    float ozone = max(0.0, 1.0 - abs(height - 25.0) / 15.0);
    // Ozone absorbs green most strongly, allowing naturally pink/purple twilight alongside orange aerosols.
    extinction = rayleigh + DRT_MIE_EXTINCT * aerosol
        + vec3(0.000650, 0.001881, 0.000085) * (ozone * drtScatteringProfile.z);
}

vec2 drtTransmittanceUv(vec3 p, vec3 d) {
    return vec2(dot(normalize(p), d) * 0.5 + 0.5,
        sqrt(clamp((length(p) - DRT_PLANET) / (DRT_TOP - DRT_PLANET), 0.0, 1.0)));
}

vec3 drtSunTransmittance(sampler2D table, vec3 p, vec3 d) {
    if (drtSphereRoots(p, d, DRT_PLANET).x > 0.0001) return vec3(0.0);
    return texture(table, drtTransmittanceUv(p, d)).rgb;
}

float drtRayleighPhase(float cosine) {
    return 3.0 * (1.0 + cosine * cosine) / (16.0 * DRT_PI);
}

float drtAerosolPhase(float cosine, float g) {
    float denom = max(1.0 + g * g - 2.0 * g * cosine, 0.0001);
    // Cornette-Shanks Mie phase, normalized over solid angle.
    return 3.0 * (1.0 - g * g) * (1.0 + cosine * cosine) /
        (8.0 * DRT_PI * (2.0 + g * g) * denom * sqrt(denom));
}

float drtMiePhase(float cosine) {
    // Preserve the accepted narrow daytime halo.
    return drtAerosolPhase(cosine, 0.9);
}

vec3 drtSegmentIntegral(vec3 extinction, float distance) {
    return (1.0 - exp(-extinction * distance)) / max(extinction, vec3(0.000001));
}
