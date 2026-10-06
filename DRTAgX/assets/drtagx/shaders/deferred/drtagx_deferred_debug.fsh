// DRTAgX deferred include: Placed-light status, tile occupancy, visibility and point-light diagnostic views.

int drtStaticTileCount(int baseIndex)
{
    int count = 0;
    for (int word = 0; word < 5; ++word) count += bitCount(drtStaticTileData[baseIndex + word]);
    return count;
}

// Debug-only nearest candidate: x=shadow visibility, y=range coverage,
// z=one when a source reaches this pixel. The normal lighting path is unchanged.
vec3 drtPlacedDebugSample(vec3 receiverView, vec3 normalView)
{
    if (drtStaticCount <= 0 || drtStaticTileWidth <= 0) return vec3(0.0);
    ivec2 tile = ivec2(gl_FragCoord.xy) / 16;
    int baseIndex = (tile.y * drtStaticTileWidth + tile.x) * TILE_STRIDE;
    int word = 0;
    uint remaining = drtStaticTileData[baseIndex];
    float closest = 1e20;
    int chosen = -1;
    for (int index = drtNextStaticLight(baseIndex, word, remaining); index >= 0;
         index = drtNextStaticLight(baseIndex, word, remaining)) {
        if (index < 0 || index >= drtStaticCount) continue;
        vec4 source = drtStaticSourceData[index * 4];
        float distanceSquared = dot(source.xyz - receiverView, source.xyz - receiverView);
        if (distanceSquared < source.w * source.w && distanceSquared < closest) {
            closest = distanceSquared;
            chosen = index;
        }
    }
    if (chosen < 0) return vec3(0.0);
    vec4 source = drtStaticSourceData[chosen * 4];
    int slot = int(drtStaticSourceData[chosen * 4 + 1].w);
    vec4 transition = drtStaticSourceData[chosen * 4 + 2];
    float shadowBlend = transition.y;
    float distance = sqrt(closest);
    vec3 toEmitter = (source.xyz - receiverView) / max(distance, 1e-4);
    bool hasNormal = dot(normalView, normalView) > 1e-6;
    vec3 normal = hasNormal ? normalize(normalView) : toEmitter;
    // Match the prepared world-space inputs used by the production light loop.
    mat3 invMV = mat3(invModelViewMatrix);
    float coverage = 1.0 - smoothstep(source.w * 0.82, source.w, distance);
    vec3 rotatedReceiver = invMV * receiverView;
    vec3 receiverWorld = rotatedReceiver - drtStaticSourceData[chosen * 4 + 3].xyz;
    vec3 normalWorld = normalize(invMV * normal);
    // Visibility diagnostics use the same world-anchored cells as relighting.
    if (hasNormal)
        if (drtShadowGridEnabled != 0)
            receiverWorld += drtStaticGridOffset(rotatedReceiver - drtStaticSourceData[3].xyz, normalWorld);
    return vec3(drtStaticVisibility(receiverWorld, normalWorld,
        length(receiverWorld), dot(normalWorld, receiverWorld),
        slot, int(transition.z), shadowBlend), coverage, 1.0);
}

vec3 drtStaticStatusColor()
{
    if (drtStaticDebugState == 0) return vec3(0.45);             // menu switch off
    if (drtStaticDebugState == 1) return vec3(0.1, 0.25, 1.0);   // scanning
    if (drtStaticDebugState == 2) return vec3(1.0, 0.45, 0.0);   // no sources found
    if (drtStaticDebugState == 3) return vec3(1.0, 0.0, 1.0);    // legacy overflow status
    if (drtStaticDebugState == 4) return vec3(1.0, 0.9, 0.0);    // cache incomplete
    if (drtStaticDebugState == 5) return vec3(0.6, 0.0, 0.8);    // GPU bindings absent
    if (drtStaticDebugState == 6) return vec3(0.0, 0.8, 0.15);   // ready
    if (drtStaticDebugState == 9) return vec3(0.0, 0.8, 0.8);    // ready, view candidates exceed resident budget
    return vec3(1.0, 0.0, 0.0);                                  // setup failure
}

