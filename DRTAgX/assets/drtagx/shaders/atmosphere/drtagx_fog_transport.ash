// Shared vertex/fragment ABI. Distances are blocks (one block = one meter).
// UBO binding is assigned by C#, so native GLSL 330 variants remain valid.
#ifndef DRT_FOG_TRANSPORT
#define DRT_FOG_TRANSPORT 1
// Keep native Sheyder setters live in surface programs; programs without those
// setters use the once-per-frame shared values captured from opaque terrain.
uniform float wf_Density;
uniform float wf_Intensity = 1.0;
uniform float wf_FadeStart;
uniform float wf_FallOff;
layout(std140) uniform DrtAtmosphere {
    vec4 drtFogColor;       // native blended radiance; w = world transport enabled
    vec4 drtFogDensity;     // ordinary, minimum opacity, signed flat density, plane Y
    vec4 drtAtmosphereView; // approved distance, weather scale height, underwater, LUT blend
    vec4 drtAtmosphereSun;  // native normalized direction, strength
    vec4 drtAtmosphereMoon; // native normalized direction, strength
    vec4 drtAtmosphereCamera; // render-origin camera offset, altitude above sea level
    vec4 drtAtmosphereFlags;  // LUT ready, volume ready, volume far distance, sky normalization
    vec4 drtAtmosphereWorldFog; // density, intensity, fade start, falloff
    vec4 drtAtmosphereScreen; // viewport width/height; z=replacement valid, w=liquid depth valid
    mat4 drtAtmosphereInvProjection;
    mat4 drtAtmosphereInvView;
    vec4 drtAirColor;       // Last dry native blend; public base if starting underwater.
    vec4 drtAirDensity;     // Dry ordinary/minimum/signed flat/plane, for water exits.
    mat4 drtAtmosphereProjection; // Uploaded once; no per-fragment matrix inversion.
    vec4 drtAmbientScale;   // Ordered overlay baseline coefficient; w = verified native replay.
    vec4 drtAmbientOverlay; // Ordered native color additions, already scaled by retained scene brightness.
    vec4 drtAmbientNative;  // Native blended RGB reference; w = current cloud density.
    vec4 drtAmbientResolved; // GPU E/pi plus solar-region chroma; w = same-frame result ready.
    vec4 drtScatteringFrame; // Native daily aerosol density/height, ozone and twilight spread.
    vec4 drtSunGridFrame;    // Fractional absolute camera xyz, w = grid enabled and phase valid.
    vec4 drtFogEnvironment;  // Live sunlight threshold, camera exposure, optional LOD endpoint, valid snapshot.
    vec4 drtRoomMin;         // Camera-relative inclusive room corner; w = eased shelter weight 0..1.
    vec4 drtRoomMax;         // Exclusive corner; w = rectangular (skip voxel traversal).
    uvec4 drtRoomVoxels[192]; // Native room bitset: X fastest, then Z, then Y. At most 29^3 cells.
};

// A 0.001% opacity floor keeps terrain fog metadata positive even at slider zero
// or zero sunlight, including when exp(-opticalDepth) rounds to exactly one.
const float DRT_MIN_FOG_OPACITY = 0.00001;

struct DrtFogTransport { vec3 transmittance; vec3 scatter; };

DrtFogTransport drtClearTransport() {
    return DrtFogTransport(vec3(1.0), vec3(0.0));
}

// Native rooms are cached on the CPU. Ordinary rectangular rooms need only a
// ray/box exit; the exact bitset prevents concave rooms from clearing outdoor
// air in their bounding-box corners. This adds no texture or render pass.
bool drtRoomContainsCell(ivec3 cell, ivec3 size) {
    if (any(lessThan(cell, ivec3(0))) || any(greaterThanEqual(cell, size))) return false;
    int index = (cell.y * size.z + cell.z) * size.x + cell.x;
    uint bits = drtRoomVoxels[index >> 7][(index >> 5) & 3];
    return (bits & (1u << uint(index & 31))) != 0u;
}

