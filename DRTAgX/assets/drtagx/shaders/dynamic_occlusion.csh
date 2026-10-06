#version 430 core

// A chunk of block IDs is classified entirely on the GPU. One texel represents one
// world block; the second pass marks 4^3 bricks that contain any opaque cell.
layout(local_size_x = 4, local_size_y = 4, local_size_z = 4) in;
layout(std430, binding = 5) readonly buffer ChunkIds { int blockIds[]; };
layout(std430, binding = 6) readonly buffer BlockOpacity { uint opaqueById[]; };
layout(r8ui, binding = 0) uniform uimage3D fineMask;
layout(r8ui, binding = 1) uniform uimage3D brickMask;
uniform ivec3 chunkOffset;
uniform int opacityCount;
uniform int passIndex;

void main() {
    ivec3 p = ivec3(gl_GlobalInvocationID.xyz);
    if (passIndex == 0) {
        if (any(greaterThanEqual(p, ivec3(32)))) return;
        // Vintage Story chunk index: X + 32 * (Z + 32 * Y).
        int index = p.x + 32 * (p.z + 32 * p.y);
        int id = blockIds[index];
        uint opaque = id >= 0 && id < opacityCount ? opaqueById[id] : 1u;
        imageStore(fineMask, chunkOffset + p, uvec4(opaque, 0u, 0u, 0u));
    } else {
        if (any(greaterThanEqual(p, ivec3(8)))) return;
        ivec3 baseCell = chunkOffset + p * 4;
        uint occupied = 0u;
        for (int z = 0; z < 4 && occupied == 0u; z++)
        for (int y = 0; y < 4 && occupied == 0u; y++)
        for (int x = 0; x < 4; x++) {
            occupied |= imageLoad(fineMask, baseCell + ivec3(x, y, z)).r;
        }
        imageStore(brickMask, chunkOffset / 4 + p, uvec4(occupied, 0u, 0u, 0u));
    }
}
