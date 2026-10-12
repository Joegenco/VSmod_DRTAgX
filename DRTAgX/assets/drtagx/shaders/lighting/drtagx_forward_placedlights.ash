// Bounded vertex lighting for animated models, hands/items and particles.
// Reuse the nearest completed placed sources; no depth reads or extra pass.
uniform int drtEntityPlacedCount;
// One additional candidate lets a fading cache replacement merge its radiance.
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
    DrtPlacedLightSum lights;
    drtClearPlacedSum(lights);
    vec3 pairRadiance = vec3(0.0);
    float coverage = 0.0, pairCoverage = 0.0;
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
        // Match current terrain's diffuse response without shadow sampling.
        // The independent shoulder keeps away faces lit instead of suppressing
        // the entire contribution to the old three-percent facing floor.
        float towardsLight = n2 > 1e-6 && d > 1e-5 ?
            max(dot(n, delta / d), 0.0) : 0.0;
        float facing = n2 > 1e-6 && d > 1e-5 ? 0.28 + 0.72 * towardsLight : 1.0;
        float strength = drtPlacedCalibration[level].x * (1.0 - d / radius) *
            (0.75 * facing / (1.0 + 0.25 * d2) + 0.2);
#ifdef DRT_ANIMATED_POINT_FACING_GAIN
        // Boost this lamp's facing contribution before overlap blending. Voxel
        // fill, native emission and sunlight remain independently composed.
        strength *= 1.0 + (DRT_ANIMATED_POINT_FACING_GAIN - 1.0) * clamp(towardsLight, 0.0, 1.0);
#endif
        vec3 contribution = drtEntityPlacedColors[i].rgb * strength * fade;
        float energy = max(dot(contribution, DRT_LUMINANCE), 0.0);
        if (energy <= 0.0) continue; // Empty/faded records preserve voxel fallback.
        if (replacementPair) pairRadiance += contribution;
        else drtAddPlacedLight(lights, contribution);
#else
        float facing = n2 > 1e-6 && d > 1e-5 ?
            0.28 + 0.72 * max(dot(n, delta / d), 0.0) : 1.0;
        // Other forward callers retain the terrain's calibrated inverse-square
        // term and independent authored isotropic shoulder.
        float strength = drtPlacedCalibration[level].x * (1.0 - d / radius) *
            (facing / (1.0 + 0.25 * d2) + 0.33);
        vec3 contribution = drtEntityPlacedColors[i].rgb * strength * fade;
        if (replacementPair) pairRadiance += contribution;
        else drtAddPlacedLight(lights, contribution);
#endif
        float sourceCoverage = (1.0 - smoothstep(0.82 * radius, radius, d)) * fade;
        if (replacementPair) pairCoverage += sourceCoverage;
        else coverage = max(coverage, sourceCoverage);
    }
    // Old/new fades divide one source's coverage. Sum that pair before the
    // voxel handoff so its midpoint cannot expose extra unoccluded voxel light.
    coverage = max(coverage, min(pairCoverage, 1.0));
    // The old/incoming records crossfade one cache slot, rather than adding a
    // second peak and causing a brightness dip during replacement.
    drtAddPlacedLight(lights, pairRadiance);
    vec3 direct = drtPlacedSumRadiance(lights);
#ifdef DRT_ANIMATED_PLACED_FACING
    // Full PLS coverage retains half the native voxel light on every side.
    // Coverage and the existing envelope fade smoothly back to native lighting;
    // sun, emission and moving light are added separately by the caller.
    // Animated receivers intentionally use unshadowed PLS. Its RGB must not
    // follow transient native flood-light values during nearby block updates.
    // The shared sum applies global half-strength PLS once. Animated models
    // use a further 0.66 RGB gain; their half-voxel handoff stays unchanged.
    return voxel * (1.0 - 0.5 * coverage * weight) +
        direct * (0.66 * weight);
#else
    // Other legacy forward materials retain native voxel occlusion so an
    // unoccluded source cannot light a sealed cave through the terrain.
    vec3 voxelGate = clamp(voxel * 32.0, vec3(0.0), vec3(1.0));
    return voxel * (1.0 - 0.5 * coverage * weight) + direct * voxelGate * (0.5 * weight);
#endif
}
