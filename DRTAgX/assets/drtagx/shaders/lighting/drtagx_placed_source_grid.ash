// Forward-only world cells. The native Use/Stop bridge borrows/restores SSBO 5;
// deferred programs retain their independent 16x16 screen/depth masks there.
layout(std430, binding = 5) readonly buffer DrtTerrainPlacedCells { uint drtTerrainPlacedCellMasks[]; };
uniform int drtTerrainPlacedCellsReady = 0;
uniform vec3 drtTerrainPlacedCellsMin;

int drtTerrainPlacedCellBase(vec3 receiverInSourceCamera) {
    if (drtTerrainPlacedCellsReady == 0) return -1;
    // Both source records and the minimum share SourceCamera's world axes.
    // This also handles early wind fills using the previous published snapshot.
    vec3 cell = floor((receiverInSourceCamera - drtTerrainPlacedCellsMin) * 0.125);
    if (!all(greaterThanEqual(cell, vec3(0.0))) || !all(lessThan(cell, vec3(32.0)))) return -1;
    ivec3 c = ivec3(cell);
    return ((c.z * 32 + c.y) * 32 + c.x) * 5;
}

struct DrtTerrainPlacedCellMask { uvec4 low; uint high; };

bool drtLoadTerrainPlacedCellMask(vec3 receiverInSourceCamera, int count,
                                out DrtTerrainPlacedCellMask mask) {
    mask.low = uvec4(0u); mask.high = 0u;
    // Small sets and unavailable cells retain the original straight source loop.
    // Decide once per receiver, rather than branching in every source iteration.
    if (count <= 8) return false;
    int cellBase = drtTerrainPlacedCellBase(receiverInSourceCamera);
    if (cellBase < 0) return false;
    mask.low = uvec4(drtTerrainPlacedCellMasks[cellBase], drtTerrainPlacedCellMasks[cellBase + 1],
                    drtTerrainPlacedCellMasks[cellBase + 2], drtTerrainPlacedCellMasks[cellBase + 3]);
    mask.high = drtTerrainPlacedCellMasks[cellBase + 4];
    ivec4 bits = bitCount(mask.low);
    int candidates = bits.x + bits.y + bits.z + bits.w + bitCount(mask.high);
    // Exact all-source evaluation is cheaper when little work can be rejected.
    return candidates * 4 < count * 3;
}

