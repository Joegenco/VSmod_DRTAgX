#include drtagx_atmosphere_math.ash

// Match the sky LUT's narrow daytime and broader daily twilight aerosol lobes.
float drtVolumetricAerosolPhase(float cosine) {
    float twilight = 1.0 - smoothstep(0.0, 0.25, abs(drtAtmosphereSun.y));
    return mix(drtMiePhase(cosine), drtAerosolPhase(cosine, 0.6),
        drtScatteringFrame.w * twilight);
}

vec3 drtVolumetricGroundRadiance(vec3 rayDir) {
    // Above-horizon fog keeps its accepted lookup and avoids the extra phase work.
    if (rayDir.y >= 0.0) return drtClearSkyRadiance(rayDir);
    // The sky/background LUT repeats the horizon for every negative elevation.
    // It cannot be used unchanged for air in front of downward-facing receivers:
    // its horizon sun halo would become a vertical stripe painted onto terrain.
    float horizontalLength = length(rayDir.xz);
    vec3 reference = vec3(-drtAtmosphereSun.z, 0.0, drtAtmosphereSun.x);
    reference = length(reference) > 0.00001 ? normalize(reference) : vec3(1.0, 0.0, 0.0);
    // Azimuth is undefined at the nadir. Use an off-sun horizon direction there.
    if (rayDir.y < 0.0 && horizontalLength < 0.00001) return drtClearSkyRadiance(reference);
    vec3 radiance = drtClearSkyRadiance(rayDir);

    vec3 horizon = vec3(rayDir.x, 0.0, rayDir.z) / horizontalLength;
    float horizonPhase = drtVolumetricAerosolPhase(dot(horizon, drtAtmosphereSun.xyz));
    float referencePhase = drtVolumetricAerosolPhase(0.0);
    if (horizonPhase <= referencePhase + 0.00001) return radiance;

    float actualPhase = drtVolumetricAerosolPhase(dot(rayDir, drtAtmosphereSun.xyz));
    // Correct only the solar excess above an off-sun background, preserving
    // broad/diffuse haze. The phase ratio is continuous at the horizon and
    // retains the current halo's spectral color, exposure and daily spread.
    vec3 diffuse = drtClearSkyRadiance(reference);
    vec3 solarExcess = max(radiance - diffuse, vec3(0.0));
    float reduction = clamp((horizonPhase - actualPhase) /
        (horizonPhase - referencePhase), 0.0, 1.0);
    return max(radiance - solarExcess * reduction, vec3(0.0));
}

vec3 drtVolumetricSkyRadiance(vec3 rayDir) {
    vec3 radiance = drtVolumetricGroundRadiance(rayDir);
    // A local shaft must not reproduce the sky LUT's entire planetary horizon
    // column as a colored band over receivers. Blend 65% toward diffuse sky at
    // the horizon, fading smoothly to the existing color by +/-15 degrees.
    // The same tint applies to terrain and background rays, avoiding a color
    // discontinuity at skylines. The displayed sky and weather fog are separate.
    float horizonBlend = 0.65 * (1.0 - smoothstep(0.0, sin(radians(15.0)), abs(rayDir.y)));
    if (horizonBlend <= 0.0) return radiance;
    vec3 offSun = vec3(-drtAtmosphereSun.z, 0.0, drtAtmosphereSun.x);
    offSun = length(offSun) > 0.00001 ? normalize(offSun) : vec3(1.0, 0.0, 0.0);
    // Perpendicular azimuth excludes the directional solar halo; a modest
    // positive elevation supplies diffuse chroma without the horizon column.
    vec3 diffuseDir = offSun * cos(radians(15.0)) + vec3(0.0, sin(radians(15.0)), 0.0);
    return mix(radiance, drtClearSkyRadiance(diffuseDir), horizonBlend);
}
