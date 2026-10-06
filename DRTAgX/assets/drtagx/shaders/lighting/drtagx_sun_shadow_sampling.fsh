// World-aligned 1/32-block cells, projected onto the actual receiving plane.
// This adds arithmetic only; dynamic sun maps and the existing PCF tap count stay native.
vec3 drtSunGridOffset(vec3 positionFromCamera, vec3 normalWorld, vec3 cameraPhase) {
    vec3 p = positionFromCamera + cameraPhase;
    vec3 offset = (floor(p * 32.0) + 0.5) / 32.0 - p;
    float n2 = dot(normalWorld, normalWorld);
    return n2 > 0.0001 ? offset - normalWorld * (dot(normalWorld, offset) / n2) : vec3(0.0);
}

vec3 drtSunPrimitiveNormal(vec3 position, vec3 fallbackNormal) {
    // Derivatives of interpolated geometry describe this wind-bent triangle,
    // including helper lanes. Evaluate before alpha discard or deferred branching.
    vec3 dx = dFdx(position), dy = dFdy(position);
    float dx2 = dot(dx, dx), dy2 = dot(dy, dy);
    if (min(dx2, dy2) <= 1e-30) return fallbackNormal;
    // Test angular degeneracy after removing pixel footprint scale. Close-up
    // grass can have tiny valid derivatives; an absolute area cutoff makes
    // its receiving plane switch to the unbent authored normal within a face.
    vec3 plane = cross(dx * inversesqrt(dx2), dy * inversesqrt(dy2));
    float n2 = dot(plane, plane);
    if (n2 <= 1e-12) return fallbackNormal;
    plane *= inversesqrt(n2);
    // Screen derivatives face the camera. Raster winding recovers the same
    // physical face on either side, including when wind bends it past grazing.
    // Native shade:false cards can have synthetic +Y material normals even on
    // vertical leaves: orienting to those flips the whole plane as its Y crosses
    // zero. Keep authored normals only for degenerate geometry and sun shading.
    return gl_FrontFacing ? plane : -plane;
}

float drtSunPackFoliageLighting(float shade, float windClass) {
    // Normal W keeps the native SSAO sign (positive only for wind backlight).
    // Disjoint intervals store the authored sunlight shade with the verified
    // 0/.5/1 backlight class: [-3,-2], [2,3], [4,5]. Half-float safe; no new MRT.
    shade = clamp(shade, 0.0, 1.0);
    return windClass > 0.75 ? 4.0 + shade :
        windClass > 0.001 ? 2.0 + shade : -2.0 - shade;
}

vec2 drtSunUnpackFoliageLighting(float payload) {
    float base = payload > 3.5 ? 4.0 : 2.0;
    return vec2(clamp(abs(payload) - base, 0.0, 1.0),
        payload > 3.5 ? 1.0 : payload > 0.0 ? 0.5 : 0.0);
}

vec2 drtSunPlaneGradient(mat4 shadowMatrix, vec3 normalWorld) {
    // Native sun projections are orthographic: the three scaled rotation rows
    // form orthogonal axes. Transform the plane covector by their reciprocal
    // scales, then solve n.u*du + n.v*dv + n.z*dz = 0. No screen helper lanes.
    vec3 u = vec3(shadowMatrix[0].x, shadowMatrix[1].x, shadowMatrix[2].x);
    vec3 v = vec3(shadowMatrix[0].y, shadowMatrix[1].y, shadowMatrix[2].y);
    vec3 z = vec3(shadowMatrix[0].z, shadowMatrix[1].z, shadowMatrix[2].z);
    vec3 plane = vec3(dot(normalWorld, u) / max(dot(u, u), 1e-20),
        dot(normalWorld, v) / max(dot(v, v), 1e-20),
        dot(normalWorld, z) / max(dot(z, z), 1e-20));
    if (abs(plane.z) <= length(plane) * 1e-4) return vec2(0.0);
    return -plane.xy / plane.z;
}

// Set only for no-cull terrain cards; material shading and solid receivers keep
// their existing contracts. Both forward and deferred classify the same cards.
bool drtSunFoliageReceiver = false;

vec2 drtSunFoliagePlaneGradient(mat4 shadowMatrix, sampler2DShadow depthMap,
    vec3 normalWorld) {
    vec2 gradient = drtSunPlaneGradient(shadowMatrix, normalWorld);
    vec2 texel = 1.0 / vec2(textureSize(depthMap, 0));
    vec3 depthAxis = vec3(shadowMatrix[0].z, shadowMatrix[1].z, shadowMatrix[2].z);
    // dz/du diverges as wind rotates a card parallel to the sun. Extrapolating
    // that infinite plane across a map texel can bias a finite blade through
    // metres of unrelated occluders. Limit the slope footprint to 1/64 block:
    // tap correction plus the shared bilinear margin then moves the reference
    // by at most one 1/32-block cell (in addition to the precision margin).
    float maximumFootprint = length(depthAxis) / 64.0;
    float footprint = dot(abs(gradient), texel);
    return gradient * (maximumFootprint / max(maximumFootprint, max(footprint, 1e-20)));
}

// Sun maps only: compare each PCF tap against the depth of the receiving plane.
// Call this before per-pixel cascade branches, where derivatives remain defined.
vec2 drtSunReceiverGradient(vec3 shadowCoord) {
    vec3 dx = dFdx(shadowCoord);
    vec3 dy = dFdy(shadowCoord);
    float det = dx.x * dy.y - dx.y * dy.x;
    float area = sqrt(dot(dx.xy, dx.xy) * dot(dy.xy, dy.xy));
    // An edge-on/degenerate projection has no stable depth-to-UV derivative.
    if (area <= 1e-20 || abs(det) <= area * 1e-4) return vec2(0.0);
    vec2 gradient = vec2(dy.y * dx.z - dx.y * dy.z,
        dx.x * dy.z - dy.x * dx.z) / det;
    return any(isnan(gradient)) || any(isinf(gradient)) ? vec2(0.0) : gradient;
}

float drtSunShadowVisibility(sampler2DShadow depthMap, vec3 shadowCoord,
    vec2 gradient, float precisionBias, float receiverBiasScale) {
    vec2 texel = 1.0 / vec2(textureSize(depthMap, 0));
    // Hardware bilinear comparisons share one reference within their footprint.
    // Allow at most one texel of planar depth variation plus a small precision
    // margin, rather than moving every solid receiver by the old large constant.
    // Thin receivers need a larger precision margin, not four times the
    // planar footprint. That excessive slope bias can leak through a sun blocker.
    float bias = precisionBias * receiverBiasScale + dot(abs(gradient), texel);
    float visibility = 0.0;
    for (int y = -1; y <= 1; ++y) {
        for (int x = -1; x <= 1; ++x) {
            vec2 offset = vec2(x, y) * texel;
            float reference = shadowCoord.z + dot(gradient, offset) - bias;
            visibility += texture(depthMap, vec3(shadowCoord.xy + offset, reference));
        }
    }
    return visibility / 9.0;
}
