// DRTAgX deferred include: Cached projected-depth sampling, face seams and receiver bias.

// Precomputed projection constants (near = 0.1, far = 22.0)
const float STATIC_DEPTH_A = -1.0091324;
const float STATIC_DEPTH_B = -0.2009132;

// Surface shadow cells are 1/32 block; the GUI controls their use for all shadow types.
const float STATIC_SHADOW_GRID_CELLS = 32.0;
// Keep four bilinear comparison samples, with a tighter contact footprint.
const float STATIC_SHADOW_FILTER_RADIUS = 0.5;

// A coplanar slab/chisel surface should remain unlit despite tiny sign changes
// from view/depth reconstruction. Ramp out of that dead band without a hard pop.
const float STATIC_GRAZING_MIN = 0.001;
const float STATIC_GRAZING_MAX = 0.01;

vec3 drtStaticGridOffset(vec3 receiverFromAnchor, vec3 normalWorld)
{
    // Every placed source is block-centred: its coordinates are integer + .5.
    // At 32 or 16 cells/block any source is an exact world-grid anchor. Working
    // relative to one prepared source avoids rounding large absolute floats.
    vec3 centre = (floor(receiverFromAnchor * STATIC_SHADOW_GRID_CELLS) + 0.5)
        / STATIC_SHADOW_GRID_CELLS;
    vec3 offset = centre - receiverFromAnchor;
    // Move along the unit-normal receiver plane, preserving thin/chisel depth.
    // Axis-aligned faces snap only their two tangential coordinates.
    return offset - normalWorld * dot(normalWorld, offset);
}

float drtStaticFaceVisibility(vec3 receiverWorld, vec3 planeGradient, int face, int slot)
{
    vec2 side;
    vec2 gradient;
    float forward;

    switch (face) {
        case 0: side = vec2( receiverWorld.z, -receiverWorld.y); gradient = vec2( planeGradient.z, -planeGradient.y); forward =  receiverWorld.x; break;
        case 1: side = vec2(-receiverWorld.z, -receiverWorld.y); gradient = vec2(-planeGradient.z, -planeGradient.y); forward = -receiverWorld.x; break;
        case 2: side = vec2(-receiverWorld.x,  receiverWorld.z); gradient = vec2(-planeGradient.x,  planeGradient.z); forward =  receiverWorld.y; break;
        case 3: side = vec2(-receiverWorld.x, -receiverWorld.z); gradient = vec2(-planeGradient.x, -planeGradient.z); forward = -receiverWorld.y; break;
        case 4: side = vec2(-receiverWorld.x, -receiverWorld.y); gradient = vec2(-planeGradient.x, -planeGradient.y); forward =  receiverWorld.z; break;
        default: side = vec2( receiverWorld.x, -receiverWorld.y); gradient = vec2( planeGradient.x, -planeGradient.y); forward = -receiverWorld.z; break;
    }

    if (forward <= 0.1) return 1.0;

    vec2 faceUv = vec2(0.5) + 0.47 * side / forward;
    if (any(lessThan(faceUv, vec2(0.0))) || any(greaterThan(faceUv, vec2(1.0)))) return 1.0;

    // Compare projected raster depth, not radial distance/range. This is the
    // exact native perspective mapping for near=0.1 and far=22, with depth bias.
    float depth = 0.5 * (1.0 - STATIC_DEPTH_A + STATIC_DEPTH_B / forward) - 0.0001;
    vec2 texel = 1.0 / vec2(textureSize(drtStaticMaps, 0).xy);
    // Bilinear hardware PCF shares one reference across its 2x2 footprint.
    // One texel of planar depth variation protects that footprint, while the
    // explicit tap offsets below follow the plane instead of shadowing it.
    depth -= dot(abs(gradient), texel);
    vec2 lowUv = clamp(faceUv - STATIC_SHADOW_FILTER_RADIUS * texel, texel * 0.5, vec2(1.0) - texel * 0.5);
    vec2 highUv = clamp(faceUv + STATIC_SHADOW_FILTER_RADIUS * texel, texel * 0.5, vec2(1.0) - texel * 0.5);
    // Share the two axis corrections between all four taps. Actual clamped
    // offsets keep secondary-face edge comparisons on the same surface plane.
    vec2 lowDepth = gradient * (lowUv - faceUv);
    vec2 highDepth = gradient * (highUv - faceUv);
    float layer = float(slot * 6 + face);
    float visibility = texture(drtStaticMaps, vec4(lowUv, layer, depth + lowDepth.x + lowDepth.y));
    visibility += texture(drtStaticMaps, vec4(highUv.x, lowUv.y, layer, depth + highDepth.x + lowDepth.y));
    visibility += texture(drtStaticMaps, vec4(lowUv.x, highUv.y, layer, depth + lowDepth.x + highDepth.y));
    visibility += texture(drtStaticMaps, vec4(highUv, layer, depth + highDepth.x + highDepth.y));
    return visibility * 0.25;
}

