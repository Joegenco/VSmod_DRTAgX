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
    vec4 drtFogEnvironment;  // Level-one table radiance, camera shelter, optional LOD endpoint, valid snapshot.
};

const float DRT_AIR_FOG_STRENGTH = 1.0 / 2.0;
// A 0.001% opacity floor keeps terrain fog metadata positive even at slider zero
// or zero sunlight, including when exp(-opticalDepth) rounds to exactly one.
const float DRT_MIN_FOG_OPACITY = 0.00001;

struct DrtFogTransport { vec3 transmittance; vec3 scatter; };

DrtFogTransport drtClearTransport() {
    return DrtFogTransport(vec3(1.0), vec3(0.0));
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

float drtAirFogStrength(float density) {
    // Double low-weather extinction and strengthen medium fog smoothly.
    // The lift reaches zero at rho=.05, preserving the accepted heavy-fog curve.
    return DRT_AIR_FOG_STRENGTH * mix(1.8, 0.4, smoothstep(0.01, 0.05, max(density, 0.0)));
}

float drtFlatFogStrength(float density) {
    // Flat fog also grows with penetration below/above its signed plane.
    // Give it its own response: a quarter of the previous flat extinction,
    // without inheriting the increase to low ordinary weather fog.
    return 0.25 * DRT_AIR_FOG_STRENGTH * mix(0.2, 1.0, smoothstep(0.01, 0.05, max(density, 0.0)));
}

float drtAirOpticalDepth(vec3 start, vec3 end) {
    float distance = length(end - start);
    vec4 density = drtAtmosphereView.z > 0.7 ? drtAirDensity : drtFogDensity;
    // Remap density, not distance: weather still attenuates from the camera.
    // Retain the authored linear/quadratic curve. drtAirFogStrength applies
    // the current density-dependent gain; the signed flat term is independent.
    float ordinary = max(density.x, 0.0);
    ordinary *= drtAirFogStrength(ordinary) * (0.05 + 7.23933 * ordinary);
    float flatDensity = abs(density.z);
    flatDensity *= drtFlatFogStrength(flatDensity) * (0.05 + 7.23933 * flatDensity);
    return ordinary * drtHeightIntegral(distance, start.y, end.y, drtAtmosphereView.y)
        + drtFlatIntegral(distance, start.y, end.y, density.w, sign(density.z) * flatDensity);
}

float drtFogExposure(float sunlight) {
    // Sunlight is normalized live-table radiance, never an sRGB color or raw
    // level/31 fraction. C# publishes the actual table value for level one.
    float full = drtFogEnvironment.w > 0.5 ? max(drtFogEnvironment.x, 0.000001) : 1.0;
    return smoothstep(0.0, full, max(sunlight, 0.0));
}

float drtSurfaceFogExposure(vec3 worldPos, float sunlight) {
    float local = drtFogExposure(sunlight);
    float camera = drtFogEnvironment.w > 0.5 ? clamp(drtFogEnvironment.y, 0.0, 1.0) : 0.0;
    // A distant dark face still has outdoor air in front of it. Restore that
    // camera exposure over 8..48 blocks instead of cutting holes in the haze.
    // Both endpoints sheltered retains cave protection at every distance.
    float outdoorPath = smoothstep(8.0, 48.0, length(worldPos - drtAtmosphereCamera.xyz));
    return max(local, camera * outdoorPath);
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
    // This separate Sheyder haze previously bypassed the air-density rebalance.
    float density = drtAtmosphereView.z > 0.7 ? drtAirDensity.x : drtFogDensity.x;
    return 1.0 - drtAirFogStrength(density) * clamp(opacity, 0.0, 1.0);
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
        t *= 1.0 - exposure * drtAirFogStrength(drtAtmosphereView.z > 0.7 ? drtAirDensity.x : drtFogDensity.x)
            * clamp(drtAtmosphereView.z > 0.7 ? drtAirDensity.y : drtFogDensity.y, 0.0, 1.0);
    return min(t, 1.0 - DRT_MIN_FOG_OPACITY);
}

float drtAirTransmittance(vec3 start, vec3 end, bool minimumFog) {
    return drtAirTransmittance(start, end, minimumFog, 1.0);
}

float drtWorldFogSegmentTransmittance(vec3 start, vec3 end, float exposure) {
    if (exposure <= 0.0) return 1.0; // Avoid undefined pow(0,0) for opaque WorldFog.
    return pow(drtWorldFogSegmentTransmittance(start, end), exposure);
}

// Used by vertex-only native paths and fog metadata, never to fog packed buffers.
float drtFogOpacity(vec3 worldPos, bool boundary, float sunlight) {
    if (drtFogColor.w < 0.5) return DRT_MIN_FOG_OPACITY;
    vec3 p = worldPos - drtAtmosphereCamera.xyz;
    float exposure = drtSurfaceFogExposure(worldPos, sunlight);
    float t = drtAirTransmittance(vec3(0.0), p, true, exposure)
        * drtWorldFogSegmentTransmittance(vec3(0.0), p, exposure);
    if (boundary) t *= mix(1.0, drtBoundaryTransmittance(length(p.xz)), exposure);
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