float drtRoomExitDistance(vec3 endpoint) {
    float distance = length(endpoint);
    if (distance < 0.0001) return 0.0;
    vec3 direction = endpoint / distance;
    // Parallel axes have no exit. Avoid 0*infinity at a wall/voxel boundary.
    vec3 inverse = mix(vec3(-1.0), vec3(1.0), greaterThanEqual(direction, vec3(0.0)))
        / max(abs(direction), vec3(0.0000001));
    vec3 farAxis = max(drtRoomMin.xyz * inverse, drtRoomMax.xyz * inverse);
    farAxis = mix(farAxis, vec3(1e20), lessThan(abs(direction), vec3(0.0000001)));
    float boxExit = max(0.0, min(farAxis.x, min(farAxis.y, farAxis.z)));
    // Three centimetres includes the visible inside face at the native air-cell
    // boundary without introducing a haze seam on walls, doors or windows.
    if (drtRoomMax.w > 0.5) return boxExit + 0.03;

    ivec3 size = ivec3(floor(drtRoomMax.xyz - drtRoomMin.xyz + vec3(0.5)));
    ivec3 cell = ivec3(floor(-drtRoomMin.xyz));
    if (!drtRoomContainsCell(cell, size)) return 0.0;
    ivec3 stepCell = ivec3(sign(direction));
    vec3 nextBoundary = drtRoomMin.xyz + vec3(cell) + step(vec3(0.0), direction);
    vec3 nextT = nextBoundary * inverse;
    nextT = mix(nextT, vec3(1e20), equal(stepCell, ivec3(0)));
    vec3 deltaT = abs(inverse);
    float limit = min(distance, boxExit);
    // A native room fits a 29-cell span on each axis: a straight ray crosses
    // fewer than 87 cell boundaries. Only irregular rooms take this path.
    for (int i = 0; i < 90; ++i) {
        float next = min(nextT.x, min(nextT.y, nextT.z));
        if (next >= limit) return limit + 0.03;
        bvec3 crossed = lessThanEqual(nextT, vec3(next + 0.000001));
        cell += ivec3(crossed) * stepCell;
        nextT += vec3(crossed) * deltaT;
        if (!drtRoomContainsCell(cell, size)) return max(next, 0.0) + 0.03;
    }
    return 0.0; // An invalid mask must never erase outdoor weather.
}

// Camera-collinear air segment: retain only the portion beyond the current
// sheltered room. A closed room is clear, while the view through its window
// still has the correct exterior path length, independent of voxel sunlight.
vec2 drtFogOutdoorInterval(vec3 start, vec3 end) {
    if (drtRoomMin.w <= 0.0) return vec2(0.0, 1.0);
    float distance = length(end - start);
    if (distance < 0.0001) return vec2(1.0);
    float clearDistance = drtRoomExitDistance(end);
    float from = clamp((clearDistance - length(start)) / distance, 0.0, 1.0);
    return vec2(from, 1.0);
}

float drtRoomBoundaryExposure(vec3 endpoint, float exposure) {
    float weight = clamp(drtRoomMin.w, 0.0, 1.0);
    if (weight <= 0.0) return exposure;
    // Keep distant concealment outside a window. Only a ray wholly inside the
    // room loses its boundary contribution, over the same two-second fade.
    float target = drtFogOutdoorInterval(vec3(0.0), endpoint).x < 1.0 ? 1.0 : 0.0;
    return mix(exposure, target, weight);
}

// Near segment A followed by far segment B: camera receives A.S + A.T * B.S.
DrtFogTransport drtComposeTransport(DrtFogTransport a, DrtFogTransport b) {
    return DrtFogTransport(a.transmittance * b.transmittance,
        a.scatter + a.transmittance * b.scatter);
}

vec4 drtApplyTransport(vec4 radiance, DrtFogTransport fog) {
    return vec4(radiance.rgb * fog.transmittance + fog.scatter, radiance.a);
}

