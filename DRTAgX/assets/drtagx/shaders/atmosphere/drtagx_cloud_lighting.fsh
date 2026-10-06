#include drtagx_atmosphere_sampling.fsh
#include drtagx_celestial_balance.ash

// Solar elevation, not cached cloud RGB or the calendar's delayed night brightness, owns this ramp.
float drtCloudSunlight() {
    return smoothstep(sin(radians(-6.0)), sin(radians(10.0)), drtAtmosphereSun.y);
}

vec3 drtCloudLight() {
    if (drtFogColor.w < 0.5) return vec3(1.0);
    float sunlight = drtCloudSunlight();
    vec3 tint = vec3(1.0);
    if (drtAmbientResolved.w > 0.5) {
        vec3 ambient = max(drtAmbientResolved.rgb, vec3(0.0));
        float y = dot(ambient, vec3(0.2126, 0.7152, 0.0722));
        // Surface daylight gain affects energy, not cloud tint; cloud brightness is independently bounded.
        tint = mix(vec3(1.0), ambient / max(y, 0.00001), 0.55 * step(0.00001, y));
    }
    float moon = max(drtAtmosphereMoon.w, 0.0) * DRT_MOON_GAIN *
        smoothstep(-0.05, 0.1, drtAtmosphereMoon.y);
    return 3.5 * sunlight * tint + vec3(0.005 + 0.5 * moon);
}

float drtCloudDirection(vec3 normal, vec3 viewRay) {
    // A modest sun-facing relief and forward silver lining; moon light stays diffuse.
    float facing = max(dot(normal, drtAtmosphereSun.xyz), 0.0);
    float forward = pow(max(dot(viewRay, drtAtmosphereSun.xyz), 0.0), 8.0);
    return mix(1.0, 0.75 + 0.4 * facing + 0.15 * forward, drtCloudSunlight());
}

vec4 drtCloudThroughFog(vec4 color, DrtFogTransport fog) {
    // Sky/terrain already contain foreground fog. Fade cloud contrast through it,
    // rather than compositing another copy of scatter over the accepted fog gradient.
    float visibility = max(max(fog.transmittance.r, fog.transmittance.g), fog.transmittance.b);
    color.rgb *= fog.transmittance / max(visibility, 0.00001);
    color.a *= visibility;
    return color;
}

vec2 drtCloudInterval(float origin, float direction, vec2 bounds, float segmentLength) {
    // Native map B/A are vertical extents in 50-block tile units; handle horizontal rays without 0/0.
    if (abs(direction) < 0.00001)
        return origin >= bounds.x && origin <= bounds.y ? vec2(0.0, max(segmentLength, 0.0)) : vec2(0.0);
    vec2 hit = (bounds - origin) / direction;
    float near = max(min(hit.x, hit.y), 0.0);
    return vec2(near, max(near, min(max(hit.x, hit.y), max(segmentLength, 0.0))));
}
