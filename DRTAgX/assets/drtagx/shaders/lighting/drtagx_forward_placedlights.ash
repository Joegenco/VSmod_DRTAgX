// Bounded vertex lighting for animated models, hands/items and particles.
// Reuse the nearest completed placed sources; no depth reads or extra pass.
uniform int drtEntityPlacedCount;
// One additional candidate lets a fading cache replacement share one rank.
uniform vec4 drtEntityPlacedSources[9]; // current eye-space xyz, native reach
uniform vec4 drtEntityPlacedColors[9];  // native linear RGB chroma, light level
uniform vec4 drtEntityPlacedFades;
uniform vec4 drtEntityPlacedFadesExtra; // Fade components for sources 4..7.
uniform float drtEntityPlacedFadeOverflow; // Temporary ninth record.
uniform int drtEntityPlacedPairMask; // Bits 0..8 identify the single replacement pair.
uniform vec2 drtPlacedCalibration[32];

// Preserve the serialized preference and caller uniforms; specialize only
// owned PLS calculations while native block-light contribution remains live.
#ifdef DRT_PERFORMANCE_NO_PLS
#define drtEntityPlacedCount 0
#endif

vec3 drtForwardPlacedLocal(vec3 receiverView, vec3 normalView, vec3 voxel,
    float sunAccess, float glow) {
    if (drtEntityPlacedCount <= 0) return voxel;
    float skyBrightness = pow(sunAccess, 1.6) * clamp(dot(drtSkyLight, DRT_LUMINANCE), 0.0, 1.0);
    float weight = (1.0 - smoothstep(112.0, 128.0, length(receiverView))) *
        (1.0 - smoothstep(0.2, 0.9, skyBrightness)) * (1.0 - clamp(glow * 1.6, 0.0, 1.0));
    if (weight <= 0.0) return voxel; // Bright daylight/emissive tips need no lamp loop.
    float n2 = dot(normalView, normalView);
    vec3 n = n2 > 1e-6 ? normalView * inversesqrt(n2) : vec3(0.0);
    DrtLightRanks ranks;
    
    drtClearRanks(ranks);
    vec3 pairRadiance = vec3(0.0);
    float coverage = 0.0;
#ifdef DRT_ANIMATED_PLACED_FACING
    float facingEnergy = 0.0, sourceEnergy = 0.0;
    DrtRankMoments facingMoments;
    vec2 pairMoments = vec2(0.0);
    drtClearRankMoments(facingMoments);
#endif
    for (int i = 0; i < min(drtEntityPlacedCount, DRT_LIGHT_BUDGET + 1); ++i) {
        vec3 delta = drtEntityPlacedSources[i].xyz - receiverView;
        float d2 = dot(delta, delta);
        float radius = drtEntityPlacedSources[i].w;
        if (d2 >= radius * radius || radius <= 0.0) continue;
        float d = sqrt(d2);
        float fade = clamp(i < 4 ? drtEntityPlacedFades[i] :
            i < 8 ? drtEntityPlacedFadesExtra[i - 4] : drtEntityPlacedFadeOverflow, 0.0, 1.0);
        bool replacementPair = (drtEntityPlacedPairMask & (1 << i)) != 0;
        int level = clamp(int(drtEntityPlacedColors[i].w), 0, 31);
#ifdef DRT_ANIMATED_PLACED_FACING
        // Shade the entire head-on contribution, including its isotropic
        // shoulder. Normalizing the skinned/view normal makes model scale safe.
        // Retain the tuned normal response and head-on energy.
        float facing = n2 > 1e-6 && d > 1e-5 ?
            0.03 + 0.97 * max(dot(n, delta / d), 0.0) : 1.0;
        float strength = drtPlacedCalibration[level].x * (1.0 - d / radius) *
            (1.0 / (1.0 + 0.25 * d2) + 0.33);
        vec3 unshaded = drtEntityPlacedColors[i].rgb * strength * fade;
        float energy = max(dot(unshaded, DRT_LUMINANCE), 0.0);
        if (energy <= 0.0) continue; // Empty/faded records preserve voxel fallback.
        // Weight voxel direction by unshaded source energy, so facing a lamp
        // does not increase its influence relative to an opposing colored lamp.
        if (replacementPair) { pairRadiance += unshaded * facing; pairMoments += vec2(energy * facing, energy); }
        else drtInsertFacingRank(ranks, facingMoments, unshaded * facing, vec2(energy * facing, energy));
#else
        float facing = n2 > 1e-6 && d > 1e-5 ?
            0.28 + 0.72 * max(dot(n, delta / d), 0.0) : 1.0;
        // Other forward callers retain the terrain's calibrated inverse-square
        // term and independent authored isotropic shoulder.
        float strength = drtPlacedCalibration[level].x * (1.0 - d / radius) *
            (facing / (1.0 + 0.25 * d2) + 0.33);
        vec3 contribution = drtEntityPlacedColors[i].rgb * strength * fade;
        if (replacementPair) pairRadiance += contribution;
        else drtInsertRank(ranks, contribution);
#endif
        coverage = max(coverage, (1.0 - smoothstep(0.82 * radius, radius, d)) * fade);
    }
    // The old/incoming records crossfade one cache slot, rather than adding a
    // second harmonic rank and causing a brightness dip during replacement.
#ifdef DRT_ANIMATED_PLACED_FACING
    drtInsertFacingRank(ranks, facingMoments, pairRadiance, pairMoments);
#else
    drtInsertRank(ranks, pairRadiance);
#endif
    // Apply 1, 1/2, ... 1/8 to the strongest available receiver contributions.
    DrtRankWeights rankedWeights=drtRankWeights(ranks);
    vec3 direct = drtRankRadiance(ranks, rankedWeights);
    // Reduce only covered voxel energy and retain its occlusion: models are
    // receivers outside the terrain-only depth cache, so never light a sealed cave
    // from an unoccluded source on the other side of a wall.
    vec3 voxelGate = clamp(voxel * 32.0, vec3(0.0), vec3(1.0));
#ifdef DRT_ANIMATED_PLACED_FACING
    vec2 selectedMoments=drtRankMomentSum(facingMoments,rankedWeights); facingEnergy=selectedMoments.x; sourceEnergy=selectedMoments.y;
    float voxelFacing = sourceEnergy > 0.0 ? facingEnergy / sourceEnergy : 1.0;
    // Only the covered share follows lamp direction; uncovered voxel light
    // retains the fallback. Sun, emission and moving light are added separately.
    // Keep the tuned animated gain on the placed-lit voxel/source share.
    // Uncovered voxel light keeps its fallback as coverage/weight fades out.
    const float animatedPlacedGain = 0.5;
    return voxel * (1.0 - coverage * weight * (1.0 - 0.5 * voxelFacing * animatedPlacedGain)) +
        direct * voxelGate * (0.5 * weight * animatedPlacedGain);
#else
    return voxel * (1.0 - 0.5 * coverage * weight) + direct * voxelGate * (0.5 * weight);
#endif
}
