// DRTAgX deferred include: GPU tile-mask traversal, emitter self-light and placed-light radiance/coverage.

// drtagx_deferred_uniforms retains the native/third-party caller dictionary.
// This specialization leaves the deferred relight and voxel fallback enabled.
#ifdef DRT_PERFORMANCE_NO_PLS
#define drtStaticCount 0
#endif

#ifndef DRT_MEMO_GRID_ENABLED
#define DRT_MEMO_GRID_ENABLED drtShadowGridEnabled
#endif
#include drtagx_placed_light_memo.ash

// The compositor must keep full voxel lighting for a missing memo cell.
bool drtPlacedMemoAvailable = false;
bool drtPlacedMemoFillWanted = false; // Deferred-only warmup follows the same direct-light envelope.

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
    // Cards receive cached depth, including their own crossed surfaces. Wind
    // normally prepares these terms at its rest pose before G-buffer fill.
    voxelFacing = 1.0; // Missing source directions must preserve the voxel fallback.
#if defined(DRT_MEMO_ONLY) || defined(DRT_PERFORMANCE_NO_PLS)
    // Moving foliage cannot reuse this static surface memo. Compile its old
    // source traversal out of production rather than retain a uniform branch.
    return vec4(0.0);
#else
    // Moving wind cards retain ordinary native voxel light in memo mode. They
    // cannot amortize surface-cache fills, and depth filtering is their main cost.
    if (drtMemoEnabled != 0) return vec4(0.0);
    if (drtStaticCount <= 0 || drtStaticTileWidth <= 0 || drtStaticBlend <= 0.0) return vec4(0.0);
    float n2 = dot(normalWorld, normalWorld);
    vec3 normal = n2 > 1e-6 ? normalWorld * inversesqrt(n2) : vec3(0.0);
    vec3 rotatedReceiver = mat3(invModelViewMatrix) * receiverView;
    ivec2 tile = ivec2(gl_FragCoord.xy) / 16;
    int baseIndex = (tile.y * drtStaticTileWidth + tile.x) * TILE_STRIDE;
    int word = 0;
    uint remaining = drtStaticTileData[baseIndex];
    DrtPlacedLightSum lights;
    drtClearPlacedSum(lights);
    vec2 facingMoments = vec2(0.0);
    // The cache has one staging map. Old/incoming replacement records share
    // one cache slot, so merge their radiance before updating the visible peak.
    vec3 pairRadiance = vec3(0.0);
    vec3 shadowGridOffset = vec3(0.0);
    if (drtShadowGridEnabled != 0 && n2 > 1e-6)
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
        // Brightness depends on depth, preserving unoccluded calibration.
        float strength = drtPlacedCalibration[level].x * (1.0 - distance / source.w) *
            (1.0 / (1.0 + 0.25 * d2) + 0.33);
        if (all(lessThan(abs(rotatedReceiver - prepared.xyz - normal * 0.003), vec3(0.5))))
            strength = drtPlacedCalibration[level].y; // Bounded owning-voxel light, including its centre.
        if (strength <= 0.0 || max(colorSlot.r, max(colorSlot.g, colorSlot.b)) <= 0.0) continue;
        float facing = drtStaticTwoSidedVisibilityWithTolerance(rotatedReceiver - prepared.xyz + shadowGridOffset,
            normal, int(colorSlot.w), int(transition.z), transition.y, 0.02);
        // The voxel base also belongs to these sources. Weight its facing by
        // unshaded source energy, so dim back faces cannot bias the average
        // toward a bright front face when differently colored lights overlap.
        float sourceWeight = dot(drtSourceRgb(colorSlot), DRT_LUMINANCE) * strength * fade;
        // Keep blocked lights in voxel ownership despite their zero direct RGB.
        // Otherwise fully shadowed grass stays lit
        // by its original voxel base after all direct contributions become zero.
        vec3 contribution = drtSourceRgb(colorSlot) * strength * fade * facing;
        facingMoments += vec2(sourceWeight * facing, sourceWeight);
        if (transition.w > 0.5) pairRadiance += contribution;
        else drtAddPlacedLight(lights, contribution);
        float sourceCoverage = (1.0 - smoothstep(0.82 * source.w, source.w, distance)) * fade;
        // A replacement pair divides one source's fade across two records.
        // Add their coverage, so a map handoff cannot pulse the voxel base.
        if (transition.w > 0.5) pairCoverage += sourceCoverage;
        else coverage = max(coverage, sourceCoverage);
    }
    drtAddPlacedLight(lights, pairRadiance);
    vec3 radiance = drtPlacedSumRadiance(lights);
    // Voxel ownership uses unshaded energy from all ready sources; continuous
    // color blending needs no separately sorted facing-moment arrays.
    if (facingMoments.y > 0.0)
        voxelFacing = clamp(facingMoments.x / facingMoments.y, 0.0, 1.0);
    return vec4(radiance, max(coverage, min(pairCoverage, 1.0)));