// Stable integral of exp(-height/H) along a straight segment. The density is
// anchored at camera height, rather than changing the native weather strength.
float drtHeightIntegral(float distance, float startY, float endY, float scaleHeight) {
    float x = clamp((endY - startY) / max(scaleHeight, 1.0), -40.0, 40.0);
    float average = abs(x) < 0.001 ? 1.0 - 0.5 * x + x * x / 6.0
        : (1.0 - exp(-x)) / x;
    return max(distance, 0.0) * exp(clamp(-startY / max(scaleHeight, 1.0), -40.0, 40.0)) * average;
}

// Integral of positive height penetration; sign selects native upper/lower fog.
float drtFlatIntegral(float distance, float startY, float endY, float plane, float density) {
    float a = (startY - plane) * sign(density);
    float b = (endY - plane) * sign(density);
    float average;
    if (a >= 0.0 && b >= 0.0) average = 0.5 * (a + b);
    else if (a <= 0.0 && b <= 0.0) average = 0.0;
    else average = 0.5 * max(a, b) * max(a, b) / max(abs(b - a), 0.0001);
    return abs(density) * max(distance, 0.0) * average / 60.0;
}

float drtAirOpticalDepth(vec3 start, vec3 end) {
    float distance = length(end - start);
    vec4 density = drtAtmosphereView.z > 0.7 ? drtAirDensity : drtFogDensity;
    // Halve light haze (native rho <= .005), then smoothly return to the
    // approved native strength at .020, the strong-haze preset's mean density.
    // The gain only rises with density, so stronger weather cannot get clearer.
    // Apply it to ordinary haze alone: mist height/penetration, water, and the
    // optional WorldFog envelope retain their own units and existing balance.
    float ordinary = max(density.x, 0.0);
    ordinary *= mix(0.5, 1.0, smoothstep(0.005, 0.020, ordinary));
    // Keep the height integral for the sky and continuous air/water segments.
    // For horizontal mist this is exactly the native extra-distance term:
    // max(-flatDensity * relativePlane, 0) * distance / 60.
    return ordinary * drtHeightIntegral(distance, start.y, end.y, drtAtmosphereView.y)
        + drtFlatIntegral(distance, start.y, end.y, density.w, density.z);
}

float drtFogExposure(float sunlight) {
    // Sunlight is normalized live-table radiance, never an sRGB color or raw
    // level/31 fraction. This fallback protects caves outside detected rooms.
    float full = drtFogEnvironment.w > 0.5 ? max(drtFogEnvironment.x, 0.000001) : 1.0;
    return smoothstep(0.0, full, max(sunlight, 0.0));
}

float drtSurfaceFogExposure(vec3 worldPos, float sunlight) {
    // Sunlight describes the local air near each endpoint, not the entire ray.
    // Clearing the whole column for a dark face punched holes in distant fog,
    // especially when the camera was also shaded. Bound both sheltered lengths
    // instead; an enclosed room still clears its exact interior in transport.
    float local = drtFogExposure(sunlight);
    float camera = drtFogEnvironment.w > 0.5 ? clamp(drtFogEnvironment.y, 0.0, 1.0) : 0.0;
    float distance = length(worldPos - drtAtmosphereCamera.xyz);
    float cameraLength = min(distance, 16.0), surfaceLength = min(distance, 8.0);
    float cameraShelter = 1.0 - camera, surfaceShelter = 1.0 - local;
    // The union of the two clear intervals: overlap uses the stronger shelter
    // once. Fully lit rays are unchanged; two dark endpoints clear at most 24m.
    float overlap = max(cameraLength + surfaceLength - distance, 0.0);
    float clearLength = cameraShelter * cameraLength + surfaceShelter * surfaceLength
        - min(cameraShelter, surfaceShelter) * overlap;
    return clamp(1.0 - clearLength / max(distance, 0.0001), 0.0, 1.0);
}

// Cloud fragment programs extend horizon concealment without changing physical
// weather/WorldFog distances or the terrain programs' default boundary.
#ifndef DRT_BOUNDARY_DISTANCE_SCALE
#define DRT_BOUNDARY_DISTANCE_SCALE 1.0
#endif