vec3 drtDeferredDebugColor(vec3 receiverView, vec3 normalView, float voxelSun, float glowLevel)
{
    if (drtDebugView >= 4) {
        vec3 debugColor = drtStaticStatusColor();
        if (drtDebugView == 4) {
            // Four bottom bars show scan progress, found sources, selected
            // sources, and valid cached maps. Counts follow resident/published capacity.
            float y = gl_FragCoord.y;
            float fraction = gl_FragCoord.x / frameSize.x;
            if (y < 12.0) debugColor = fraction < drtStaticDebugCounts.x ? vec3(0.1, 0.4, 1.0) : vec3(0.04);
            else if (y < 24.0) debugColor = fraction < drtStaticDebugCounts.y / 128.0 ? vec3(1.0, 0.2, 0.1) : vec3(0.04);
            else if (y < 36.0) debugColor = fraction < drtStaticDebugCounts.z / 129.0 ? vec3(0.1, 1.0, 0.3) : vec3(0.04);
            else if (y < 48.0) debugColor = fraction < drtStaticDebugCounts.w / 128.0 ? vec3(1.0, 0.9, 0.1) : vec3(0.04);
        } else if ((drtStaticDebugState == 6 || drtStaticDebugState == 9) &&
                   drtStaticCount > 0 && drtStaticTileWidth > 0) {
            ivec2 tile = ivec2(gl_FragCoord.xy) / 16;
            int tileCount = drtStaticTileCount((tile.y * drtStaticTileWidth + tile.x) * TILE_STRIDE);
            if (drtDebugView == 5) {
                // Black means no projected candidate; cyan is one, yellow is
                // two or more, and red means 128 or more records overlap the tile.
                debugColor = tileCount == 0 ? vec3(0.0) :
                    tileCount == 1 ? vec3(0.0, 0.8, 0.9) :
                    tileCount < 128 ? vec3(1.0, 0.9, 0.0) : vec3(1.0, 0.0, 0.0);
            } else if (drtDebugView == 6) {
                vec3 sampleInfo = drtPlacedDebugSample(receiverView, normalView);
                // Blue: no source reaches the pixel. Red: shadowed. Green: visible.
                debugColor = sampleInfo.z < 0.5 ? vec3(0.05, 0.05, 0.45) :
                    mix(vec3(1.0, 0.0, 0.0), vec3(0.0, 1.0, 0.0), sampleInfo.x);
            } else if (drtDebugView == 7) {
                float emitterPixel;
                vec3 emitterSelfLight;
                vec4 placedDebug = drtPlacedLights(receiverView,
                    dot(normalView, normalView) > 1e-6
                        ? normalize(mat3(invModelViewMatrix) * normalView) : vec3(0.0), false, false,
                    emitterPixel, emitterSelfLight);
                // Match the real compositor's time-of-day-aware sun envelope.
                float sunBrightness = pow(voxelSun, 1.6) * clamp(
                    dot(drtAmbientSky(drtSkyColor), DRT_LUMINANCE), 0.0, 1.0);
                float weight = drtPlacedWeight(receiverView, sunBrightness, glowLevel) * placedDebug.a;
                // White is full replacement, black is vanilla handoff.
                debugColor = vec3(weight);
            }
        }
        return debugColor;
    }

    int shadowedLight = -1;
    for (int i = 0; i < min(drtPointCount, 16); i++)
        if (drtShadowSlot[i] >= 0) { shadowedLight = i; break; }
    // Normal shading remains inspectable when moving shadows are disabled.
    int light = shadowedLight >= 0 ? shadowedLight : (drtPointCount > 0 ? 0 : -1);
    bool hasMap = drtShadowCount > 0 && shadowedLight >= 0;
    float distance = light >= 0 ? length(receiverView - drtPointPos[light]) : 0.0;
    float visibility = hasMap ? drtCurrentFrameVisibility(receiverView, drtPointPos[shadowedLight], shadowedLight, normalView) : 1.0;
    float facing = light >= 0 && distance > 1e-5 && dot(normalView, normalView) > 1e-6
        ? 0.28 + 0.72 * max(dot(normalize(normalView), normalize(drtPointPos[light] - receiverView)), 0.0) : 1.0;
    // Alt+1 visibility; Alt+2 cube blue, right orange, left green; Alt+3 normal.
    vec3 unavailable = vec3(1.0, 0.0, 1.0);
    vec3 debugColor = drtDebugView == 1 ? (hasMap ? vec3(visibility) : unavailable) :
        drtDebugView == 2 ? (hasMap ?
            (drtShadowKind[drtShadowSlot[shadowedLight]] == 1 ? vec3(1.0, 0.4, 0.1) :
             drtShadowKind[drtShadowSlot[shadowedLight]] == 2 ? vec3(0.1, 1.0, 0.3) : vec3(0.1, 0.3, 1.0)) : unavailable) :
            (light >= 0 ? vec3(facing) : unavailable);
    return debugColor;

}
