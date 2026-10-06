// Native declarations are retained because native C# setters require their keys.
uniform float playerToSealevelOffset;
uniform int ditherSeed;
uniform int horizontalResolution;
uniform float fogWaveCounter;
uniform sampler2D glow;
uniform sampler2D sky;
uniform float sunsetMod;
#include drtagx_atmosphere_sampling.fsh

float getFogAmountForSky(vec3 position, vec3 direction, float seaOffset, float horizonFog) {
    return 1.0 - drtCelestialVisibility(direction);
}

float drtSolarExtraction(vec3 direction, vec3 sunDirection, float radiusScale) {
    // This is the sun/godray source mask, not an atmospheric RGB halo. Restrict
    // it to a one-degree neighborhood; Mie + multiple scatter supply the spread.
    float cosine = dot(normalize(direction), normalize(sunDirection));
    float outer = radians(1.0) * max(radiusScale, 0.25);
    float inner = radians(0.27) * max(radiusScale, 0.25);
    return smoothstep(cos(outer), cos(inner), cosine);
}

vec3 drtSkyOverlayRadiance(vec3 background, vec3 direction, float daylight, float alpha) {
    if (drtAtmosphereFlags.x < 0.5 || alpha <= 0.00001) return background;
    // Verified native SrcAlpha/OneMinusSrcAlpha blending: the night base has
    // already drawn clear atmosphere. Solve for the overlay's straight RGB.
    vec3 base = drtClearSkyRadiance(direction);
    return max((background - base * (1.0 - alpha)) / alpha, vec3(0.0));
}

void getSkyColorAt(vec3 position, vec3 sunPosition, float seaOffset, float daylight,
                  float horizonFog, out vec4 skyColor, out vec4 skyGlow) {
    vec3 direction = normalize(position);
    float visibility = drtAirCelestialVisibility(direction);
    vec3 radiance;
    if (drtAtmosphereFlags.x > 0.5) {
        radiance = drtSkyBackground(direction);
    } else {
        // Texture fallback for startup/resource failure; cached atmosphere is
        // otherwise independent of these daytime textures.
        float u = (sunPosition.y + 1.0) * 0.5;
        float v = pow(max(1.0 - direction.y, 0.0), 0.25);
        vec4 halo = texture(glow, vec2(clamp(u - sunsetMod * 2.0, 0.0, 1.0),
            clamp(distance(direction, sunPosition) / 6.0, 0.0, 1.0)));
        vec3 clear = 3.0 * mix(texture(sky, vec2(u, v)).rgb, halo.rgb, halo.a) * max(daylight, 0.0);
        // Startup fallback must approach the same weather radiance as terrain.
        // Retain the native texture sky's separate clear-weather balance.
        radiance = mix(drtWeatherRadiance(direction), DRT_SKY_EXPOSURE * clear, visibility);
    }
    // Stars were drawn at .1. Apply solar visibility and weather exactly once
    // through overlay alpha. Daytime stays opaque even if the native night-sky
    // renderer skips its now-invisible cube draw; clear atmosphere cannot vanish.
    float alpha = drtAtmosphereFlags.x > 0.5 ? 1.0 - visibility * drtStarVisibility()
        : mix(1.0 - visibility, 1.0, clamp(daylight, 0.0, 1.0));
    skyColor = vec4(radiance, alpha);
    skyGlow = vec4(0.0, 0.0, 0.0, 1.0);
#if GODRAYS > 0
    skyGlow.g = drtSolarExtraction(direction, sunPosition, 1.0) * visibility * max(daylight, 0.0);
#endif
}

vec4 getSkyGlowAt(vec3 position, vec3 sunPosition, float seaOffset, float daylight,
                 float horizonFog, float proximityMultiplier) {
    vec3 direction = normalize(position);
    float halo = drtSolarExtraction(direction, sunPosition, inversesqrt(max(proximityMultiplier, 0.25)));
    float visibility = drtCelestialVisibility(direction);
    return vec4(drtClearSkyRadiance(direction), halo * visibility * max(daylight, 0.0));
}
