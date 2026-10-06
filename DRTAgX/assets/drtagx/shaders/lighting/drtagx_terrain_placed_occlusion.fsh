// Fragment receiving for forward terrain and rest-pose wind G-buffer fills.
// The C# Use/Stop bridge scopes SSBO 4 and the depth array on texture unit 14.
layout(std430, binding = 4) readonly buffer DrtTerrainPlacedSources { vec4 drtTerrainPlacedData[]; };
layout(binding = 14) uniform sampler2DArrayShadow drtStaticMaps;
uniform int drtTerrainPlacedCount = 0;
uniform int drtTerrainPlacedAllPasses = 0;
uniform int drtTerrainPlacedGridEnabled = 1;
uniform vec3 drtTerrainPlacedSourceShift = vec3(0.0);
uniform vec2 drtPlacedCalibration[32];

// Keep the caller declaration above, but make every owned use a compile-time
// zero in Performance. The runtime count gate alone did not remove frame cost.
#ifdef DRT_PERFORMANCE_NO_PLS
#define drtTerrainPlacedCount 0
#endif

#include drtagx_deferred_cube.fsh
#include drtagx_deferred_staticshadows.fsh

vec3 drtTerrainPlacedPlane = vec3(0.0);

void drtCaptureTerrainPlacedPlane(vec3 worldPos) {
    // Capture derivatives before alpha/boundary discard or divergent returns.
    // The winding is irrelevant: two-sided depth receiving orients per light.
    vec3 dx = dFdx(worldPos), dy = dFdy(worldPos);
    float dx2 = dot(dx, dx), dy2 = dot(dy, dy);
    if (min(dx2, dy2) <= 1e-30) { drtTerrainPlacedPlane = vec3(0.0); return; }
    // Test angular degeneracy, independent of the grass card's pixel footprint.
    // A small valid rest plane must not lose its PCF slope as wind changes coverage.
    vec3 plane = cross(dx * inversesqrt(dx2), dy * inversesqrt(dy2));
    float n2 = dot(plane, plane);
    drtTerrainPlacedPlane = n2 > 1e-12 ? plane * inversesqrt(n2) : vec3(0.0);
}

void drtPrepareTerrainPlacedVisibility(vec3 worldPos, bool foliage, float glowLevel) {
    if (drtTerrainPlacedCount <= 0) return;
    bool useOcclusion = drtTerrainPlacedAllPasses != 0;
    if (!useOcclusion && !foliage) return;
    // Match the prepared source's inverse-view-rotated eye position, without
    // absolute float world coordinates or a per-source matrix multiplication.
    vec3 receiver = worldPos - drtAtmosphereCamera.xyz;
    vec3 gridOffset = vec3(0.0);
    if (useOcclusion && drtTerrainPlacedGridEnabled != 0 && dot(drtTerrainPlacedPlane, drtTerrainPlacedPlane) > 0.0)
        gridOffset = drtStaticGridOffset(receiver - drtTerrainPlacedData[3].xyz - drtTerrainPlacedSourceShift, drtTerrainPlacedPlane);
    vec2 energy = vec2(0.0);
    DrtLightRanks ranks;
    
    if (foliage) drtClearRanks(ranks);
    vec3 pairRadiance = vec3(0.0);
    float coverage = 0.0, pairCoverage = 0.0;
    for (int i = 0; i < min(drtTerrainPlacedCount, 129); ++i) {
        vec4 source = drtTerrainPlacedData[i * 4];
        vec4 color = drtTerrainPlacedData[i * 4 + 1];
        vec4 transition = drtTerrainPlacedData[i * 4 + 2];
        vec4 prepared = drtTerrainPlacedData[i * 4 + 3];
        float fade = clamp(transition.x, 0.0, 1.0);
        vec3 q = receiver - prepared.xyz - drtTerrainPlacedSourceShift;
        float distance = length(q);
        if (fade <= 0.0 || source.w <= 0.0 || distance >= source.w) continue;
        int level = clamp(int(prepared.w), 0, 31);
        float strength = drtPlacedCalibration[level].x * (1.0 - distance / source.w) *
            (1.0 / (1.0 + 0.25 * distance * distance) + 0.33);
        bool ownVoxel = all(lessThan(abs(q - drtTerrainPlacedPlane * 0.003), vec3(0.5)));
        if (foliage && ownVoxel) strength = drtPlacedCalibration[level].y;
        float sourceEnergy = dot(max(color.rgb, vec3(0.0)), DRT_LUMINANCE) * strength * fade;
        if (sourceEnergy <= 0.0) continue;
        // Preserve the owning emitter's self-light; every other surface can
        // receive its own cached depth, including no-cull, glass and liquids.
        float visibility = !useOcclusion || ownVoxel ? 1.0 :
            drtStaticTwoSidedVisibilityWithTolerance(q + gridOffset, drtTerrainPlacedPlane,
                int(color.w), int(transition.z), transition.y, foliage ? 0.02 : 0.0);
        // All-pass cards shade by their own cached depth, with no normal gate.
        // Ordinary PLS gives the visible away side one quarter of facing light.
        if (foliage && !useOcclusion)
            visibility *= drtPlacedFoliageFacing(drtTerrainPlacedPlane, -q, -receiver);
        energy += vec2(sourceEnergy * visibility, sourceEnergy);
        if (foliage) {
            // Rank only visible RGB. Old/incoming records of one replacement
            // share a rank, and blocked sources still own their voxel share.
            vec3 contribution = color.rgb * strength * fade * visibility;
            if (transition.w > 0.5) pairRadiance += contribution;
            else drtInsertRank(ranks, contribution);
        }
        float sourceCoverage = (1.0 - smoothstep(0.82 * source.w, source.w, distance)) * fade;
        if (transition.w > 0.5) pairCoverage += sourceCoverage;
        else coverage = max(coverage, sourceCoverage);
    }
    if (energy.y <= 0.0) return;
    coverage = max(coverage, min(pairCoverage, 1.0));
    float sky = pow(clamp(drtSurfaceSunAccess, 0.0, 1.0), 1.6) *
        clamp(dot(drtSkyLight, DRT_LUMINANCE), 0.0, 1.0);
    float weight = (1.0 - smoothstep(112.0, 128.0, length(receiver))) *
        (1.0 - smoothstep(0.2, 0.9, sky));
    float visibility = clamp(energy.x / energy.y, 0.0, 1.0);
    if (foliage) {
        // Match the deferred foliage compositor, including its half voxel
        // share, scalar darkness envelope, source budget and emissive keep.
        weight *= 1.0 - clamp(glowLevel * 4.0, 0.0, 1.0);
        drtInsertRank(ranks, pairRadiance);
        
        float voxelGate = clamp(max(drtVoxelLight.r, max(drtVoxelLight.g, drtVoxelLight.b)) * 32.0, 0.0, 1.0);
        drtTerrainPlacedRadiance = drtRankRadiance(ranks) * (0.5 * weight * voxelGate);
        visibility *= 0.5;
    }
    // Only covered voxel blocklight is occluded. Sun, moving lights, emission,
    // alpha and OIT revealage retain their existing independent contributions.
    drtTerrainPlacedVisibility = 1.0 - coverage * weight * (1.0 - visibility);
}

void drtPrepareTerrainPlacedVisibility(vec3 worldPos) {
    drtPrepareTerrainPlacedVisibility(worldPos, false, 0.0);
}
