#include drtagx_fog_transport.ash

uniform sampler2D drtSkyViewPrevious;
uniform sampler2D drtSkyViewCurrent;
uniform sampler3D drtFogVolume;

const float DRT_ATMOSPHERE_PI = 3.14159265359;
// Ambient sampling retains the user's current 0.5 -> 3.0 exposure balance.
#define DRT_SKY_EXPOSURE (mix(0.5, 3.0, smoothstep(sin(radians(-6.0)), sin(radians(3.0)), drtAtmosphereSun.y)))
// Only the displayed sky gains one stop. Leave sunset/night and surface E/pi
// unchanged; begin the additional daytime gain above the solar horizon.
#ifdef DRT_AMBIENT_SKY_SAMPLING
#define DRT_SKY_DISPLAY_GAIN 1.0
#else
#define DRT_SKY_DISPLAY_GAIN (mix(1.0, 2.0, smoothstep(0.0, sin(radians(10.0)), drtAtmosphereSun.y)))
#endif

float drtStarVisibility() {
    // Bright stars emerge immediately after sunset, reaching full visibility
    // at civil twilight. Both sky passes share this solar clock.
    return sqrt(max(1.0 - smoothstep(sin(radians(-6.0)), 0.0, drtAtmosphereSun.y), 0.0));
}

vec2 drtSkyCoordinates(vec3 direction) {
    direction = normalize(direction);
    return vec2(atan(direction.z, direction.x) / (2.0 * DRT_ATMOSPHERE_PI) + 0.5,
        asin(clamp(direction.y, -1.0, 1.0)) / DRT_ATMOSPHERE_PI + 0.5);
}

vec3 drtClearSkyRadiance(vec3 direction) {
    if (drtAtmosphereFlags.x < 0.5) return DRT_SKY_EXPOSURE * DRT_SKY_DISPLAY_GAIN * max(drtFogColor.rgb, vec3(0.0));
    vec2 uv = drtSkyCoordinates(direction);
    return max(mix(texture(drtSkyViewPrevious, uv).rgb, texture(drtSkyViewCurrent, uv).rgb,
        drtAtmosphereView.w) * drtAtmosphereFlags.w * DRT_SKY_EXPOSURE * DRT_SKY_DISPLAY_GAIN, vec3(0.0));
}

// Weather uses a fixed physical path, independent of chunk distance and LUT refresh.
float drtAirCelestialVisibility(vec3 direction) {
    if (drtFogColor.w < 0.5) return 1.0;
    vec3 endpoint = normalize(direction) * 8000.0;
    return drtAirTransmittance(vec3(0.0), endpoint, true) * drtWorldFogTransmittance(8000.0);
}

float drtCelestialVisibility(vec3 direction) {
    // Final-stage callers have no reliable water exit depth. Surface sky and
    // sprites use ordered transport when the native liquid pass supplies one.
    return drtAtmosphereView.z > 0.7 ? 0.0 : drtAirCelestialVisibility(direction);
}

// Weather scatter is scene radiance, so it needs the same display exposure and
// chroma as the sky. Native fog RGB remains a linear, unexposed UBO input.
vec3 drtWeatherRadiance(vec3 direction) {
    vec3 color = drtAtmosphereView.z > 0.7 ? drtAirColor.rgb : drtFogColor.rgb;
    vec3 clear = drtClearSkyRadiance(direction);
    float skyLuma = dot(clear, vec3(0.2126, 0.7152, 0.0722));
    float fogLuma = dot(max(color, vec3(0.0)), vec3(0.2126, 0.7152, 0.0722));
    float density = drtAtmosphereView.z > 0.7 ? drtAirDensity.x : drtFogDensity.x;
    float twilight = 1.0 - smoothstep(0.0, 0.5, abs(drtAtmosphereSun.y));
    // Clear-weather haze follows atmospheric chroma during the day as well as
    // twilight. Previously daytime tint vanished, exposing gray native fog at
    // the doubled sky exposure. Retain native fog energy and dense-weather gray.
    float daylight = smoothstep(sin(radians(3.0)), sin(radians(10.0)), drtAtmosphereSun.y);
    float tint = max(1.2 * twilight, 0.95 * daylight) *
        exp(-max(density, 0.0) * 1000.0) * step(0.0001, skyLuma);
    // The artistic tint can extrapolate beyond one; bound negative radiance so
    // the straight-alpha overlay and celestial background remain identical.
    vec3 weather = max(mix(max(color, vec3(0.0)), clear * (fogLuma / max(skyLuma, 0.0001)), tint), vec3(0.0));
    return weather * DRT_SKY_EXPOSURE * DRT_SKY_DISPLAY_GAIN;
}

