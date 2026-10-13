#ifndef DRT_FOLIAGE_RECEIVER
#define DRT_FOLIAGE_RECEIVER
// The private development timing control is constant-folded out of Release.
#ifdef DRT_DEFERRED_DEBUG
uniform int drtFoliageReceiverEnabled = 1;
#else
const int drtFoliageReceiverEnabled = 1;
#endif
// Reuse the existing R32I/RG32UI receiver attachments. Ordinary planes cannot
// enter this reserved negative range: pool translations are bounded to 65535
// blocks and each native normal component is at most seven. Wind's signed
// 28-bit intercept covers the cache's 4096-block anchor lifetime plus its reach.
const int DRT_FOLIAGE_PLANE_BASE = (-2147483647);
const int DRT_FOLIAGE_PLANE_BIAS = 134217728;
const uint DRT_FOLIAGE_PAGE_TOKEN = 4096u;
bool drtIsFoliagePlane(int plane) {
    return plane >= DRT_FOLIAGE_PLANE_BASE && plane < DRT_FOLIAGE_PLANE_BASE + 268435456;
}
int drtPackFoliagePlane(int plane) { return DRT_FOLIAGE_PLANE_BASE + (plane + DRT_FOLIAGE_PLANE_BIAS); }
int drtUnpackFoliagePlane(int plane) { return (plane - DRT_FOLIAGE_PLANE_BASE) - DRT_FOLIAGE_PLANE_BIAS; }
vec3 drtFoliageNormal(uint token) {
    return vec3(float((token>>1u)&7u),float((token>>5u)&7u),float((token>>9u)&7u)) *
        vec3((token&1u)!=0u?-1.0:1.0,(token&16u)!=0u?-1.0:1.0,(token&256u)!=0u?-1.0:1.0);
}
ivec3 drtFoliageAxes(vec3 n) {
    vec3 a=abs(n);int m=a.x>=a.y&&a.x>=a.z?0:a.y>=a.z?1:2;
    return m==0?ivec3(1,2,0):m==1?ivec3(0,2,1):ivec3(0,1,2);
}
uvec2 drtPackFoliagePosition(vec3 anchoredRest,uint token) {
    ivec3 a=drtFoliageAxes(drtFoliageNormal(token));
    // Signed 24-bit tangent coordinates at 1/256 block: finer than the baked
    // 1/16 field and independent of camera/wind. The remaining bits carry N.
    uvec2 p=uvec2(ivec2(round(vec2(anchoredRest[a.x],anchoredRest[a.y])*256.0)))&uvec2(0xffffffu);
    return p|uvec2((token&255u)<<24u,((token>>8u)&15u)<<24u);
}
vec3 drtUnpackFoliagePosition(int encodedPlane,uvec2 payload,out uint token,out int plane) {
    token=((payload.x>>24u)|((payload.y>>24u)<<8u))&4095u;
    plane=drtUnpackFoliagePlane(encodedPlane);
    vec3 n=drtFoliageNormal(token);ivec3 a=drtFoliageAxes(n);
    ivec2 signedTangent=(ivec2(payload<<8u)>>8);
    vec3 p=vec3(0.0);p[a.x]=float(signedTangent.x)/256.0;p[a.y]=float(signedTangent.y)/256.0;
    p[a.z]=(float(plane)/1024.0-n[a.x]*p[a.x]-n[a.y]*p[a.y])/max(abs(n[a.z]),1.0)*sign(n[a.z]);
    return p;
}
#endif
