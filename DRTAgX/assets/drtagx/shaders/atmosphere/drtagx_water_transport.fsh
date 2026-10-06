// Preserve the native declarations/setter ABI and filtered quarter-size depth.
uniform sampler2D liquidDepth;
uniform float cameraUnderwater;
uniform vec2 frameSize;
uniform vec4 waterMurkColor;

#include drtagx_atmosphere_sampling.fsh

float drtNativeReceiverDepth(vec3 worldPos, float fallbackDepth) {
    // LOD providers compress projected distance to fit the native far clip.
    // Recover the real receiver's native depth before comparing liquid depth,
    // otherwise a short water segment can be stretched over the whole LOD ray.
    vec3 p = worldPos - drtAtmosphereCamera.xyz;
    vec3 view = transpose(mat3(drtAtmosphereInvView)) * p;
    vec4 clip = drtAtmosphereProjection * vec4(view, 1.0);
    return abs(clip.w) > 0.00001 ? 0.5 + 0.5 * clip.z / clip.w : fallbackDepth;
}

float drtFilteredLiquidDepth() {
    // An unavailable native liquid pass is an unknown interface, not depth=0.
    if (drtAtmosphereScreen.w < 0.5) return 1.0;
    return max(texture(liquidDepth, gl_FragCoord.xy / frameSize.xy).r,
        texture(liquidDepth, (gl_FragCoord.xy + vec2(0.0, 3.0)) / frameSize.xy).r);
}

float drtLiquidRayDistance(float depth, float receiverDistance, float receiverDepth) {
    // Inverse projection removes non-linear depth. Distance ratios remain
    // correct off-axis and under the held-item projection.
    vec2 uv = gl_FragCoord.xy / max(frameSize.xy, vec2(1.0));
    vec4 p = drtAtmosphereInvProjection * vec4(uv * 2.0 - 1.0, depth * 2.0 - 1.0, 1.0);
    vec4 r = drtAtmosphereInvProjection * vec4(uv * 2.0 - 1.0, receiverDepth * 2.0 - 1.0, 1.0);
    return receiverDistance * (length(p.xyz / max(abs(p.w), 0.00001)) /
        max(length(r.xyz / max(abs(r.w), 0.00001)), 0.00001));
}

DrtFogTransport drtSkyMediumTransport(vec3 direction) {
    if (drtFogColor.w < 0.5) return drtClearTransport();
    vec3 end = normalize(direction) * 8000.0;
    float depth = drtFilteredLiquidDepth();
    bool submerged = drtAtmosphereView.z > 0.7 || cameraUnderwater > 0.7;
    vec3 murk = submerged ? max(drtFogColor.rgb, vec3(0.0)) : max(waterMurkColor.rgb, vec3(0.0)) * 0.4;
    if (depth >= 0.999999) {
        if (submerged) return drtWaterTransport(8000.0, murk, true);
        float t = drtAirCelestialVisibility(direction);
        return DrtFogTransport(vec3(t), drtWeatherRadiance(direction) * (1.0 - t));
    }
    // A known native exit permits a finite water segment. If it is absent,
    // submerged sky remains conservatively in water, without an invented exit.
    vec2 uv = gl_FragCoord.xy / max(frameSize.xy, vec2(1.0));
    vec4 view = drtAtmosphereInvProjection * vec4(uv * 2.0 - 1.0, depth * 2.0 - 1.0, 1.0);
    float distance = clamp(length(view.xyz / max(abs(view.w), 0.00001)), 0.0, 8000.0);
    vec3 p = normalize(direction) * distance;
    vec3 start = submerged ? p : vec3(0.0), finish = submerged ? end : p;
    float t = drtAirTransmittance(start, finish, !submerged) * drtWorldFogSegmentTransmittance(start, finish);
    // Expose only the air segment; water absorption/murk keeps its native energy.
    DrtFogTransport air = DrtFogTransport(vec3(t), drtWeatherRadiance(direction) * (1.0 - t));
    DrtFogTransport water = drtWaterTransport(submerged ? distance : 8000.0 - distance, murk, submerged);
    return submerged ? drtComposeTransport(water, air) : drtComposeTransport(air, water);
}

