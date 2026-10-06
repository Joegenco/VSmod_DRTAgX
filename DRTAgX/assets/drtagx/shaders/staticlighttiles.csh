#version 430 core
// Coarse: one invocation/tile. Depth-aware: one workgroup/tile. Both overwrite
// every mask word, including empty tiles and the staging-record word.
layout(local_size_x = 64) in;
layout(std430, binding = 5) writeonly buffer TileMasks { uint masks[]; };
layout(std430, binding = 6) readonly buffer TileRectangles { ivec4 rectangles[]; };
uniform int sourceCount;
uniform int tileWidth;
uniform int tileCount;

#ifdef DRT_DEPTH_CULL
// Same source records as deferred lighting: eye position/range is vec4 zero.
layout(std430, binding = 4) readonly buffer Sources { vec4 sources[]; };
uniform sampler2D terrainDepth;
uniform mat4 inverseProjection;
shared float minimumDepth[64];
shared float maximumDepth[64];
shared vec3 receiverMinimum;
shared vec3 receiverMaximum;
shared uint volumeState; // 0 empty, 1 finite conservative volume, 2 fallback.
shared uint outputBits[5];

void main()
{
    uint tile = gl_WorkGroupID.x + gl_WorkGroupID.y * gl_NumWorkGroups.x;
    uint lane = gl_LocalInvocationID.x;
    if (tile >= uint(tileCount)) return; // Uniform across this workgroup.
    ivec2 xy = ivec2(int(tile) % tileWidth, int(tile) / tileWidth);
    ivec2 size = textureSize(terrainDepth, 0);
    float nearest = 1.0, farthest = 0.0;
    if (lane == 0u) {
        volumeState = 0u;
        receiverMinimum = vec3(0.0); receiverMaximum = vec3(0.0);
    }
    if (lane < 5u) outputBits[lane] = 0u;
    barrier();
    for (uint pixel = lane; pixel < 256u; pixel += 64u) {
        ivec2 p = xy * 16 + ivec2(int(pixel & 15u), int(pixel >> 4u));
        if (any(greaterThanEqual(p, size))) continue;
        float d = texelFetch(terrainDepth, p, 0).r;
        if (isnan(d) || isinf(d) || d < 0.0 || d > 1.0) atomicOr(volumeState, 2u);
        // Only exact clear depth is empty; no epsilon may erase far terrain.
        if (d >= 0.0 && d < 1.0) { nearest = min(nearest, d); farthest = max(farthest, d); }
    }
    minimumDepth[lane] = nearest; maximumDepth[lane] = farthest;
    barrier();
    for (uint stride = 32u; stride > 0u; stride >>= 1u) {
        if (lane < stride) {
            minimumDepth[lane] = min(minimumDepth[lane], minimumDepth[lane + stride]);
            maximumDepth[lane] = max(maximumDepth[lane], maximumDepth[lane + stride]);
        }
        barrier();
    }
    if (lane == 0u && volumeState != 2u && minimumDepth[0] < 1.0) {
        volumeState = 1u;
        receiverMinimum = vec3(1e30); receiverMaximum = vec3(-1e30);
        // Every covered pixel lies inside these tile-edge/depth corners for a
        // standard perspective projection. Invalid inversions retain 2D masks.
        for (int corner = 0; corner < 8; ++corner) {
            vec2 p = vec2(xy * 16) + vec2((corner & 1) != 0 ? 16.0 : 0.0, (corner & 2) != 0 ? 16.0 : 0.0);
            vec2 uv = min(p, vec2(size)) / vec2(size);
            float d = (corner & 4) != 0 ? maximumDepth[0] : minimumDepth[0];
            vec4 point = inverseProjection * vec4(uv * 2.0 - 1.0, d * 2.0 - 1.0, 1.0);
            vec3 eye = point.xyz / point.w;
            // Positive homogeneous w throughout the interval guarantees that
            // the eight corners enclose all reconstructed receivers.
            if (point.w <= 1e-8 || any(isnan(eye)) || any(isinf(eye))) volumeState = 2u;
            receiverMinimum = min(receiverMinimum, eye); receiverMaximum = max(receiverMaximum, eye);
        }
        // Pad for float reconstruction and fragment/texel-centre rounding.
        vec3 pad = vec3(0.01) + max(abs(receiverMinimum), abs(receiverMaximum)) * 1e-5;
        receiverMinimum -= pad; receiverMaximum += pad;
    }
    barrier();
    if (volumeState != 0u) {
        for (uint i = lane; i < uint(sourceCount); i += 64u) {
            ivec4 bounds = rectangles[i];
            if (any(lessThan(xy, bounds.xy)) || any(greaterThan(xy, bounds.zw))) continue;
            vec4 light = sources[i * 4u];
            vec3 outside = max(max(receiverMinimum - light.xyz, light.xyz - receiverMaximum), vec3(0.0));
            if (volumeState == 2u || dot(outside, outside) <= light.w * light.w)
                atomicOr(outputBits[i >> 5u], 1u << (i & 31u));
        }
    }
    barrier();
    if (lane < 5u) masks[tile * 5u + lane] = outputBits[lane];
}
#else

void main()
{
    uint tile = gl_GlobalInvocationID.x;
    if (tile >= uint(tileCount)) return;
    ivec2 xy = ivec2(int(tile) % tileWidth, int(tile) / tileWidth);
    uint bits[5] = uint[5](0u, 0u, 0u, 0u, 0u);
    for (int i = 0; i < sourceCount; ++i) {
        ivec4 bounds = rectangles[i]; // inclusive minX/minY/maxX/maxY
        if (all(greaterThanEqual(xy, bounds.xy)) && all(lessThanEqual(xy, bounds.zw)))
            bits[i >> 5] |= 1u << uint(i & 31);
    }
    for (int word = 0; word < 5; ++word) masks[tile * 5u + uint(word)] = bits[word];
}
#endif