vec3 drtSkyBackground(vec3 direction) {
    return mix(drtWeatherRadiance(direction), drtClearSkyRadiance(direction), drtAirCelestialVisibility(direction));
}

vec3 drtFogScatterColor(vec3 direction) {
    return drtWeatherRadiance(direction);
}

float drtCumulativeScatterWeight(vec3 p) {
    float t = drtAirTransmittance(vec3(0.0), p, false) * drtWorldFogTransmittance(length(p));
    return 1.0 - t;
}

float drtVolumeVisibility(vec3 start, vec3 end) {
    if (drtAtmosphereFlags.y < 0.5) return 1.0;
    // Project the actual world ray, rather than the invocation's pixel: clouds
    // and sky helpers may evaluate a ray that differs from gl_FragCoord.
    vec3 view = transpose(mat3(drtAtmosphereInvView)) * end;
    vec4 clip = drtAtmosphereProjection * vec4(view, 1.0);
    vec2 uv = clip.xy / max(abs(clip.w), 0.00001) * 0.5 + 0.5;
    float denominator = log(1.0 + drtAtmosphereFlags.z);
    float farSlice = log(1.0 + length(end)) / denominator;
    float farV = texture(drtFogVolume, vec3(uv, clamp(farSlice, 0.0, 1.0))).r;
    if (length(start) < 0.0001) return farV;
    float nearSlice = log(1.0 + length(start)) / denominator;
    float nearV = texture(drtFogVolume, vec3(uv, clamp(nearSlice, 0.0, 1.0))).r;
    float nearW = drtCumulativeScatterWeight(start), farW = drtCumulativeScatterWeight(end);
    // Recover a partial segment's weighted visibility from cumulative samples.
    return farW - nearW > 0.00001 ? clamp((farV * farW - nearV * nearW) / (farW - nearW), 0.0, 1.0) : 1.0;
}

DrtFogTransport drtAirTransport(vec3 start, vec3 end, bool minimumFog, float exposure) {
    float distance = length(end - start);
    if (distance < 0.0001) return drtClearTransport();
    float t = drtAirTransmittance(start, end, minimumFog, exposure);
    // WorldFog is a single bounded extinction term. For split media, use the
    // ratio of endpoint transmittances, so its fade start stays camera-relative.
    t *= drtWorldFogSegmentTransmittance(start, end, exposure);
    vec3 color = drtFogScatterColor(end / max(length(end), 0.0001));
    // Shafts only shade directional contrast. Transmission bounds that contrast
    // so opaque ordinary/minimum/flat/WorldFog converges to the weather sky,
    // even when the optional volume reports complete solar occlusion.
    float density = drtAtmosphereView.z > 0.7 ? drtAirDensity.x : drtFogDensity.x;
    float direct = 0.35 * exp(-max(density, 0.0) * drtAtmosphereView.y) * t;
    color *= 1.0 - direct * (1.0 - drtVolumeVisibility(start, end));
    return DrtFogTransport(vec3(t), color * (1.0 - t));
}

DrtFogTransport drtAirTransport(vec3 start, vec3 end, bool minimumFog) {
    return drtAirTransport(start, end, minimumFog, 1.0);
}

DrtFogTransport drtSurfaceAirTransport(vec3 worldPos, bool boundary, float exposure) {
    if (drtFogColor.w < 0.5) return drtClearTransport();
    vec3 p = worldPos - drtAtmosphereCamera.xyz;
    DrtFogTransport fog = drtAirTransport(vec3(0.0), p, true, exposure);
    if (boundary && length(p) > 0.0001) {
        float t = mix(1.0, drtBoundaryTransmittance(length(p.xz)), exposure);
        fog.transmittance *= t;
        fog.scatter = fog.scatter * t + drtSkyBackground(normalize(p)) * (1.0 - t);
    }
    return fog;
}

DrtFogTransport drtSurfaceAirTransport(vec3 worldPos, bool boundary) {
    return drtSurfaceAirTransport(worldPos, boundary, 1.0);
}

vec4 drtApplyAirFog(vec4 radiance, vec3 worldPos, bool boundary, float sunlight) {
    return drtApplyTransport(radiance, drtSurfaceAirTransport(worldPos, boundary, drtSurfaceFogExposure(worldPos, sunlight)));
}

vec4 drtApplyAirFog(vec4 radiance, vec3 worldPos, bool boundary) {
    return drtApplyTransport(radiance, drtSurfaceAirTransport(worldPos, boundary));
}
