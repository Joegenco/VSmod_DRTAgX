#ifndef DRT_PLACED_LIGHT_PAGES
#define DRT_PLACED_LIGHT_PAGES
#include drtagx_placed_surface_normal.ash
#ifndef DRT_MEMO_GRID_ENABLED
#define DRT_MEMO_GRID_ENABLED 0
#endif
// One page owns a native plane and a one-block tangent brick, with a complete
// 17x17 halo at 1/16-block spacing in each immutable bank. This retains chisel
// detail without increasing the 289-node record or per-receiver lookup count.
// Only compute writes; receivers never PCF. The 1/32 presentation grid below
// remains independent of this field's sampling lattice.
const int DRT_PAGE_NODES=289;
const float DRT_PAGE_BLOCKS=1.0, DRT_PAGE_NODES_PER_BLOCK=16.0;
const int DRT_PAGE_ANCHOR_SHIFT=4, DRT_PAGE_SUPPORT_UNITS=16;
const uint DRT_PAGE_BUSY=0x80000000u, DRT_MEMO_GENERIC_NORMAL=4096u;
const int DRT_MEMO_LEGACY=0,DRT_MEMO_HIT=1,DRT_MEMO_FILLED=2,DRT_MEMO_PENDING=3;
// selfLightEmitter.A carries emitter classification only; voxel composition
// retains a uniform half share independently of cached shadow visibility.
struct DrtPageNode {vec4 radianceCoverage; vec4 selfLightEmitter;};
struct DrtPlacedPage {
    uvec4 key; // absolute tangent brick X/Y, authored intercept, native normal
    uvec4 support; // exact low/high tangent bounds, anchored1/16 modulo65536
    uvec4 state; // world ownership/busy, last request, published bank+1, stamp
    uvec4 bankMeta[2]; // stamp, publication frame, dense revision(bank0), blocked evaluation revision(bank1)
    DrtPageNode nodes[578];
};
#ifdef DRT_PAGE_WORKER
layout(std430,binding=6) coherent buffer DrtPlacedMemoBuffer {
#else
layout(std430,binding=6) readonly buffer DrtPlacedMemoBuffer {
#endif
    uvec4 drtMemoControl; DrtPlacedPage drtMemoPages[];
};
layout(std430,binding=7) readonly buffer DrtPlacedMemoSources {vec4 drtMemoSourceData[];};
uniform int drtMemoEnabled=2;
uniform uint drtMemoGeneration=1u,drtMemoFrame=1u;
uniform int drtMemoCapacityMask=4095;
uniform uint drtMemoFillLimit=4u;
uniform int drtMemoSourceCount=0,drtMemoRegionOffset=4096;
uniform int drtMemoDiagnostics=0,drtMemoRegionsEnabled=1,drtMemoTransitionFrames=12;
uniform int drtMemoBoundsReady=0;
uniform vec3 drtMemoBoundsMin,drtMemoBoundsMax;
uniform ivec3 drtMemoAnchorCells;
uniform vec3 drtMemoCameraOffset;
uniform isampler2D drtReceiverPlane;
uniform usampler2D drtReceiverBounds;
uniform int drtReceiverPlaneReady=0;
bool drtAuthoredPlaneValid=false;
int drtAuthoredPlane=0;
uvec2 drtAuthoredBounds=uvec2(0xffffffffu);
// Visibility remains a neutral compatibility field; nodes need no shadow mask.
struct DrtPlacedMemoValue {vec4 radianceCoverage;vec4 selfLightEmitter;float visibility;};
void drtClearPlacedMemoValue(out DrtPlacedMemoValue v) {v.radianceCoverage=vec4(0);v.selfLightEmitter=vec4(0);v.visibility=1.0;}
uint drtMemoMix(uint v) {v^=v>>16u;v*=0x7feb352du;v^=v>>15u;v*=0x846ca68bu;return v^(v>>16u);}
uint drtMemoHash(uvec4 k) {return drtMemoMix(k.x^drtMemoMix(k.y+0x9e3779b9u)^drtMemoMix(k.z+0x85ebca6bu)^drtMemoMix(k.w+0xc2b2ae35u));}
ivec3 drtMemoAxes(vec3 n) {
    vec3 a=abs(n);int m=a.x>=a.y&&a.x>=a.z?0:a.y>=a.z?1:2;
    return m==0?ivec3(1,2,0):m==1?ivec3(0,2,1):ivec3(0,1,2);
}
bool drtMemoNativeNormal(uint token,out vec3 n) {
    n=token<4096u?drtPlacedSurfaceNormalVector(token):vec3(0);float l=dot(n,n);
    if(l<=0.0)return false;n*=inversesqrt(l);return true;
}
uint drtMemoAge(uint frame) {return (drtMemoFrame-frame)&0x3fffffffu;}
uint drtMemoDesiredStampAt(vec3 p) {
    if(drtMemoRegionsEnabled==0)return 1u;
    ivec3 r=ivec3(floor(p/8.0))+(drtMemoAnchorCells>>7);
    uint b=drtMemoHash(uvec4(uvec3(r),0x51ed270bu))&65535u;
    return max(floatBitsToUint(drtMemoSourceData[drtMemoRegionOffset+int(b>>2u)][int(b&3u)]),1u);
}
bool drtMemoInfluenced(vec3 p) {
    if(drtMemoBoundsReady==0||any(lessThan(p,drtMemoBoundsMin))||any(greaterThan(p,drtMemoBoundsMax)))return false;
    ivec3 r=ivec3(floor(p/8.0))+(drtMemoAnchorCells>>7);
    uint b=drtMemoHash(uvec4(uvec3(r),0x51ed270bu))&65535u;
    return floatBitsToUint(drtMemoSourceData[drtMemoRegionOffset+16384+int(b>>2u)][int(b&3u)])!=0u;
}
uint drtPageHash(uvec4 k,uvec2 support) {return drtMemoHash(k)^drtMemoMix(support.x+0x319f87d1u)^drtMemoMix(support.y+0x63ef1371u);}
vec4 drtPageBounds(uvec4 k,uvec2 support) {
    ivec3 a=drtMemoAxes(drtPlacedSurfaceNormalVector(k.w));
    ivec2 anchor=ivec2(drtMemoAnchorCells[a.x],drtMemoAnchorCells[a.y])>>DRT_PAGE_ANCHOR_SHIFT;
    ivec2 center=(ivec2(k.xy)-anchor)*DRT_PAGE_SUPPORT_UNITS+DRT_PAGE_SUPPORT_UNITS/2;
    ivec4 code=ivec4(int(support.x&65535u),int(support.x>>16u),int(support.y&65535u),int(support.y>>16u));
    // Bounds lie <=32blocks from this brick. Unwrap modulo16bit around its
    // center, preserving negative origins and fine chisel edges exactly.
    ivec4 reference=ivec4(center,center);
    ivec4 delta=((code-(reference&65535)+32768)&65535)-32768;
    return vec4(reference+delta)/16.0;
}
vec3 drtPageReceiver(uvec4 k,ivec2 node,uvec2 support) {
    vec3 n=drtPlacedSurfaceNormalVector(k.w);ivec3 a=drtMemoAxes(n);vec3 p=vec3(0);
    ivec2 anchor=ivec2(drtMemoAnchorCells[a.x],drtMemoAnchorCells[a.y])>>DRT_PAGE_ANCHOR_SHIFT;
    vec2 base=vec2(ivec2(k.xy)-anchor)*DRT_PAGE_BLOCKS;
    vec4 bounds=drtPageBounds(k,support);
    vec2 tangent=clamp(base+vec2(node)/DRT_PAGE_NODES_PER_BLOCK,bounds.xy,bounds.zw);
    p[a.x]=tangent.x;p[a.y]=tangent.y;
    p[a.z]=(float(int(k.z))/1024.0-n[a.x]*p[a.x]-n[a.y]*p[a.y])/n[a.z];return p;
}
uint drtPageStamp(uvec4 k,uvec2 support) {
    vec3 p0=drtPageReceiver(k,ivec2(0),support),p1=drtPageReceiver(k,ivec2(16,0),support);
    vec3 p2=drtPageReceiver(k,ivec2(0,16),support),p3=drtPageReceiver(k,ivec2(16),support);
    ivec3 lo=ivec3(floor(min(min(p0,p1),min(p2,p3))/8.0));
    ivec3 hi=ivec3(floor(max(max(p0,p1),max(p2,p3))/8.0));
    uint stamp=1u;
    // Dominant-axis slopes are <=1: this 1-block brick spans <=2 depth blocks
    // and at most eight regional cells. Interior regions matter on slopes.
    for(int z=lo.z;z<=hi.z;++z)for(int y=lo.y;y<=hi.y;++y)for(int x=lo.x;x<=hi.x;++x)
        stamp=max(stamp,drtMemoDesiredStampAt(vec3(x,y,z)*8.0));
    return stamp;
}
uvec4 drtPageKey(vec3 p,uint token,int plane) {
    ivec3 a=drtMemoAxes(drtPlacedSurfaceNormalVector(token));
    ivec2 anchor=ivec2(drtMemoAnchorCells[a.x],drtMemoAnchorCells[a.y])>>DRT_PAGE_ANCHOR_SHIFT;
    return uvec4(uvec2(ivec2(floor(vec2(p[a.x],p[a.y])/DRT_PAGE_BLOCKS))+anchor),uint(plane),token);
}
int drtPageSecondaryBucket(uint hash) {
    // The odd offset selects a distinct bucket in every power-of-two table
    // with at least two buckets. A one-bucket diagnostic remains four-way.
    return int((hash+(drtMemoMix(hash^0x6d2b79f5u)|1u))&uint(drtMemoCapacityMask));
}
int drtFindPage(uvec4 k,uvec2 support) {
    uint hash=drtPageHash(k,support);
    for(int bucket=0;bucket<2;++bucket) {
        // Ordinary primary hits return before any secondary entry is read.
        int first=(bucket==0?int(hash&uint(drtMemoCapacityMask)):drtPageSecondaryBucket(hash))*4;
        for(int way=0;way<4;++way) {
            int slot=first+way;uvec4 s=drtMemoPages[slot].state;
            if((s.x&0x3fffffffu)==drtMemoGeneration&&all(equal(drtMemoPages[slot].key,k))&&
                all(equal(drtMemoPages[slot].support.xy,support)))return slot;
        }
        if(drtMemoCapacityMask==0)break;
    }return -1;
}
int drtQueryPlacedMemo(vec3 relative,vec3 normal,uint token,int mode,bool ignored,out DrtPlacedMemoValue v) {
    drtClearPlacedMemoValue(v);
    if(drtMemoEnabled==0)return DRT_MEMO_LEGACY;
    if(drtMemoEnabled!=1||!drtAuthoredPlaneValid||mode!=0||token==0u||token>=4096u)return DRT_MEMO_PENDING;
    vec3 p=relative+drtMemoCameraOffset;
    // Removed sources cannot leave stale illumination outside current true
    // influence. Missing depth remains present in this source-truth envelope.
    if(!drtMemoInfluenced(p))return DRT_MEMO_PENDING;
    uvec4 k=drtPageKey(p,token,drtAuthoredPlane);int slot=drtFindPage(k,drtAuthoredBounds);
    // Published bank3 means the complete potential source set exceeded the
    // bounded 128-source worker. Preserve native lighting, never read bank2.
    if(slot<0||drtMemoPages[slot].state.z==0u||drtMemoPages[slot].state.z==3u)return DRT_MEMO_PENDING;
    int bank=int(drtMemoPages[slot].state.z)-1;ivec3 a=drtMemoAxes(drtPlacedSurfaceNormalVector(token));
    vec2 tangent=vec2(p[a.x],p[a.y]);
    vec4 bounds=drtPageBounds(k,drtAuthoredBounds);
    if(DRT_MEMO_GRID_ENABLED!=0)tangent=(floor(tangent*32.0)+0.5)/32.0;
    tangent=clamp(tangent,bounds.xy,bounds.zw);
    ivec2 anchor=ivec2(drtMemoAnchorCells[a.x],drtMemoAnchorCells[a.y])>>DRT_PAGE_ANCHOR_SHIFT;
    vec2 grid=clamp((tangent-vec2(ivec2(k.xy)-anchor)*DRT_PAGE_BLOCKS)*DRT_PAGE_NODES_PER_BLOCK,vec2(0),vec2(16));
    ivec2 lo=min(ivec2(floor(grid)),ivec2(15));
    vec2 base=vec2(ivec2(k.xy)-anchor)*DRT_PAGE_BLOCKS;
    vec2 low=clamp(base+vec2(lo)/DRT_PAGE_NODES_PER_BLOCK,bounds.xy,bounds.zw),high=clamp(base+vec2(lo+1)/DRT_PAGE_NODES_PER_BLOCK,bounds.xy,bounds.zw);
    // Recompute weights after boundary clamping; a half-cell chisel edge must
    // not bias interpolation toward an off-surface lattice sample.
    vec2 phase=clamp((tangent-low)/max(high-low,vec2(1e-8)),vec2(0),vec2(1));
    bool upper=phase.x+phase.y>1.0;
    vec3 weights=upper?vec3(phase.x+phase.y-1.0,1.0-phase.x,1.0-phase.y):vec3(1.0-phase.x-phase.y,phase.x,phase.y);
    uint age=drtMemoAge(drtMemoPages[slot].bankMeta[bank].y);
    // Zero transition frames exposes a whole completed bank immediately for
    // the manual throughput experiment. It never exposes partially written nodes.
    float t=drtMemoTransitionFrames<=0?1.0:clamp(float(age)/float(drtMemoTransitionFrames),0.0,1.0);t=t*t*(3.0-2.0*t);
    for(int i=0;i<3;++i) {
        ivec2 o=i==0?(upper?ivec2(1):ivec2(0)):i==1?(upper?ivec2(0,1):ivec2(1,0)):(upper?ivec2(1,0):ivec2(0,1));
        int node=(lo.y+o.y)*17+lo.x+o.x;DrtPageNode n=drtMemoPages[slot].nodes[bank*289+node];
        float emitterFlag=max(n.selfLightEmitter.a,0.0);
        if(drtMemoPages[slot].bankMeta[1-bank].x!=0u&&t<1.0) {
            DrtPageNode old=drtMemoPages[slot].nodes[(1-bank)*289+node];
            n.radianceCoverage=mix(old.radianceCoverage,n.radianceCoverage,t);
            n.selfLightEmitter.rgb=mix(old.selfLightEmitter.rgb,n.selfLightEmitter.rgb,t);
            emitterFlag=mix(max(old.selfLightEmitter.a,0.0),emitterFlag,t);
        } else if(drtMemoPages[slot].bankMeta[1-bank].x==0u) {
            // Whole-page readiness fades coherently from native lighting.
            n.radianceCoverage*=t;n.selfLightEmitter.rgb*=t;emitterFlag*=t;
        }
        n.selfLightEmitter.a=emitterFlag;
        v.radianceCoverage+=n.radianceCoverage*weights[i];v.selfLightEmitter+=n.selfLightEmitter*weights[i];
    }return DRT_MEMO_HIT;
}
int drtQueryPlacedMemo(vec3 p,vec3 n,int mode,bool fill,out DrtPlacedMemoValue v) {return drtQueryPlacedMemo(p,n,DRT_MEMO_GENERIC_NORMAL,mode,fill,v);}
int drtQueryPlacedMemoNode(vec3 p,vec3 n,uint token,int mode,bool fill,out DrtPlacedMemoValue v) {return drtQueryPlacedMemo(p,n,token,mode,fill,v);}
int drtQueryPlacedMemoNode(vec3 p,vec3 n,int mode,bool fill,out DrtPlacedMemoValue v) {return drtQueryPlacedMemo(p,n,mode,fill,v);}
// Removed unbounded screen-edge extrapolation; preparation owns requests.
int drtPrefetchPlacedMemoSurface(vec3 p,uint token,vec2 uv,mat4 ip,mat3 im) {return DRT_MEMO_PENDING;}
#endif