#endif
}

vec4 drtPlacedLights(vec3 receiverView, vec3 normalWorld, uint nativeToken, bool evaluateDirect, bool grassReceiver,
    out float emitterPixel, out vec3 emitterSelfLight)
{
    emitterPixel = 0.0;
    emitterSelfLight = vec3(0.0);
    drtPlacedMemoAvailable = false;
    drtPlacedMemoFillWanted = false;

#ifdef DRT_PERFORMANCE_NO_PLS
    return vec4(0.0);
#else
    if (drtMemoEnabled != 0) {
        drtPlacedMemoFillWanted = evaluateDirect && drtMemoEnabled == 1;
        DrtPlacedMemoValue memo;
        int status = drtQueryPlacedMemo(mat3(invModelViewMatrix) * receiverView,
            normalWorld, nativeToken, 0, evaluateDirect, memo);
        if (status == DRT_MEMO_HIT || status == DRT_MEMO_FILLED) {
            drtPlacedMemoAvailable = true;
            emitterPixel = memo.selfLightEmitter.a;
            emitterSelfLight = memo.selfLightEmitter.rgb;
            return memo.radianceCoverage;
        }
        return vec4(0.0); // Pending/failed memo cells never enter the lamp loop.
    }

#ifndef DRT_MEMO_ONLY
    if (drtStaticCount <= 0 || drtStaticTileWidth <= 0) return vec4(0.0);

    ivec2 tile = ivec2(gl_FragCoord.xy) / 16;
    int baseIndex = (tile.y * drtStaticTileWidth + tile.x) * TILE_STRIDE;
    int word = 0;
    uint remaining = drtStaticTileData[baseIndex];

    // Check every word: the fifth includes the refresh/staging record 128.
    uint occupied = remaining;
    for (int i = 1; i < TILE_STRIDE; ++i) occupied |= drtStaticTileData[baseIndex + i];
    if (occupied == 0u) return vec4(0.0);

    DrtPlacedLightSum lights;
    drtClearPlacedSum(lights);
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
            vec3 selfLight = drtSourceRgb(colorSlot) * drtPlacedCalibration[level].y * selfLightBoost * DRT_PLACED_INTENSITY;
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

        // Accumulate after visibility, so blocked emitters add neither color nor
        // peak energy. A cache replacement's two records update the peak once.
        vec3 contribution = drtSourceRgb(colorSlot) * strength * visibility * sourceFade;
        if (transition.w > 0.5) pairRadiance += contribution;
        else drtAddPlacedLight(lights, contribution);
    }

    drtAddPlacedLight(lights, pairRadiance);
    return vec4(drtPlacedSumRadiance(lights), max(coverage, min(pairCoverage, 1.0)));
#else
    return vec4(0.0); // Production never restores legacy per-receiver work.
#endif
#endif
}

// Generic callers retain the oct-normal cache namespace and legacy behavior.
vec4 drtPlacedLights(vec3 receiverView, vec3 normalWorld, bool evaluateDirect, bool grassReceiver,
    out float emitterPixel, out vec3 emitterSelfLight)
{
    return drtPlacedLights(receiverView, normalWorld, DRT_MEMO_GENERIC_NORMAL, evaluateDirect, grassReceiver,
        emitterPixel, emitterSelfLight);
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
