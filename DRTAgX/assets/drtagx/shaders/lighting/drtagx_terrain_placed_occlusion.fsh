#ifdef DRT_DEFERRED_RECEIVER_ONLY
// The supported deferred path evaluates PLS only in compute and fullscreen
// relight. Forward native subpasses never bind or query the page directory.
vec3 drtTerrainPlacedPlane = vec3(0.0);
void drtCaptureTerrainPlacedPlane(vec3 p) { drtTerrainPlacedPlane = cross(dFdx(p),dFdy(p)); }
void drtPrepareTerrainPlacedVisibility(vec3 p,bool foliage,float glow) {}
void drtPrepareTerrainPlacedVisibility(vec3 p) {}
#else
// Fragment receiving for forward terrain and rest-pose wind G-buffer fills.
// The C# Use/Stop bridge scopes SSBOs 4/5 and the depth array on texture unit 14.
#ifndef DRT_MEMO_ONLY
layout(std430, binding = 4) readonly buffer DrtTerrainPlacedSources { vec4 drtTerrainPlacedData[]; };
uniform int drtTerrainPlacedCount = 0;
uniform vec3 drtTerrainPlacedSourceShift = vec3(0.0);
#endif
uniform int drtTerrainPlacedGridEnabled = 1; // Shared presentation/legacy grid preference.
#if !defined(DRT_MEMO_ONLY) || !defined(DRT_MEMO_READ_ONLY)
layout(binding = 14) uniform sampler2DArrayShadow drtStaticMaps;
#endif
uniform vec2 drtPlacedCalibration[32];

// Keep the caller declaration above, but make every owned use a compile-time
// zero in Performance. The runtime count gate alone did not remove frame cost.
#ifdef DRT_PERFORMANCE_NO_PLS
#define drtTerrainPlacedCount 0
#endif

#ifndef DRT_MEMO_ONLY
#include drtagx_placed_source_grid.ash
#endif
#if !defined(DRT_MEMO_ONLY) || !defined(DRT_MEMO_READ_ONLY)
#include drtagx_deferred_cube.fsh
#include drtagx_deferred_staticshadows.fsh
#endif
#ifndef DRT_MEMO_GRID_ENABLED
#define DRT_MEMO_GRID_ENABLED drtTerrainPlacedGridEnabled
#endif
#include drtagx_placed_light_memo.ash

#ifndef DRT_MEMO_FORWARD_RECEIVER
#define DRT_MEMO_FORWARD_RECEIVER true
#endif
#ifndef DRT_MEMO_NATIVE_TOKEN
#define DRT_MEMO_NATIVE_TOKEN DRT_MEMO_GENERIC_NORMAL
#endif

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

#ifndef DRT_MEMO_ONLY
void drtAccumulateTerrainPlacedSource(int i, vec3 receiver, bool foliage,
                                     vec3 gridOffset, inout vec2 energy, inout DrtPlacedLightSum lights,
                                     inout vec3 pairRadiance, inout float coverage, inout float pairCoverage) {
    vec4 source = drtTerrainPlacedData[i * 4];
    vec4 color = drtTerrainPlacedData[i * 4 + 1];
    vec4 transition = drtTerrainPlacedData[i * 4 + 2];
    vec4 prepared = drtTerrainPlacedData[i * 4 + 3];
    float fade = clamp(transition.x, 0.0, 1.0);
    if (fade <= 0.0 || source.w <= 0.0) return;
    vec3 q = receiver - prepared.xyz - drtTerrainPlacedSourceShift;
    float distance2 = dot(q, q);
    // The exact spherical reach test needs no square root for rejected sources.
    // Reuse squared distance for inverse-square attenuation after acceptance.
    if (distance2 >= source.w * source.w) return;
    float distance = sqrt(distance2);
    int level = clamp(int(prepared.w), 0, 31);
    float strength = drtPlacedCalibration[level].x * (1.0 - distance / source.w) *
        (1.0 / (1.0 + 0.25 * distance2) + 0.33);
    bool ownVoxel = all(lessThan(abs(q - drtTerrainPlacedPlane * 0.003), vec3(0.5)));
    if (foliage && ownVoxel) strength = drtPlacedCalibration[level].y;
    float sourceEnergy = dot(max(color.rgb, vec3(0.0)), DRT_LUMINANCE) * strength * fade;
    if (sourceEnergy <= 0.0) return;
    // Preserve the owning emitter's self-light; every other surface can
    // receive its own cached depth, including no-cull, glass and liquids.
    float visibility = ownVoxel ? 1.0 :
        drtStaticTwoSidedVisibilityWithTolerance(q + gridOffset, drtTerrainPlacedPlane,
            int(color.w), int(transition.z), transition.y, foliage ? 0.02 : 0.0);
    energy += vec2(sourceEnergy * visibility, sourceEnergy);
    if (foliage) {
        // Merge a replacement before updating the peak so its fade stays stable.
        vec3 contribution = color.rgb * strength * fade * visibility;
        if (transition.w > 0.5) pairRadiance += contribution;
        else drtAddPlacedLight(lights, contribution);
    }
    float sourceCoverage = (1.0 - smoothstep(0.82 * source.w, source.w, distance)) * fade;
    if (transition.w > 0.5) pairCoverage += sourceCoverage;
    else coverage = max(coverage, sourceCoverage);
}
#endif

