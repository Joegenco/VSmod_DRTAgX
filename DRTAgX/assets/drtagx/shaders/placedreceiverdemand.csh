#version 430 core
// Every real depth pixel contributes. The bitmap remains complete even if the
// bounded exact-bounds feedback fills up; overflow cells use coarse admission.
layout(local_size_x = 64) in;
layout(std430, binding = 5) buffer DrtReceiverDemandScratch {
    uint occupied[8192]; uint cellBounds[];
};
layout(std430, binding = 6) buffer DrtReceiverDemand {
    uvec4 control; uint demand[8192]; uvec4 boundsRecords[];
};
const uint EMPTY = 0xffffffffu;
const int AXIS = 64;
const uint MAX_BOUNDS = 16384u;

#ifdef DRT_DEMAND_COMPACT
void main()
{
    uint cell = gl_GlobalInvocationID.x;
    if (cell < 8192u) demand[cell] = occupied[cell];
    if (cell >= 262144u || (occupied[cell >> 5u] & (1u << (cell & 31u))) == 0u) return;
    uint record = atomicAdd(control.w, 1u);
    if (record >= MAX_BOUNDS) return;
    uint at = cell * 6u;
    // Reversed minima permit zero-clear plus unsigned atomicMax in the collector.
    boundsRecords[record * 2u] = uvec4(cell, 128u - cellBounds[at],
        128u - cellBounds[at + 1u], 128u - cellBounds[at + 2u]);
    boundsRecords[record * 2u + 1u] = uvec4(cellBounds[at + 3u],
        cellBounds[at + 4u], cellBounds[at + 5u], 0u);
}
#else
uniform sampler2D terrainDepth;
uniform ivec2 dimensions;
// Nine integer partitions preserve full viewport projection coordinates. Their
// world-space scratch is cleared once and published only after all nine tiles.
uniform ivec2 pixelOrigin, pixelExtent;
uniform mat4 inverseProjection;
uniform mat4 inverseViewRotation;
uniform vec3 cameraInGrid;
shared uint cells[64];
shared uint minima[192], maxima[192];
shared uint invalidPixels, outsidePixels, receiverPixels;

void mergeGlobal(uint cell, uvec3 lo, uvec3 hi)
{
    uint at = cell * 6u;
    for (uint axis = 0u; axis < 3u; ++axis)
    {
        atomicMax(cellBounds[at + axis], 128u - lo[axis]);
        atomicMax(cellBounds[at + axis + 3u], hi[axis]);
    }
    atomicOr(occupied[cell >> 5u], 1u << (cell & 31u));
}

void mergeCell(uint cell, uvec3 lo, uvec3 hi)
{
    uint at = (cell * 2654435761u) & 63u;
    for (uint probe = 0u; probe < 8u; ++probe)
    {
        uint index = (at + probe) & 63u;
        uint old = atomicCompSwap(cells[index], EMPTY, cell);
        if (old != EMPTY && old != cell) continue;
        for (uint axis = 0u; axis < 3u; ++axis)
        {
            atomicMax(minima[index * 3u + axis], 128u - lo[axis]);
            atomicMax(maxima[index * 3u + axis], hi[axis]);
        }
        return;
    }
    // A full shared set is a bandwidth fallback, never a lost receiver.
    mergeGlobal(cell, lo, hi);
}

void main()
{
    uint lane = gl_LocalInvocationID.x;
    cells[lane] = EMPTY;
    for (uint axis = 0u; axis < 3u; ++axis)
    { minima[lane * 3u + axis] = 0u; maxima[lane * 3u + axis] = 0u; }
    if (lane == 0u) { invalidPixels = 0u; outsidePixels = 0u; receiverPixels = 0u; }
    barrier();
    ivec2 first = pixelOrigin + ivec2(gl_WorkGroupID.xy) * 16;
    uint invalid = 0u, outside = 0u, receivers = 0u, pending = EMPTY;
    uvec3 pendingLo = uvec3(128u), pendingHi = uvec3(0u);
    for (uint pixel = lane; pixel < 256u; pixel += 64u)
    {
        ivec2 xy = first + ivec2(int(pixel & 15u), int(pixel >> 4u));
        if (any(greaterThanEqual(xy, dimensions)) || any(greaterThanEqual(xy, pixelOrigin + pixelExtent))) continue;
        float depth = texelFetch(terrainDepth, xy, 0).r;
        if (depth == 1.0) continue;
        if (isnan(depth) || isinf(depth) || depth < 0.0 || depth > 1.0) { invalid++; continue; }
        vec2 ndc = (vec2(xy) + 0.5) / vec2(dimensions) * 2.0 - 1.0;
        vec4 eye = inverseProjection * vec4(ndc, depth * 2.0 - 1.0, 1.0);
        if (any(isnan(eye)) || any(isinf(eye)) || eye.w <= 1e-8) { invalid++; continue; }
        vec3 p = (inverseViewRotation * vec4(eye.xyz / eye.w, 0.0)).xyz + cameraInGrid;
        if (any(isnan(p)) || any(isinf(p))) { invalid++; continue; }
        ivec3 region = ivec3(floor(p / 8.0));
        if (any(lessThan(region, ivec3(0))) || any(greaterThanEqual(region, ivec3(AXIS)))) { outside++; continue; }
        uint cell = uint((region.z * AXIS + region.y) * AXIS + region.x);
        vec3 local = clamp((p - vec3(region) * 8.0) * 16.0, vec3(0), vec3(128));
        uvec3 lo = uvec3(floor(local)), hi = uvec3(ceil(local));
        receivers++;
        if (pending != EMPTY && pending != cell)
        { mergeCell(pending, pendingLo, pendingHi); pendingLo = uvec3(128u); pendingHi = uvec3(0u); }
        pending = cell; pendingLo = min(pendingLo, lo); pendingHi = max(pendingHi, hi);
    }
    if (pending != EMPTY) mergeCell(pending, pendingLo, pendingHi);
    atomicAdd(invalidPixels, invalid); atomicAdd(outsidePixels, outside); atomicAdd(receiverPixels, receivers);
    barrier();
    uint cell = cells[lane];
    if (cell != EMPTY)
        mergeGlobal(cell, uvec3(128u - minima[lane * 3u], 128u - minima[lane * 3u + 1u],
            128u - minima[lane * 3u + 2u]),
            uvec3(maxima[lane * 3u], maxima[lane * 3u + 1u], maxima[lane * 3u + 2u]));
    if (lane == 0u)
    { atomicAdd(control.x, invalidPixels); atomicAdd(control.y, outsidePixels); atomicAdd(control.z, receiverPixels); }
}
#endif