// Share the complete ordered air/water segment with cloud contrast visibility.
DrtFogTransport drtSurfaceMediumTransport(vec3 worldPos, float receiverDepth, bool boundary, float sunlight) {
    if (drtFogColor.w < 0.5) return drtClearTransport();
    vec3 p = worldPos - drtAtmosphereCamera.xyz;
    float distance = length(p);
    float exposure = drtSurfaceFogExposure(worldPos, sunlight);
    bool submerged = drtAtmosphereView.z > 0.7 || cameraUnderwater > 0.7;
    float depth = drtFilteredLiquidDepth();
    bool crossed = depth < receiverDepth - 0.000001 && depth < 0.999999;
    vec3 murk = submerged ? max(drtFogColor.rgb, vec3(0.0)) : max(waterMurkColor.rgb, vec3(0.0)) * 0.4;
    DrtFogTransport fog;
    if (!crossed) {
        // No exit/interface is known: retain the camera medium conservatively.
        fog = submerged ? drtWaterTransport(distance, murk, true)
            : drtSurfaceAirTransport(worldPos, boundary, exposure);
    } else {
        float interfaceDistance = clamp(drtLiquidRayDistance(depth, distance, receiverDepth), 0.0, distance);
        vec3 interfacePos = p * (interfaceDistance / max(distance, 0.0001));
        if (submerged) {
            fog = drtComposeTransport(drtWaterTransport(interfaceDistance, murk, true),
                drtAirTransport(interfacePos, p, false, exposure));
        } else {
            fog = drtComposeTransport(drtAirTransport(vec3(0.0), interfacePos, true, exposure),
                drtWaterTransport(distance - interfaceDistance, murk, false));
        }
        if (boundary && exposure > 0.0 && distance > 0.0001) {
            // Shelter masks atmospheric concealment, never water absorption.
            float t = mix(1.0, drtBoundaryTransmittance(length(p.xz)), exposure);
            vec3 background = submerged ? murk : drtSkyBackground(normalize(p));
            fog.transmittance *= t;
            fog.scatter = fog.scatter * t + background * (1.0 - t);
        }
    }
    return fog;
}

DrtFogTransport drtSurfaceMediumTransport(vec3 worldPos, float receiverDepth, bool boundary) {
    return drtSurfaceMediumTransport(worldPos, receiverDepth, boundary, 1.0);
}

vec4 drtApplySurfaceFog(vec4 color, vec3 worldPos, float receiverDepth, bool boundary, float sunlight) {
    if (drtFogColor.w < 0.5) return color;
    DrtFogTransport fog = drtSurfaceMediumTransport(worldPos, receiverDepth, boundary, sunlight);
    vec3 p = worldPos - drtAtmosphereCamera.xyz;
    // Gameplay spheres retain their bounded native coloration after transport.
    vec4 result = drtApplyTransport(color, fog);
    return length(p) > 0.0001 ? applySpheresFog(result, 0.0, p) : result;
}

// Keep full-exposure signatures for sky/clouds and existing third-party callers.
vec4 drtApplySurfaceFog(vec4 color, vec3 worldPos, float receiverDepth, bool boundary) {
    return drtApplySurfaceFog(color, worldPos, receiverDepth, boundary, 1.0);
}

// Keep native signatures for unmodified third-party callers. Updated paths
// compose media above and do not use the legacy linear color mixture.
float getUnderwaterMurkiness() {
    if (cameraUnderwater > 0.7) return 0.0;
    float depth = drtFilteredLiquidDepth();
    float delta = max((2.0 * zNear * zFar / max(zFar + zNear - (2.0 * gl_FragCoord.z - 1.0) * (zFar - zNear), 0.00001)) - (2.0 * zNear * zFar / max(zFar + zNear - (2.0 * depth - 1.0) * (zFar - zNear), 0.00001)), 0.0);
    return 1.0 - exp(-0.055 * delta);
}

float getSkyMurkiness() {
    if (cameraUnderwater > 0.7) return 1.0;
    return drtFilteredLiquidDepth() < 0.999999 ? 1.0 : 0.0;
}

vec3 applyUnderwaterEffects(vec3 color, float murkiness) {
    float t = 1.0 - clamp(murkiness, 0.0, 1.0);
    return color * t + waterMurkColor.rgb * 0.4 * (1.0 - t);
}
