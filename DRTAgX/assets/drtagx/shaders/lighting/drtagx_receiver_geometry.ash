#ifndef DRT_RECEIVER_GEOMETRY
#define DRT_RECEIVER_GEOMETRY
#include drtagx_foliage_receiver.ash
#ifndef DRT_RECEIVER_VERTEX
#include drtagx_placed_surface_normal.ash
#endif
// Authored plane ABI: N is the native unnormalized signed-magnitude normal;
// plane = dot(N, world - fixed integer anchor) * 1024. INT_MIN means unknown.
// Integer pool translation is applied before any camera-relative float math.
const int DRT_RECEIVER_INVALID_PLANE = (-2147483647 - 1);
uniform int drtReceiverGeometryEnabled = 0;
uniform ivec3 drtReceiverPoolOffset = ivec3(0);
uniform int drtDeferredIlluminationPayload = 0;
#ifdef DRT_RECEIVER_VERTEX
// Conventional native vertex stages remain GLSL330. Decode the integer vector
// here without importing the fragment carrier's GLSL420 half packing builtins.
vec3 drtReceiverNativeNormalVector(uint token) {
    vec3 m=vec3(float((token>>1u)&7u),float((token>>5u)&7u),float((token>>9u)&7u));
    return m*vec3((token&1u)!=0u?-1.0:1.0,(token&16u)!=0u?-1.0:1.0,(token&256u)!=0u?-1.0:1.0);
}
// Keep flat receiver metadata in whole vectors. The full native shadow/wind
// interface has many interpolants; independent flat scalars waste link slots.
flat out ivec4 drtReceiverPlaneMetadata;
flat out ivec4 drtReceiverBoundsUnits;
out vec2 drtReceiverPlaneVariation;
out vec4 drtReceiverUnbiasedClip;
#define drtReceiverPlaneValue drtReceiverPlaneMetadata.x
#define drtReceiverPlaneReference intBitsToFloat(drtReceiverPlaneMetadata.y)
#define drtReceiverNormalToken uint(drtReceiverPlaneMetadata.z)
#define drtReceiverBoundsValid drtReceiverPlaneMetadata.w
#define drtReceiverPlaneResidual drtReceiverPlaneVariation.x
#define drtReceiverWarp drtReceiverPlaneVariation.y
void drtAuthorReceiverPlane(vec3 local, vec3 reference, uint token, bool staticPrimitive) {
    vec3 n = drtReceiverNativeNormalVector(token);
    drtReceiverPlaneMetadata.z = int(token);
    drtReceiverBoundsUnits = ivec4(0);
    drtReceiverBoundsValid = 0;
    drtReceiverPlaneMetadata.y = floatBitsToInt(dot(n, reference) * 1024.0);
    drtReceiverPlaneResidual = dot(n, local) * 1024.0;
    drtReceiverWarp = 0.0;
    drtReceiverPlaneValue = DRT_RECEIVER_INVALID_PLANE;
    if (drtReceiverGeometryEnabled == 0 || !staticPrimitive || dot(n,n) == 0.0) return;
    // The owner bounds poolOffset so this signed integer dot and *1024 cannot
    // overflow. The local term stays pool-sized, independent of world distance.
    int originPlane = int(n.x) * drtReceiverPoolOffset.x +
        int(n.y) * drtReceiverPoolOffset.y + int(n.z) * drtReceiverPoolOffset.z;
    drtReceiverPlaneValue = originPlane * 1024 + int(round(drtReceiverPlaneReference));
}
void drtAuthorReceiverBounds(vec3 reference,vec3 edgeA,vec3 edgeB,uint token) {
    vec3 n=drtReceiverNativeNormalVector(token),an=abs(n);
    int major=an.x>=an.y&&an.x>=an.z?0:an.y>=an.z?1:2;
    ivec2 axes=major==0?ivec2(1,2):major==1?ivec2(0,2):ivec2(0,1);
    vec2 a=vec2(edgeA[axes.x],edgeA[axes.y]),b=vec2(edgeB[axes.x],edgeB[axes.y]);
    // A tangent bounding rectangle is exact only for these native quads.
    // Sheared projected quads and unsupported VBO triangles remain native.
    bool rectangle=(abs(a.y)<=1e-7 && abs(b.x)<=1e-7 && abs(a.x)>1e-7 && abs(b.y)>1e-7) ||
                   (abs(a.x)<=1e-7 && abs(b.y)<=1e-7 && abs(a.y)>1e-7 && abs(b.x)>1e-7);
    vec2 start=vec2(reference[axes.x],reference[axes.y]);
    vec2 lo=start+min(a,vec2(0))+min(b,vec2(0)),hi=start+max(a,vec2(0))+max(b,vec2(0));
    vec4 units=vec4(lo,hi)*16.0,rounded=round(units);
    bool aligned=all(lessThanEqual(abs(units-rounded),vec4(1.0/8192.0)));
    if(!rectangle || !aligned || any(greaterThan(hi-lo,vec2(32.0))) ||
       abs(dot(n,edgeA))*1024.0>0.25 || abs(dot(n,edgeB))*1024.0>0.25)return;
    ivec2 origin=ivec2(drtReceiverPoolOffset[axes.x],drtReceiverPoolOffset[axes.y])*16;
    drtReceiverBoundsUnits=ivec4(rounded)+ivec4(origin,origin);
    drtReceiverBoundsValid=1;
}
void drtAuthorFoliageReceiver(vec3 local,vec3 reference,vec3 edgeA,vec3 edgeB) {
#ifdef DRT_PERFORMANCE_NO_PLS
    return;
#endif
    if(drtFoliageReceiverEnabled==0 || drtReceiverGeometryEnabled==0)return;
    // Derive an immutable card plane, not a wind-bent derivative or a grass
    // vertex's synthetic lighting normal. Native grass randomizes its rotation;
    // quantize the plane slope to signed-magnitude seven at its fixed center.
    // This bounded rest-pose approximation is safe for a receiver-only card:
    // its own geometry never enters the shadow atlas and cannot cause acne.
    vec3 plane=cross(edgeA,edgeB);float peak=max(abs(plane.x),max(abs(plane.y),abs(plane.z)));
    if(peak<=1e-10)return;
    ivec3 q=ivec3(round(plane*(7.0/peak)));
    uint token=uint(abs(q.x)*2+(q.x<0?1:0))|
        (uint(abs(q.y)*2+(q.y<0?1:0))<<4u)|(uint(abs(q.z)*2+(q.z<0?1:0))<<8u);
    // Centering halves the worst projection error of a randomly rotated card;
    // the same rest reference is used for every vertex and both triangles.
    drtAuthorReceiverPlane(local,reference+0.5*(edgeA+edgeB),token,true);
    if(drtReceiverPlaneValue<=-DRT_FOLIAGE_PLANE_BIAS || drtReceiverPlaneValue>=DRT_FOLIAGE_PLANE_BIAS)return;
    // 2 denotes a rest-pose card; it uses complete one-block field support.
    drtReceiverBoundsValid=2;
}
#else
flat in ivec4 drtReceiverPlaneMetadata;
flat in ivec4 drtReceiverBoundsUnits;
in vec2 drtReceiverPlaneVariation;
in vec4 drtReceiverUnbiasedClip;
#define drtReceiverPlaneValue drtReceiverPlaneMetadata.x
#define drtReceiverPlaneReference intBitsToFloat(drtReceiverPlaneMetadata.y)
#define drtReceiverNormalToken uint(drtReceiverPlaneMetadata.z)
#define drtReceiverBoundsValid drtReceiverPlaneMetadata.w
#define drtReceiverPlaneResidual drtReceiverPlaneVariation.x
#define drtReceiverWarp drtReceiverPlaneVariation.y
int drtValidatedReceiverPlane(out uint token) {
    // BoundsValid is a primitive-wide certificate: ordinary packed owners
    // proved a static finite rectangle; wind owners proved an immutable card
    // plane and transport its rest coordinates separately. VBO input has no
    // such certificate and remains native fallback.
    // Do not re-derive that proof from smooth fragment interpolation. Near 4K
    // pixels have legitimately tiny derivative cross-products, while pool-local
    // ULPs and large plane-unit residuals can randomly reject a valid flat face.
    // The exact flat plane/token/support must agree across the whole primitive.
    token=drtReceiverNormalToken;
    if(drtReceiverGeometryEnabled==0 || drtReceiverPlaneValue==DRT_RECEIVER_INVALID_PLANE ||
       token==0u || token>=4096u || drtReceiverBoundsValid==0 || drtReceiverWarp>0.000001) {
        token=0u;return DRT_RECEIVER_INVALID_PLANE;
    }
    return drtReceiverPlaneValue;
}
uvec2 drtPackedReceiverBounds(int plane) {
    if(plane==DRT_RECEIVER_INVALID_PLANE)return uvec2(0xffffffffu);
    // Four anchored 1/16 coordinates modulo65536. Consumers unwrap each near
    // the page center; <=32-block support makes its signed16 difference unique.
    uvec4 b=uvec4(drtReceiverBoundsUnits)&uvec4(65535u);
    return uvec2(b.x|(b.y<<16u),b.z|(b.w<<16u));
}
#endif
#endif