float drtBoundaryEnd() {
    float distance = drtAtmosphereView.x;
    return DRT_BOUNDARY_DISTANCE_SCALE * (drtFogEnvironment.z > distance ? drtFogEnvironment.z : 1.05 * distance);
}

float drtBoundaryTransmittance(float horizontalDistance) {
    float distance = drtAtmosphereView.x * DRT_BOUNDARY_DISTANCE_SCALE;
    if (distance <= 0.0) return 1.0;
    // Keep the current 66% start. A supported enabled LOD provider extends only
    // the end; physical weather and the fixed sky path keep their own distances.
    float t;
    if (drtFogEnvironment.z > drtAtmosphereView.x)
        t = 1.0 - smoothstep(0.66 * distance, drtBoundaryEnd(), horizontalDistance);
    else t = 1.0 - smoothstep(0.66 * distance, 1.05 * distance, horizontalDistance);
    float squared = t * t;
    return squared * squared;
}

float drtWorldFogTransmittance(float distance) {
    vec4 wf = wf_Density > 0.0 ? vec4(wf_Density, wf_Intensity, wf_FadeStart, wf_FallOff) : drtAtmosphereWorldFog;
    float opacity = wf.x * wf.y *
        (1.0 - exp(-max(wf.w, 0.0) * max(distance - wf.z, 0.0)));
    // This optional envelope owns its strength. Ordinary weather must not
    // silently rescale it when a different haze preset becomes active.
    return 1.0 - clamp(opacity, 0.0, 1.0);
}

// Endpoint ratios keep WorldFog's camera-relative fade continuous across media
// segments, and supply the same extinction weights to the shaft integration.
float drtWorldFogSegmentTransmittance(vec3 start, vec3 end) {
    float a = drtWorldFogTransmittance(length(start));
    float b = drtWorldFogTransmittance(length(end));
    return clamp(b / max(a, 0.00001), 0.0, 1.0);
}

float drtAirTransmittance(vec3 start, vec3 end, bool minimumFog, float exposure) {
    if (length(end - start) == 0.0) return 1.0;
    // Reduce optical depth before exponentiation so sheltered surfaces retain
    // contrast even in dense valley fog. Apply the tiny floor after shelter,
    // so zero sunlight cannot leak substantial weather fog into caves.
    float t = exp(-min(drtAirOpticalDepth(start, end) * exposure, 80.0));
    if (minimumFog && length(end - start) > 0.0001)
        t *= 1.0 - exposure * clamp(drtAtmosphereView.z > 0.7 ? drtAirDensity.y : drtFogDensity.y, 0.0, 1.0);
    return min(t, 1.0 - DRT_MIN_FOG_OPACITY);
}

float drtAirTransmittance(vec3 start, vec3 end, bool minimumFog) {
    return drtAirTransmittance(start, end, minimumFog, 1.0);
}

float drtWorldFogSegmentTransmittance(vec3 start, vec3 end, float exposure) {
    if (exposure <= 0.0) return 1.0; // Avoid undefined pow(0,0) for opaque WorldFog.
    return pow(drtWorldFogSegmentTransmittance(start, end), exposure);
}

float drtWeatherOpticalDepth(vec3 p, bool minimumFog) {
    float distance = length(p);
    if (distance < 0.0001) return 0.0;
    float tau = drtAirOpticalDepth(vec3(0.0), p)
        - log(max(drtWorldFogTransmittance(distance), 0.000001));
    if (minimumFog) {
        vec4 density = drtAtmosphereView.z > 0.7 ? drtAirDensity : drtFogDensity;
        float opacity = clamp(density.y, 0.0, 1.0);
        tau -= log(max(1.0 - opacity, 0.000001));
    }
    // Native weather strength is not globally capped: that workaround hid
    // the misplaced mist layer while also flattening strong haze presets.
    return max(tau, 0.0);
}

float drtWeatherSegmentOpticalDepth(vec3 start, vec3 end, bool minimumFog) {
    // Difference of cumulative columns preserves exact camera-ray composition
    // through water interfaces and froxel slices; per-segment clamps do not.
    return max(drtWeatherOpticalDepth(end, minimumFog)
        - drtWeatherOpticalDepth(start, minimumFog), 0.0);
}

