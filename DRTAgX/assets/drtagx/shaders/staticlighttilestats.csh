#version 430 core
// Opt-in diagnostics only. Stratified samples of actual five-word masks avoid
// per-fragment atomics and a full-grid readback. Counts include record 128.
layout(local_size_x = 64) in;
layout(std430, binding = 5) readonly buffer Tiles { uint masks[]; };
layout(std430, binding = 7) writeonly buffer Counts { uint counts[]; };
uniform int tileCount, sampleCount, sampleOffset;
void main()
{
    uint sampleIndex = gl_GlobalInvocationID.x;
    if (sampleIndex >= uint(sampleCount)) return;
    uint tile = (sampleIndex * uint(tileCount) / uint(sampleCount) + uint(sampleOffset)) % uint(tileCount);
    uint count = 0u;
    for (uint word = 0u; word < 5u; ++word) count += uint(bitCount(masks[tile * 5u + word]));
    counts[sampleIndex] = count;
}