void drtPrepareTerrainPlacedVisibility(vec3 worldPos, bool foliage, float glowLevel) {
#ifdef DRT_PERFORMANCE_NO_PLS
    return; // Compile out memo queries and depth work in the no-PLS control.
#else
#ifdef DRT_MEMO_ONLY
    // Production cannot enter legacy loops: failed/disabled memo and wind
    // receivers retain native voxel light without keeping old GPU code alive.
    if (drtMemoEnabled != 1 || foliage) return;
#ifdef DRT_MEMO_READ_ONLY
    if (!DRT_MEMO_FORWARD_RECEIVER) return;
#endif
#else
#ifdef DRT_MEMO_READ_ONLY
    // G-buffer fill leaves lighting to deferred relight. Forward/decor replay
    // may reuse completed mode-0 lighting without making geometry a writer.
    if (drtMemoEnabled != 0 && !DRT_MEMO_FORWARD_RECEIVER) return;
#endif
    // Wind uses its native voxel envelope in memo mode; its moving surface
    // would otherwise create repeated cells and restore per-lamp depth cost.
    if (drtMemoEnabled != 0 && foliage) return;
    if (drtMemoEnabled == 0 && drtTerrainPlacedCount <= 0) return;
#endif
    // Match the prepared source's inverse-view-rotated eye position, without
    // absolute float world coordinates or a per-source matrix multiplication.
    vec3 receiver = worldPos - drtAtmosphereCamera.xyz;
    float sky = pow(clamp(drtSurfaceSunAccess, 0.0, 1.0), 1.6) *
        clamp(dot(drtSkyLight, DRT_LUMINANCE), 0.0, 1.0);
    float weight = (1.0 - smoothstep(112.0, 128.0, length(receiver))) *
        (1.0 - smoothstep(0.2, 0.9, sky));
    if (foliage) weight *= 1.0 - clamp(glowLevel * 4.0, 0.0, 1.0);
    // The helper's default visibility/radiance are 1/0. Daylight, far receivers
    // and emissive tips cannot change those outputs, so avoid every shadow tap.
    if (weight <= 0.0) return;
    if (drtMemoEnabled != 0) {
        DrtPlacedMemoValue memo;
#ifdef DRT_MEMO_READ_ONLY
        // Direct-weighted visibility comes from relight's existing PCF samples.
        // Derivative and deferred normals may form different keys; fallback is
        // preferable to coercing them and sharing lighting across distinct faces.
        vec3 memoNormal = drtTerrainPlacedPlane;
#ifdef DRT_MEMO_AUTHORED_NORMAL
        float authoredNormal2 = dot(DRT_MEMO_AUTHORED_NORMAL, DRT_MEMO_AUTHORED_NORMAL);
        if (authoredNormal2 > 1e-6 && dot(memoNormal, memoNormal) > 1e-6) {
            vec3 authoredNormal = DRT_MEMO_AUTHORED_NORMAL * inversesqrt(authoredNormal2);
            // Both normals describe this same plane. Align its winding with the
            // native G-buffer normal only when parallelism is established; keep
            // genuinely bent/unknown planes distinct rather than chasing hits.
            if (abs(dot(authoredNormal, memoNormal)) >= 0.999999) memoNormal = authoredNormal;
        }
#endif
        // Eligible ordinary opaque/topsoil replay shares the exact native
        // identity filled by deferred. Other receiver classes stay generic.
        int status = drtQueryPlacedMemo(receiver, memoNormal, DRT_MEMO_NATIVE_TOKEN, 0, false, memo);
#else
        int status = drtQueryPlacedMemo(receiver, drtTerrainPlacedPlane, 1, true, memo);
#endif
        if (status == DRT_MEMO_HIT || status == DRT_MEMO_FILLED) {
            // Match deferred: replace only covered native blocklight, then add
            // already-faded cached RGB once. Wind keeps its native fallback.
            // Cached static replay shares deferred's half-voxel PLS composition
            // throughout coverage, independent of the cached shadow visibility.
            drtTerrainPlacedVisibility = 1.0 - 0.5 * memo.radianceCoverage.a * weight;
            drtTerrainPlacedRadiance = memo.radianceCoverage.rgb * weight;
            drtTerrainPlacedSelfLight = memo.selfLightEmitter.rgb;
        }
        return; // Missing cells retain visibility1 and never traverse sources.
    }
#ifndef DRT_MEMO_ONLY
    vec3 gridOffset = vec3(0.0);
    if (drtTerrainPlacedGridEnabled != 0 && dot(drtTerrainPlacedPlane, drtTerrainPlacedPlane) > 0.0)
        gridOffset = drtStaticGridOffset(receiver - drtTerrainPlacedData[3].xyz - drtTerrainPlacedSourceShift, drtTerrainPlacedPlane);
    vec2 energy = vec2(0.0);
    DrtPlacedLightSum lights;
    if (foliage) drtClearPlacedSum(lights);
    vec3 pairRadiance = vec3(0.0);
    float coverage = 0.0, pairCoverage = 0.0;
    // Exact cell masks reject only sources outside the padded receiving volume.
    // Use the same old-camera rebase as the source/receiver subtraction below.
    int sourceCount = min(drtTerrainPlacedCount, 129);
    DrtTerrainPlacedCellMask cellMask;
    bool useCells = drtLoadTerrainPlacedCellMask(receiver - drtTerrainPlacedSourceShift, sourceCount, cellMask);
    if (!useCells) {
        // Keep the original simple control flow for small/dense sets and fallback.
        for (int i = 0; i < sourceCount; ++i)
            drtAccumulateTerrainPlacedSource(i, receiver, foliage, gridOffset,
                energy, lights, pairRadiance, coverage, pairCoverage);
    } else {
        // Visit only set bits, in the original increasing source order. The
        // fallback decision stays outside both loops and never runs per source.
        for (int word = 0; word < 5; ++word) {
            uint remaining = word < 4 ? cellMask.low[word] : cellMask.high;
            while (remaining != 0u) {
                int i = word * 32 + findLSB(remaining);
                remaining &= remaining - 1u;
                if (i >= sourceCount) continue;
                drtAccumulateTerrainPlacedSource(i, receiver, foliage, gridOffset,
                    energy, lights, pairRadiance, coverage, pairCoverage);
            }
        }
    }
    if (energy.y <= 0.0) return;
    coverage = max(coverage, min(pairCoverage, 1.0));
    float visibility = clamp(energy.x / energy.y, 0.0, 1.0);
    if (foliage) {
        // Match the deferred foliage compositor, including its half voxel
        // share, scalar darkness envelope, source budget and emissive keep.
        drtAddPlacedLight(lights, pairRadiance);
        float voxelGate = clamp(max(drtVoxelLight.r, max(drtVoxelLight.g, drtVoxelLight.b)) * 32.0, 0.0, 1.0);
        drtTerrainPlacedRadiance = drtPlacedSumRadiance(lights) * (0.5 * weight * voxelGate);
        visibility *= 0.5;
    }
    // Only covered voxel blocklight is occluded. Sun, moving lights, emission,
    // alpha and OIT revealage retain their existing independent contributions.
    drtTerrainPlacedVisibility = 1.0 - coverage * weight * (1.0 - visibility);
#endif
#endif
}

void drtPrepareTerrainPlacedVisibility(vec3 worldPos) {
    drtPrepareTerrainPlacedVisibility(worldPos, false, 0.0);
}

#endif
