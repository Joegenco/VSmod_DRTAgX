// Private scratch transport is enabled only when producer and relight targets
// were prepared together. Default0 retains the prelit diagnostic fixture ABI.
uniform int drtDeferredIlluminationPayload = 0;
vec3 drtDeferredDiffuseLocal = vec3(0.0);
vec3 drtDeferredDiffuseSun = vec3(0.0);
vec3 drtAuthoredReceiverView = vec3(0.0);
vec3 drtUncachedReceiverNormal = vec3(0.0);
// Explicit local-light handoff: G-buffer color is voxel blocklight + emission.
void drtDeferredRelight(inout vec3 albedo, inout float blockBright, vec3 rawAlbedo,
    vec3 receiverView, vec3 normalView, vec3 normalWorld, uint nativeToken,
    float voxSun, float actualVoxSun, float glowLevel, float grassTag)
{
    drtSkyLight = drtAmbientSky(drtSkyColor);
    // Recover native voxel RGB before either local-light route; emission must
    // remain separate from the placed/voxel blend and its occlusion gate.
    vec3 material = drtDeferredIlluminationPayload != 0 ? vec3(1.0) : rawAlbedo;
    vec3 emission = material * (DRT_EMISSION_GAIN * glowLevel * 2.5);
    vec3 voxel = max(albedo - emission, vec3(0.0));
    blockBright = clamp(dot(voxel / max(material, vec3(0.000001)), DRT_LUMINANCE), 0.0, 0.99);
    // Accessible sky alone stays high outdoors at night; use sky radiance too.
    float sunBrightness = pow(voxSun, 1.6) * clamp(dot(drtSkyLight, DRT_LUMINANCE), 0.0, 1.0);
    float weight = clamp(drtPlacedWeight(receiverView, sunBrightness, glowLevel), 0.0, 1.0);
    vec3 local;
    if (grassTag < 0.0) {
        // Wind PLS was evaluated before G-buffer fill at the rest pose. Do not
        // sample its cached depth again using the current wind-deformed depth.
        local = albedo;
    } else if (grassTag > 0.5) {
        // The caller supplies wind foliage, including triangles with fixed
        // roots and moving tips. Static no-cull surfaces use the solid route.
        float voxelFacing = 1.0;
        vec4 placed = vec4(0.0);
        if (weight > 0.0) placed = drtPlacedFoliageLight(receiverView, normalWorld, voxelFacing);
        vec3 voxelLight = voxel / max(material, vec3(0.000001));
        // Voxel light supplies a coarse darkness envelope. All-pass foliage
        // also receives cached depth. One scalar gate preserves the source hue:
        // per-channel gates would extinguish new red/blue light channels.
        float voxelGate = clamp(max(voxelLight.r, max(voxelLight.g, voxelLight.b)) * 32.0, 0.0, 1.0);
        // Shade the cached-source voxel share by the same occlusion in all-pass
        // mode (or facing in legacy mode). Uncovered light and emission remain
        // intact; readiness and cache transitions still blend linearly.
        local = voxel * (1.0 - placed.a * weight * (1.0 - 0.5 * voxelFacing)) +
            material * placed.rgb * (0.5 * weight * voxelGate) + emission;
    } else {
        float emitterPixel;
        vec3 emitterSelfLight;
        // Test the existing compositor envelope before filtering any placed maps.
        // Self-light and debug replacement coverage are independent of this weight.
        vec4 placed = vec4(0.0), uncachedSelf = vec4(0.0);
        // No-cull furniture, rotated quads and custom VBOs cannot certify a
        // static page. Receive completed PLS depth directly on their actual
        // geometry instead of silently keeping unshadowed native blocklight.
        // A certified but cold page stays on the bounded cache preparation path.
        bool uncachedReady = false;
#ifdef DRT_PLACED_RECEIVER_FALLBACK
        uncachedReady = !drtAuthoredPlaneValid && weight > 0.0 &&
            drtReadReceiverPlacedLight(mat3(invModelViewMatrix) * receiverView,
                drtUncachedReceiverNormal, placed, uncachedSelf);
#endif
        if (uncachedReady) {
            drtPlacedMemoAvailable = true;
            emitterPixel = max(uncachedSelf.a,0.0);
            emitterSelfLight = uncachedSelf.rgb;
        } else {
            placed = drtPlacedLights(drtAuthoredPlaneValid ? drtAuthoredReceiverView : receiverView,
                dot(normalView, normalView) > 1e-6 ? normalWorld : vec3(0.0),
                nativeToken, weight > 0.0, grassTag > 0.5, emitterPixel, emitterSelfLight);
        }
        // Replace the covered native blocklight share with placed radiance.
        // Cache RGB and coverage already contain the first-ready fade, so RGB
        // must not be multiplied by coverage again. Sun/emission stay separate;
        // cold/unknown cells and unrelated light outside lamp spheres keep native.
#ifdef DRT_MEMO_ONLY
        float placedBlend = drtPlacedMemoAvailable ? clamp(placed.a, 0.0, 1.0) : 0.0;
        // Every covered PLS area retains half its native voxel contribution,
        // independently of shadow visibility. Sun/emission/moving stay separate.
        local = voxel * (1.0 - 0.5 * placedBlend * weight) +
            material * placed.rgb * weight + emission;
#else
        float placedBlend = drtMemoEnabled != 0 ? (drtPlacedMemoAvailable ? clamp(placed.a, 0.0, 1.0) : 0.0) :
            (drtStaticCount > 0 && drtStaticTileWidth > 0 ? clamp(drtStaticBlend, 0.0, 1.0) : 0.0);
        // Explicit cache-off diagnostics retain the original authored blend.
        if (drtMemoEnabled != 0)
            local = voxel * (1.0 - 0.5 * placedBlend * weight) +
                material * placed.rgb * weight + emission;
        else
            local = max(voxel * mix(1.0, 0.5, placedBlend), material * placed.rgb * 0.5 * weight) + emission;
#endif
        // Compare source/self light before adding voxel, so even emitters keep
        // the half-voxel baseline. Authored emission remains independent.
        if (emitterPixel > 0.0) {
#ifndef DRT_MEMO_ONLY
            if (drtMemoEnabled == 0) local = max(local, material * emitterSelfLight + emission);
            else
#endif
                local = voxel * (1.0 - 0.5 * placedBlend * weight) +
                    material * max(placed.rgb * weight, emitterSelfLight) + emission;
        }
    }
    local += material * drtCurrentFrameLights(receiverView, normalView);
    // Apply the visor minimum using this pixel's actual eye-space position,
    // before material multiplication and outside directional shadows. Decal
    // irradiance receives the same falloff; emission stays independent.
    local = emission + max(local - emission, material * drtMinimumSurfaceLight(receiverView));
    drtSurfaceSunAccess = actualVoxSun;
    drtSunLight = material * drtSunRadiance(actualVoxSun, drtSkyLight);
    if (drtDeferredIlluminationPayload != 0) {
        // Export retained voxel plus selected source/self and moving light,
        // before material multiplication; authored emission is stored separately.
        drtDeferredDiffuseLocal = max(local - emission, vec3(0.0));
        drtDeferredDiffuseSun = drtSunLight;
        local *= rawAlbedo;
        drtSunLight *= rawAlbedo;
    }
    drtLocalLight = local;
    albedo = local + drtSunLight;
}

// Preserve the original entry point for generic/legacy receivers and fixtures.
void drtDeferredRelight(inout vec3 albedo, inout float blockBright, vec3 rawAlbedo,
    vec3 receiverView, vec3 normalView, vec3 normalWorld, float voxSun, float actualVoxSun, float glowLevel, float grassTag)
{
    drtDeferredRelight(albedo, blockBright, rawAlbedo, receiverView, normalView, normalWorld,
        DRT_MEMO_GENERIC_NORMAL, voxSun, actualVoxSun, glowLevel, grassTag);
}
