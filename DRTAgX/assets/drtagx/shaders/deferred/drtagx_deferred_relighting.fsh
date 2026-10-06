// Explicit local-light handoff: G-buffer color is voxel blocklight + emission.
void drtDeferredRelight(inout vec3 albedo, inout float blockBright, vec3 rawAlbedo,
    vec3 receiverView, vec3 normalView, vec3 normalWorld, float voxSun, float actualVoxSun, float glowLevel, float grassTag)
{
    drtSkyLight = drtAmbientSky(drtSkyColor);
    // Recover native voxel RGB before either local-light route; emission must
    // remain separate from the placed/voxel blend and its occlusion gate.
    vec3 emission = rawAlbedo * (DRT_EMISSION_GAIN * glowLevel * 2.5);
    vec3 voxel = max(albedo - emission, vec3(0.0));
    blockBright = clamp(dot(voxel / max(rawAlbedo, vec3(0.000001)), DRT_LUMINANCE), 0.0, 0.99);
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
        vec3 voxelLight = voxel / max(rawAlbedo, vec3(0.000001));
        // Voxel light supplies a coarse darkness envelope. All-pass foliage
        // also receives cached depth. One scalar gate preserves the source hue:
        // per-channel gates would extinguish new red/blue light channels.
        float voxelGate = clamp(max(voxelLight.r, max(voxelLight.g, voxelLight.b)) * 32.0, 0.0, 1.0);
        // Shade the cached-source voxel share by the same occlusion in all-pass
        // mode (or facing in legacy mode). Uncovered light and emission remain
        // intact; readiness and cache transitions still blend linearly.
        local = voxel * (1.0 - placed.a * weight * (1.0 - 0.5 * voxelFacing)) +
            rawAlbedo * placed.rgb * (0.5 * weight * voxelGate) + emission;
    } else {
        float emitterPixel;
        vec3 emitterSelfLight;
        // Test the existing compositor envelope before filtering any placed maps.
        // Self-light and debug replacement coverage are independent of this weight.
        vec4 placed = drtPlacedLights(receiverView,
            dot(normalView, normalView) > 1e-6 ? normalWorld : vec3(0.0),
            weight > 0.0, grassTag > 0.5, emitterPixel, emitterSelfLight);
        // Solid and static no-cull receivers share this shadowed maximum blend.
        // Disabled/unready caches keep the full forward voxel energy. Retain the
        // authored 0.5 voxel contribution only while placed maps are available;
        // blend readiness smoothly without changing individual source fades.
        float placedBlend = drtStaticCount > 0 && drtStaticTileWidth > 0
            ? clamp(drtStaticBlend, 0.0, 1.0) : 0.0;
        local = max(voxel * mix(1.0, 0.5, placedBlend), rawAlbedo * placed.rgb * 0.5 * weight) + emission;
        // local = (voxel * mix(1.0, 0.5, placedBlend) + rawAlbedo * placed.rgb * 0.5 * weight) + emission;
        // vec3 local = rawAlbedo * placed.rgb * weight + emission;
        // emitterSelfLight already includes each source's emission-strength boost.
        if (emitterPixel > 0.0) local = max(local, rawAlbedo * emitterSelfLight + emission);
    }
    local += rawAlbedo * drtCurrentFrameLights(receiverView, normalView);
    drtSurfaceSunAccess = actualVoxSun;
    drtSunLight = rawAlbedo * drtSunRadiance(actualVoxSun, drtSkyLight);
    drtLocalLight = local;
    albedo = local + drtSunLight;
}