float drtStaticVisibilityAtSlot(vec3 qWorld, vec3 planeGradient, int slot)
{
    int face = drtCubeFace(qWorld);
    float primary = drtStaticFaceVisibility(qWorld, planeGradient, face, slot);

    vec3 axes = abs(qWorld);
    float major = max(axes.x, max(axes.y, axes.z));
    // Branchless median calculation to find the second-largest (runner-up) axis
    float runnerUp = max(min(axes.x, axes.y), max(min(axes.y, axes.z), min(axes.x, axes.z)));

    float delta = major - runnerUp;
    float blendRange = max(major * 0.03, 1e-5); // Protect against zero-division NaN

    // Return early if not near a cube face seam
    if (delta >= blendRange) return primary;

    // Mask out the major axis to find the secondary face
    vec3 maskedWorld = qWorld;
    if (face < 2) maskedWorld.x = 0.0;
    else if (face < 4) maskedWorld.y = 0.0;
    else maskedWorld.z = 0.0;

    int second = drtCubeFace(maskedWorld);
    float secondary = drtStaticFaceVisibility(qWorld, planeGradient, second, slot);

    return mix(secondary, primary, 0.5 + 0.5 * smoothstep(0.0, blendRange, delta));
}

// Two-sided terrain receives by depth occlusion only. Normals correct the
// receiver plane and precision offset; they never attenuate its brightness.
float drtStaticTwoSidedVisibilityWithTolerance(vec3 receiverWorld, vec3 normalWorld,
    int slot, int secondarySlot, float shadowBlend, float receiverTolerance)
{
    float distance = length(receiverWorld);
    if (distance < 0.12) return 1.0;
    float n2 = dot(normalWorld, normalWorld);
    vec3 normal = n2 > 1e-6 ? normalWorld * inversesqrt(n2) : -receiverWorld / distance;
    float planeDistance = dot(normal, receiverWorld);
    if (planeDistance > 0.0) { normal = -normal; planeDistance = -planeDistance; }
    // Near-parallel planes have no stable depth slope. Use bounded precision
    // bias there instead of a normal-facing visibility gate or an infinite slope.
    vec3 gradient = abs(planeDistance) > 0.001 * distance
        ? normal * (STATIC_DEPTH_B / (0.94 * planeDistance)) : vec3(0.0);
    if (receiverTolerance > 0.0) {
        // Finite wind cards must not erase crossing blades with an unbounded
        // grazing slope. For depth C+B/(2*f), |dDepth/df|=|B|/(2*f*f).
        // Limit the bilinear slope allowance to 1/64 block in projected depth;
        // four PCF taps and the independent wind contact tolerance stay intact.
        float forward = max(abs(receiverWorld.x), max(abs(receiverWorld.y), abs(receiverWorld.z)));
        vec2 texel = 1.0 / vec2(textureSize(drtStaticMaps, 0).xy);
        float maximumFootprint = 0.5 * abs(STATIC_DEPTH_B) / (64.0 * max(forward * forward, 0.01));
        float footprint = dot(abs(gradient), vec3(1.0)) * max(texel.x, texel.y);
        gradient *= maximumFootprint / max(maximumFootprint, max(footprint, 1e-20));
    }
    // Wind cards permit an extra 2 cm of contact depth. Static furniture and
    // other no-cull receivers retain the original precision bias/self-shadowing.
    vec3 q = receiverWorld * (1.0 - (0.01 + receiverTolerance) / distance) + normal * 0.003;
    float visibility = drtStaticVisibilityAtSlot(q, gradient, slot);
    if (shadowBlend <= 0.0) return visibility;
    return mix(visibility, drtStaticVisibilityAtSlot(q, gradient, secondarySlot), shadowBlend);
}

float drtStaticTwoSidedVisibility(vec3 receiverWorld, vec3 normalWorld,
    int slot, int secondarySlot, float shadowBlend)
{
    return drtStaticTwoSidedVisibilityWithTolerance(receiverWorld, normalWorld,
        slot, secondarySlot, shadowBlend, 0.0);
}

float drtStaticVisibility(vec3 receiverWorld, vec3 normalWorld, float distance, float planeDistance,
    int slot, int secondarySlot, float shadowBlend)
{
    if (distance < 0.12) return 1.0;

    float invDistance = 1.0 / distance;
    float NdotL = -planeDistance * invDistance;
    // The 28% facing floor in placed-light radiance otherwise amplifies an
    // almost-zero NdotL flipping across zero into a visible on/off shadow.
    if (NdotL <= STATIC_GRAZING_MIN) return 0.0;
    float grazingVisibility = smoothstep(STATIC_GRAZING_MIN, STATIC_GRAZING_MAX, NdotL);

    // Reuse the caller's light-relative world position, distance and unit normal;
    // the plane correction now covers the filter slope, so only a 3 mm normal
    // precision offset is needed instead of the old 12..70 mm heuristic. Retain
    // the 1 cm shift toward the emitter and projected-depth precision margin.
    vec3 qWorld = receiverWorld * (1.0 - 0.01 * invDistance) + normalWorld * 0.003;

    // For the receiving plane n.q = d and face projection uv = .5 + .47*side/f,
    // depth = C + B/(2*f) is affine in uv with gradient B*nSide/(.94*d).
    // Use the original surface plane; receiver offsets must not tilt its slope.
    // Front-facing receivers already have d < 0; guard numerical degeneracy.
    planeDistance = min(planeDistance, -0.00001);
    vec3 planeGradient = normalWorld * (STATIC_DEPTH_B / (0.94 * planeDistance));
    float oldVisibility = drtStaticVisibilityAtSlot(qWorld, planeGradient, slot);

    // Skip secondary slot evaluation when shadowBlend is inactive (<= 0.0)
    if (shadowBlend <= 0.0) return oldVisibility * grazingVisibility;

    float newVisibility = drtStaticVisibilityAtSlot(qWorld, planeGradient, secondarySlot);
    return mix(oldVisibility, newVisibility, shadowBlend) * grazingVisibility;
}