float drtRoomWeatherOpticalDepth(vec3 start, vec3 end, bool minimumFog, float exposure) {
    float weight = clamp(drtRoomMin.w, 0.0, 1.0);
    if (weight <= 0.0)
        return drtWeatherSegmentOpticalDepth(start, end, minimumFog) * max(exposure, 0.0);
    vec2 outdoor = drtFogOutdoorInterval(start, end);
    float target = 0.0;
    if (outdoor.x < outdoor.y) {
        vec3 a = mix(start, end, outdoor.x), b = mix(start, end, outdoor.y);
        // Integrating only the outdoor interval keeps the room's clear path
        // out of the fog column without changing the native weather strength.
        target = drtWeatherSegmentOpticalDepth(a, b, minimumFog);
    }
    if (weight >= 1.0) return target;
    // Fade optical depth, not the room exit distance: a nearby wall must take
    // the entire transition to clear, just like the far side of a large room.
    float original = drtWeatherSegmentOpticalDepth(start, end, minimumFog) * max(exposure, 0.0);
    return mix(original, target, weight);
}

float drtRoomWeatherTransmittance(vec3 start, vec3 end, bool minimumFog, float exposure) {
    if (length(end - start) < 0.0001) return 1.0;
    float tau = drtRoomWeatherOpticalDepth(start, end, minimumFog, exposure);
    // Exactly clear interiors stay clear. Required positive vertex-fog metadata
    // is supplied by drtFogOpacity, independently of visible air transport.
    return exp(-min(tau, 80.0));
}

float drtWeatherTransmittance(vec3 start, vec3 end, bool minimumFog, float exposure) {
    if (length(end - start) < 0.0001) return 1.0;
    float tau = drtWeatherSegmentOpticalDepth(start, end, minimumFog);
    return min(exp(-min(tau * max(exposure, 0.0), 80.0)), 1.0 - DRT_MIN_FOG_OPACITY);
}

float drtWeatherTransmittance(vec3 start, vec3 end, bool minimumFog) {
    return drtWeatherTransmittance(start, end, minimumFog, 1.0);
}

// Used by vertex-only native paths and fog metadata, never to fog packed buffers.
float drtFogOpacity(vec3 worldPos, bool boundary, float sunlight) {
    if (drtFogColor.w < 0.5) return DRT_MIN_FOG_OPACITY;
    vec3 p = worldPos - drtAtmosphereCamera.xyz;
    float exposure = drtSurfaceFogExposure(worldPos, sunlight);
    float t = drtRoomWeatherTransmittance(vec3(0.0), p, true, exposure);
    if (boundary) {
        float boundaryT = drtBoundaryTransmittance(length(p.xz));
        // Nearby room rays never reach horizon concealment. Avoid traversing
        // an irregular room a second time when its boundary factor is identity.
        if (boundaryT < 1.0) t *= mix(1.0, boundaryT, drtRoomBoundaryExposure(p, exposure));
    }
    return max(DRT_MIN_FOG_OPACITY, 1.0 - t);
}

float drtFogOpacity(vec3 worldPos, bool boundary) {
    return drtFogOpacity(worldPos, boundary, 1.0);
}

DrtFogTransport drtWaterTransport(float distance, vec3 murkColor, bool nativeMinimum) {
    // Wavelength-dependent Beer-Lambert absorption: red disappears first.
    vec3 extinction = vec3(0.12, 0.055, 0.025);
    if (nativeMinimum) extinction += vec3(max(drtFogDensity.x, 0.0));
    vec3 t = exp(-extinction * max(distance, 0.0));
    if (nativeMinimum && distance > 0.0001) t *= 1.0 - clamp(drtFogDensity.y, 0.0, 1.0);
    return DrtFogTransport(t, max(murkColor, vec3(0.0)) * (1.0 - t));
}
#endif // DRT_FOG_TRANSPORT
