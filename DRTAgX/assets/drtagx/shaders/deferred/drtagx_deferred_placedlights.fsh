// DRTAgX deferred include: GPU tile-mask traversal, emitter self-light and placed-light radiance/coverage.

// drtagx_deferred_uniforms retains the native/third-party caller dictionary.
// This specialization leaves the deferred relight and voxel fallback enabled.
#ifdef DRT_PERFORMANCE_NO_PLS
#define drtStaticCount 0
#endif

vec3 drtSourceRgb(vec4 colorSlot)
{
    // Native integer HSV conversion is prepared once per source on CPU.
    // This record holds light chroma in RGB; it needs no texture gamma decode.
    return colorSlot.rgb;
}

const int TILE_STRIDE = 5;
const int MAX_TILE_LIGHTS = 129;

// Set-bit iteration evaluates only overlapping records, including staging bit 128.
int drtNextStaticLight(int baseIndex, inout int word, inout uint remaining)
{
    while (remaining == 0u) {
        ++word;
        if (word >= 5) return -1;
        remaining = drtStaticTileData[baseIndex + word];
    }
    int bit = findLSB(remaining);
    remaining &= remaining - 1u;
    return word * 32 + bit;
}

vec4 drtPlacedFoliageLight(vec3 receiverView, vec3 normalWorld, out float voxelFacing)
{
    // Source chroma/level is captured in the same records used by solid terrain.
    // Ordinary PLS uses facing; all-pass cards receive only cached depth,
    // including their own crossed surfaces. Wind normally prepares
    // these terms at its rest pose before G-buffer fill.
    voxelFacing = 1.0; // Missing source directions must preserve the voxel fallback.
    if (drtStaticCount <= 0 || drtStaticTileWidth <= 0 || drtStaticBlend <= 0.0) return vec4(0.0);
    float n2 = dot(normalWorld, normalWorld);
    vec3 normal = n2 > 1e-6 ? normalWorld * inversesqrt(n2) : vec3(0.0);
    vec3 rotatedReceiver = mat3(invModelViewMatrix) * receiverView;
    ivec2 tile = ivec2(gl_FragCoord.xy) / 16;
    int baseIndex = (tile.y * drtStaticTileWidth + tile.x) * TILE_STRIDE;
    int word = 0;
    uint remaining = drtStaticTileData[baseIndex];
    DrtLightRanks ranks;
    
    DrtRankMoments facingMoments;
    drtClearRanks(ranks);
    drtClearRankMoments(facingMoments);
    // The cache has one staging map. Old/incoming replacement records share
    // one cache slot, so merge their radiance before consuming a harmonic rank.
    vec3 pairRadiance = vec3(0.0);
    vec2 pairMoments = vec2(0.0);
    float facingSum = 0.0, facingWeight = 0.0;
    vec2 occlusionEnergy = vec2(0.0);
    bool useOcclusion = drtStaticAllTerrainPasses != 0;
    vec3 shadowGridOffset = vec3(0.0);
    if (useOcclusion && drtShadowGridEnabled != 0 && n2 > 1e-6)
        shadowGridOffset = drtStaticGridOffset(rotatedReceiver - drtStaticSourceData[3].xyz, normal);
    float coverage = 0.0, pairCoverage = 0.0;
    for (int index = drtNextStaticLight(baseIndex, word, remaining); index >= 0;
         index = drtNextStaticLight(baseIndex, word, remaining)) {
        if (index >= drtStaticCount) continue;
        vec4 source = drtStaticSourceData[index * 4];
        vec4 transition = drtStaticSourceData[index * 4 + 2];
        float fade = clamp(transition.x, 0.0, 1.0);
        vec3 delta = source.xyz - receiverView;
        float d2 = dot(delta, delta);
        if (fade <= 0.0 || source.w <= 0.0 || d2 >= source.w * source.w) continue;
        float distance = sqrt(d2);
        vec4 colorSlot = drtStaticSourceData[index * 4 + 1];
        vec4 prepared = drtStaticSourceData[index * 4 + 3];
        int level = clamp(int(prepared.w), 0, 31);
        // Unknown normals and the exact emitter centre have no defined facing.
        float facing = useOcclusion ? 1.0 : drtPlacedFoliageFacing(normal,
            mat3(invModelViewMatrix) * delta, -rotatedReceiver);
        // Match fully light-facing terrain energy, then shade both terms with
        // the foliage response. A separate isotropic shoulder otherwise hides
        // directionality at distance. Ordinary PLS uses a 25% away-facing floor;
        // all-pass brightness depends on depth, preserving unoccluded calibration.
        float strength = drtPlacedCalibration[level].x * (1.0 - distance / source.w) *
            (1.0 / (1.0 + 0.25 * d2) + 0.33);
        if (all(lessThan(abs(rotatedReceiver - prepared.xyz - normal * 0.003), vec3(0.5))))
            strength = drtPlacedCalibration[level].y; // Bounded owning-voxel light, including its centre.
        if (strength <= 0.0 || max(colorSlot.r, max(colorSlot.g, colorSlot.b)) <= 0.0) continue;
        if (useOcclusion)
            facing *= drtStaticTwoSidedVisibilityWithTolerance(rotatedReceiver - prepared.xyz + shadowGridOffset,
                normal, int(colorSlot.w), int(transition.z), transition.y, 0.02);
        // The voxel base also belongs to these sources. Weight its facing by
        // unshaded source energy, so dim back faces cannot bias the average
        // toward a bright front face when differently colored lights overlap.
        float sourceWeight = dot(drtSourceRgb(colorSlot), DRT_LUMINANCE) * strength * fade;
        // Keep blocked lights in voxel ownership even though their direct RGB
        // consumes no harmonic rank. Otherwise fully shadowed grass stays lit
        // by its original voxel base after all direct contributions become zero.
        if (useOcclusion) occlusionEnergy += vec2(sourceWeight * facing, sourceWeight);
        vec3 contribution = drtSourceRgb(colorSlot) * strength * fade * facing;
        vec2 moments = vec2(sourceWeight * facing, sourceWeight);
        if (transition.w > 0.5) { pairRadiance += contribution; pairMoments += moments; }
        else drtInsertFacingRank(ranks, facingMoments, contribution, moments);
        float sourceCoverage = (1.0 - smoothstep(0.82 * source.w, source.w, distance)) * fade;
        // A replacement pair divides one source's fade across two records.
        // Add their coverage, so a map handoff cannot pulse the voxel base.
        if (transition.w > 0.5) pairCoverage += sourceCoverage;
        else coverage = max(coverage, sourceCoverage);
    }
    drtInsertFacingRank(ranks, facingMoments, pairRadiance, pairMoments);
    DrtRankWeights rankedWeights=drtRankWeights(ranks);
    vec3 radiance = drtRankRadiance(ranks, rankedWeights);
    vec2 selectedMoments=drtRankMomentSum(facingMoments,rankedWeights); facingSum=selectedMoments.x; facingWeight=selectedMoments.y;
    if (useOcclusion && occlusionEnergy.y > 0.0)
        voxelFacing = clamp(occlusionEnergy.x / occlusionEnergy.y, 0.0, 1.0);
    else if (facingWeight > 0.0) voxelFacing = facingSum / facingWeight;
    return vec4(radiance, max(coverage, min(pairCoverage, 1.0)));
}

