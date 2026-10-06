// Included after fogandlight: reuse its existing light-balance functions/UBO.
#ifndef DRT_LOD_LIGHTING
#define DRT_LOD_LIGHTING 1

void drtDiscardLodNearBoundary(vec3 worldPos) {
    // Protect the first 75% of the native terrain radius, in blocks. Use the
    // approved native range, never the extended LOD fog endpoint or compressed Z.
    bool snapshotReady = drtFogColor.w > 0.5;
    float nativeRange = snapshotReady ? drtAtmosphereView.x : viewDistance;
    vec2 offset = worldPos.xz - (snapshotReady ? drtAtmosphereCamera.xz : vec2(0.0));
    float cutoff = 0.75 * max(nativeRange, 0.0);
    // Discard before gap-fill and depth-only returns: rejected proxies must
    // leave native object color, depth and every auxiliary attachment intact.
    if (dot(offset, offset) < cutoff * cutoff) discard;
}

vec3 drtLodMaterialAlbedo(vec3 materialColor) {
    // Keep the provider's existing atlas/tint/snow material handling. The real
    // terrain shader also lights its mapped atlas result directly; an extra
    // transfer curve here would change material brightness at the handoff.
    return max(materialColor, vec3(0.0));
}

vec3 drtLodSkyLight() {
    // LOD meshes have no voxel light channels. Their exposed outdoor surfaces
    // use the same full-access diffuse sky, native overlay replay and fallback
    // as real terrain. Never apply a second provider dayLight/sunColor bias.
    return drtSunRadiance(1.0, drtAmbientSky(drtAmbientNative.rgb));
}

float drtLodSunShadowDarkness() {
    return DRT_SUN_SHADOW_GAIN * clamp(drtAtmosphereSun.w, 0.0, 1.0);
}

float drtLodFaceShade(vec3 normalWorld, vec3 lightDirection, float shadowDarkness) {
    float n2 = dot(normalWorld, normalWorld), l2 = dot(lightDirection, lightDirection);
    if (n2 < 1e-8 || l2 < 1e-8) return 1.0;
    vec3 n = normalWorld * inversesqrt(n2);
    float facing = dot(n, lightDirection * inversesqrt(l2));
    // Match the shared terrain normal response and grazing-face shadow style.
    float nativeBrightness = max(max(0.45, 0.5 + 0.5 * facing), n.y * 0.95);
    return max(0.45, drtSunFaceBrightness(nativeBrightness, facing, shadowDarkness));
}
#endif
