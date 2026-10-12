#ifndef DRT_PLACED_RECEIVER_FALLBACK
#define DRT_PLACED_RECEIVER_FALLBACK
// Uncached receivers borrow the same completed source/depth snapshot as pages.
// This include is read-only: no field allocation, publication or fragment atomics.
#ifndef DRT_PLACED_RECEIVER_SHARED_DECLARATIONS
layout(std430,binding=7) readonly buffer DrtPlacedReceiverSources { vec4 drtMemoSourceData[]; };
uniform int drtMemoEnabled=2;
uniform int drtMemoSourceCount=0;
uniform vec3 drtMemoCameraOffset;
layout(binding=14) uniform sampler2DArrayShadow drtStaticMaps;
uniform vec2 drtPlacedCalibration[32];
#include drtagx_deferred_cube.fsh
#include drtagx_deferred_staticshadows.fsh
#endif

bool drtReadReceiverPlacedLight(vec3 receiverInCameraWorld,vec3 geometricNormalWorld,
    out vec4 radianceCoverage,out vec4 emitterSelf) {
    radianceCoverage=vec4(0.0);emitterSelf=vec4(0.0);
#ifdef DRT_PERFORMANCE_NO_PLS
    return false;
#else
    float n2=dot(geometricNormalWorld,geometricNormalWorld);
    if(drtMemoEnabled!=1 || drtMemoSourceCount<=0 || drtMemoSourceCount>1024 ||
       !(n2>1e-12) || any(isnan(geometricNormalWorld)) || any(isinf(geometricNormalWorld)) ||
       any(isnan(receiverInCameraWorld)) || any(isinf(receiverInCameraWorld)))return false;
    vec3 p=receiverInCameraWorld+drtMemoCameraOffset;
    vec3 normal=geometricNormalWorld*inversesqrt(n2);
    // Establish a complete local set before any PCF. Source7 transition.y
    // permits retained complete depth during refresh; transition.x is stricter
    // and is reserved for baking new pages. Refresh never erases resident faces.
    // Missing depth or >128 contributing truthsources keeps the whole native result.
    int contributors=0;
    for(int i=0;i<drtMemoSourceCount;++i) {
        vec4 source=drtMemoSourceData[i*4],prepared=drtMemoSourceData[i*4+3];
        vec3 q=p-prepared.xyz;
        if(source.w<=0.0 || dot(q,q)>=source.w*source.w)continue;
        if(++contributors>128 || drtMemoSourceData[i*4+2].y<0.5 || drtMemoSourceData[i*4+1].w<0.0)
            return false;
    }
    if(contributors==0)return false;
    DrtPlacedLightSum sum;drtClearPlacedSum(sum);
    float coverage=0.0;
    for(int i=0;i<drtMemoSourceCount;++i) {
        vec4 source=drtMemoSourceData[i*4],color=drtMemoSourceData[i*4+1],prepared=drtMemoSourceData[i*4+3];
        vec3 q=p-prepared.xyz;float d2=dot(q,q);
        if(source.w<=0.0 || d2>=source.w*source.w)continue;
        float distance=sqrt(d2);int level=clamp(int(prepared.w),0,31);
        coverage=max(coverage,1.0-smoothstep(0.82*source.w,source.w,distance));
        if(all(lessThan(abs(q-normal*0.003),vec3(0.5)))) {
            emitterSelf.rgb=max(emitterSelf.rgb,color.rgb*drtPlacedCalibration[level].y*DRT_PLACED_INTENSITY*
                max(1.0,0.2+0.05*float(level)));
            emitterSelf.a=1.0;continue;
        }
        float planeDistance=dot(normal,q);
        float facing=0.28+0.72*max(-planeDistance/max(distance,1e-5),0.0);
        float strength=drtPlacedCalibration[level].x*(1.0-distance/source.w)*
            (0.75*facing/(1.0+0.25*d2)+0.2);
        if(strength<0.0005)continue;
        float visibility=drtStaticVisibility(q,normal,distance,planeDistance,int(color.w),-1,0.0);
        // Fully blocked surfaces receive no source-derived bounce.
        drtAddPlacedLight(sum,color.rgb*strength*visibility);
    }
    radianceCoverage=vec4(drtPlacedSumRadiance(sum),coverage);
    return true;
#endif
}

// Replace native voxel only. Night vision, moving light, authored emission and
// sunlight stay in the owner's existing composition; raw material adds channels
// that a multiplier of already-black vertex lighting cannot introduce.
vec3 drtReceiverPlacedVoxelDelta(vec3 voxel,vec4 placed,vec4 self,float weight) {
    // Retain half the native voxel share throughout covered lit/shadowed areas.
    // Select source/self radiance first so emitter self-light cannot erase
    // the half-voxel baseline that every PLS receiver now retains.
    vec3 replaced=voxel*(1.0-0.5*clamp(placed.a,0.0,1.0)*weight)+max(placed.rgb,self.rgb)*weight;
    return replaced-voxel;
}
#endif