vec4 drtPlacedLights(vec3 receiverView, vec3 normalWorld, bool evaluateDirect, bool grassReceiver,
    out float emitterPixel, out vec3 emitterSelfLight)
{
    emitterPixel = 0.0;
    emitterSelfLight = vec3(0.0);

    if (drtStaticCount <= 0 || drtStaticTileWidth <= 0) return vec4(0.0);

    ivec2 tile = ivec2(gl_FragCoord.xy) / 16;
    int baseIndex = (tile.y * drtStaticTileWidth + tile.x) * TILE_STRIDE;
    int word = 0;
    uint remaining = drtStaticTileData[baseIndex];

    // Check every word: the fifth includes the refresh/staging record 128.
    uint occupied = remaining;
    for (int i = 1; i < TILE_STRIDE; ++i) occupied |= drtStaticTileData[baseIndex + i];
    if (occupied == 0u) return vec4(0.0);

    DrtLightRanks ranks;
    
    drtClearRanks(ranks);
    vec3 pairRadiance = vec3(0.0);
    float coverage = 0.0;
    float pairCoverage = 0.0;

    // Receiver rotation is shared by all sources. Source rotations are prepared
    // in the matching inverse camera matrix, preserving relative coordinates.
    vec3 rotatedReceiver = mat3(invModelViewMatrix) * receiverView;
    bool hasNormal = dot(normalWorld, normalWorld) > 1e-6;
    // Snap shadow evaluation once per receiver, shared across every light.
    // Record zero supplies a block-centred anchor; switching anchors by whole
    // blocks preserves the grid. Unknown normals retain unsnapped evaluation.
    vec3 shadowGridOffset = vec3(0.0);
    if (drtShadowGridEnabled != 0 && evaluateDirect && hasNormal)
        shadowGridOffset = drtStaticGridOffset(rotatedReceiver - drtStaticSourceData[3].xyz, normalWorld);

    for (int index = drtNextStaticLight(baseIndex, word, remaining); index >= 0;
         index = drtNextStaticLight(baseIndex, word, remaining)) {
        if (index < 0 || index >= drtStaticCount) continue;

        vec4 transition = drtStaticSourceData[index * 4 + 2];
        float sourceFade = clamp(transition.x, 0.0, 1.0);
        if (sourceFade <= 0.0) continue; // Early exit for faded sources

        vec4 source = drtStaticSourceData[index * 4];
        vec3 delta = source.xyz - receiverView;
        float distanceSquared = dot(delta, delta);
        if (distanceSquared >= source.w * source.w) continue;

        float distance = sqrt(distanceSquared);

        // Subtract prepared source rotation to classify the owning voxel before
        // shadow comparison, using the same camera-relative coordinate contract.
        vec4 prepared = drtStaticSourceData[index * 4 + 3];
        vec3 fromBlockCenter = rotatedReceiver - prepared.xyz;

        // Emitter voxel self-light classification
        if (all(lessThan(abs(fromBlockCenter - normalWorld * 0.003), vec3(0.5)))) {
            vec4 colorSlot = drtStaticSourceData[index * 4 + 1];
            emitterPixel = max(emitterPixel, sourceFade);
            int level = clamp(int(prepared.w), 0, 31);
            // Native emission level 0..31:
            // with a 1x floor. Apply per source before combining emitter RGB.
            float selfLightBoost = max(1.0, 0.2 + 0.05 * float(level));
            vec3 selfLight = drtSourceRgb(colorSlot) * drtPlacedCalibration[level].y * selfLightBoost;
            emitterSelfLight = max(emitterSelfLight, selfLight * sourceFade);
            continue;
        }

        // Vanilla light replacement coverage term
        float sourceCoverage = (1.0 - smoothstep(0.82 * source.w, source.w, distance)) * sourceFade;
        if (transition.w > 0.5) pairCoverage += sourceCoverage;
        else coverage = max(coverage, sourceCoverage);

        // Coverage and owning-voxel self-light above remain valid even when the
        // compositor gives direct placed radiance zero weight. Skip all shadows.
        if (!evaluateDirect) continue;

        vec4 colorSlot = drtStaticSourceData[index * 4 + 1];
        // n.q supplies both incidence and the analytical shadow-plane slope.
        // Owning-voxel pixels returned above, so remaining distances are nonzero.
        float planeDistance = hasNormal ? dot(normalWorld, fromBlockCenter) : -distance;
        float NdotL = -planeDistance / distance;
        // Thin grass has no one-sided receiving plane. Use isotropic light
        // response at both roots and tips, including above a low emitter.
        float facing = grassReceiver ? 1.0 : 0.28 + 0.72 * max(NdotL, 0.0);
        int level = clamp(int(prepared.w), 0, 31);
        float strength = drtPlacedCalibration[level].x * (1.0 - distance / source.w) * facing / (1.0 + 0.25 * distanceSquared);
        strength *= 0.75;
        strength += drtPlacedCalibration[level].x * (1.0 - distance / source.w) * 0.2;

        // Early out before expensive shadow map evaluations
        if (strength * sourceFade < 0.0005) continue;

        float shadowBlend = transition.y;
        int shadowSlot = int(colorSlot.w);
        // The world position is already needed for owning-voxel classification.
        // Keep attenuation, coverage and emitter classification at the actual
        // pixel; only shadow evaluation moves to its fixed surface-cell centre.
        vec3 receiverNormal = hasNormal ? normalWorld : -fromBlockCenter / distance;
        vec3 shadowReceiver = fromBlockCenter + shadowGridOffset;
        float shadowDistance = hasNormal ? length(shadowReceiver) : distance;
        // Grass does not cast into this static atlas. A light-facing plane
        // avoids backface rejection and silhouette slope artifacts while
        // retaining depth occlusion from walls and other static geometry.
        if (grassReceiver) receiverNormal = -shadowReceiver / max(shadowDistance, 0.00001);
        float visibility = drtStaticVisibility(shadowReceiver, receiverNormal, shadowDistance,
            dot(receiverNormal, shadowReceiver),
            shadowSlot, int(transition.z), shadowBlend);

        // Rank after visibility: a shadowed bright emitter must not displace
        // a visible light. Old/incoming records of the cache replacement count once.
        vec3 contribution = drtSourceRgb(colorSlot) * strength * visibility * sourceFade;
        if (transition.w > 0.5) pairRadiance += contribution;
        else drtInsertRank(ranks, contribution);
    }

    drtInsertRank(ranks, pairRadiance);
    DrtRankWeights rankedWeights=drtRankWeights(ranks);
    return vec4(drtRankRadiance(ranks, rankedWeights), max(coverage, min(pairCoverage, 1.0)));
}

float drtPlacedWeight(vec3 receiverView, float sunBrightness, float glowLevel)
{
    // Preserve vertex-authored emissive tips and return to vanilla block
    // light at the camera handoff. sunBrightness includes time-of-day sky
    // radiance; voxel sky access alone would suppress outdoor lights at night.
    float emissiveKeep = clamp(glowLevel * 4.0, 0.0, 1.0);
    return (1.0 - smoothstep(112.0, 128.0, length(receiverView)))
        * (1.0 - smoothstep(0.2, 0.9, sunBrightness))
        * clamp(drtStaticBlend, 0.0, 1.0) * (1.0 - emissiveKeep);
}
