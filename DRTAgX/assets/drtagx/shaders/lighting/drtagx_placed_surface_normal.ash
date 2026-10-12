#ifndef DRT_PLACED_SURFACE_NORMAL
#define DRT_PLACED_SURFACE_NORMAL

// Native terrain bits13..24 contain three sign/magnitude nibbles. Reinterpret
// their 12 bits as finite negative half values (-16,-1], so RGBA16F preserves
// every token exactly. Ordinary opaque/topsoil own this payload only; native
// SSAO/GTAO use W's positive sign for foliage and keep their existing behavior.
float drtPackPlacedSurfaceNormal(uint token) {
    return -unpackHalf2x16(0x3c00u + (token & 4095u)).x;
}

uint drtPlacedSurfaceNormalToken(float payload) {
    return (packHalf2x16(vec2(abs(payload), 0.0)) & 65535u) - 0x3c00u;
}

vec3 drtPlacedSurfaceNormalVector(uint token) {
    vec3 magnitude = vec3(float((token >> 1u) & 7u), float((token >> 5u) & 7u), float((token >> 9u) & 7u));
    vec3 sign = vec3((token & 1u) != 0u ? -1.0 : 1.0,
        (token & 16u) != 0u ? -1.0 : 1.0, (token & 256u) != 0u ? -1.0 : 1.0);
    return magnitude * sign;
}

bool drtDecodePlacedSurfaceNormal(float payload, bool ordinaryReceiver, out vec3 normal) {
    normal = vec3(0.0);
    // The receiver class is essential: negative no-cull sunlight payloads
    // overlap this numerical range and must never be interpreted as normals.
    if (!ordinaryReceiver || payload > -1.0 || payload <= -16.0) return false;
    uint token = drtPlacedSurfaceNormalToken(payload);
    if (token > 4095u) return false;
    vec3 vector = drtPlacedSurfaceNormalVector(token);
    float n2 = dot(vector, vector);
    if (n2 <= 0.0) return false; // Zero magnitude is not a receiving plane.
    normal = vector * inversesqrt(n2);
    return true;
}

uint drtPlacedSurfacePlaneToken(uint token, vec3 actualWorldPlane) {
    vec3 native = drtPlacedSurfaceNormalVector(token);
    vec3 a = abs(native);
    int major = a.x >= a.y && a.x >= a.z ? 0 : a.y >= a.z ? 1 : 2;
    if (token > 4095u || a[major] <= 0.0 || dot(actualWorldPlane, actualWorldPlane) <= 1e-12 ||
        any(isnan(actualWorldPlane)) || any(isinf(actualWorldPlane)) || abs(actualWorldPlane[major]) <= 1e-6)
        return 0u;
    ivec2 tangent = major == 0 ? ivec2(1, 2) : major == 1 ? ivec2(0, 2) : ivec2(0, 1);
    vec2 nativeSlope = vec2(native[tangent.x], native[tangent.y]) / native[major];
    vec2 actualSlope = vec2(actualWorldPlane[tangent.x], actualWorldPlane[tangent.y]) / actualWorldPlane[major];
    vec2 difference = abs(actualSlope - nativeSlope);
    // A tangent node has at most 1/8-block receiver support. Bounding the L1
    // slope error to 1/512 bounds its depth drift to 1/4096 block, one quarter
    // of the fine-plane step. Ratios ignore raster winding, unlike a signed dot.
    // Approximate shape normals fall back to native light instead of churning keys.
    return difference.x + difference.y <= 1.0 / 512.0 ? token : 0u;
}
#endif
