// Projection type affects visibility only; emission remains omnidirectional.
void drtMovingFace(vec3 q,int face,out vec2 side,out float forward) {
    if(face==0){side=vec2(q.z,-q.y);forward=q.x;}
    else if(face==1){side=vec2(-q.z,-q.y);forward=-q.x;}
    else if(face==2){side=vec2(-q.x,q.z);forward=q.y;}
    else if(face==3){side=vec2(-q.x,-q.z);forward=-q.y;}
    else if(face==4){side=vec2(-q.x,-q.y);forward=q.z;}
    else{side=vec2(q.x,-q.y);forward=-q.z;}
}
float drtMovingPcf(vec3 shadow,vec2 gradient,int slot,int face,bool held) {
    float size=drtMovingAtlasInfo.z*(held?6.0:1.0),stride=drtMovingAtlasInfo.w;
    vec2 origin=vec2(slot%drtMovingAtlasColumns,slot/drtMovingAtlasColumns)*vec2(2,3)*stride+4.0;
    // Cube slots retain their six-face cells. Hand projectors use the
    // appended high-resolution strip, with selected hand slots 0 and 1.
    if(held)origin=vec2(float(drtMovingAtlasColumns)*2.0*stride+4.0,float(slot)*(size+8.0)+4.0);
    else origin+=vec2(face%2,face/2)*stride;
    vec2 texel=vec2(1.0/size),atlasSize=drtMovingAtlasInfo.xy;
    // Follow the receiving plane per tap, including the hardware bilinear footprint.
    // D24 precision margin is four depth-code steps. A large projected bias
    // would erase close blockers with the held projector's 5 mm near plane.
    float reference=shadow.z-dot(abs(gradient),texel)-0.00000024,visibility=0.0;
    if(drtPerformanceMode != 0) {
        // Four symmetric comparison footprints retain receiving-plane depth and the D24 margin.
        for(int y=-1;y<=1;y+=2)for(int x=-1;x<=1;x+=2) {
            vec2 uv=clamp(shadow.xy+vec2(x,y)*texel*0.8,texel,1.0-texel);
            float depth=reference+dot(gradient,uv-shadow.xy);
            visibility+=texture(drtShadowMaps,vec3((origin+uv*size)/atlasSize,depth));
        }
        return visibility*0.25;
    }
    for(int y=-1;y<=1;++y)for(int x=-1;x<=1;++x) {
        vec2 uv=clamp(shadow.xy+vec2(x,y)*texel*1.6,texel,1.0-texel);
        float depth=reference+dot(gradient,uv-shadow.xy);
        visibility+=texture(drtShadowMaps,vec3((origin+uv*size)/atlasSize,depth));
    }
    return visibility/9.0;
}
float drtMovingCubeFace(vec3 q,vec3 normal,float planeDistance,int slot,int face) {
    vec2 side,nside;float forward,nforward;
    drtMovingFace(q,face,side,forward);drtMovingFace(normal,face,nside,nforward);
    if(forward<=0.1)return 1.0;
    float farPlane=drtShadowRange[slot]+0.5;
    float a=(farPlane+0.1)/(0.1-farPlane),b=0.2*farPlane/(0.1-farPlane);
    vec3 shadow=vec3(0.5+0.47*side/forward,0.5*(1.0-a+b/forward));
    if(any(lessThan(shadow,vec3(0)))||any(greaterThan(shadow,vec3(1))))return 1.0;
    return drtMovingPcf(shadow,b*nside/(0.94*planeDistance),slot,face,false);
}
float drtMovingVisibility(vec3 receiver,vec3 emitter,int lightIndex,vec3 unitNormal) {
    int slot=drtShadowSlot[lightIndex];
    if(drtShadowCount<=0||slot<0)return 1.0;
    vec3 q=receiver-emitter;float distance=length(q);
    if(distance<0.12||distance>=drtShadowRange[slot])return 1.0;
    float planeDistance=dot(unitNormal,q);
    // Preserve back-facing radiance's established normal-facing floor.
    if(planeDistance>=-0.00001)return 1.0;
    if(drtShadowGridEnabled!=0) {
        // Anchor to the world, never to the moving emitter. Work relative to
        // the exact double-camera phase to avoid large-world float rounding.
        // Tangent projection preserves the actual receiver plane and its bias.
        vec3 normalWorld=mat3(invModelViewMatrix)*unitNormal;
        vec3 position=mat3(invModelViewMatrix)*receiver+drtSunGridCameraPhase;
        vec3 offset=(floor(position*32.0)+0.5)/32.0-position;
        offset-=normalWorld*dot(normalWorld,offset)/max(dot(normalWorld,normalWorld),1e-6);
        q+=transpose(mat3(invModelViewMatrix))*offset;
    }
    // Bias from the snapped comparison point; continuous lighting/range above stays unchanged.
    vec3 biased=q*(1.0-0.01/max(length(q),0.00001))+unitNormal*0.003;
    int kind=drtShadowKind[slot];
    if(kind>0) {
        int hand=kind-1;if(drtHeldStatus[hand].x<=0.0)return 1.0;
        mat4 matrix=drtHeldMatrices[hand];vec4 clip=matrix*vec4(emitter+biased,1);
        if(clip.w<=0.0)return 1.0;
        vec3 shadow=clip.xyz/clip.w*0.5+0.5;
        if(any(lessThan(shadow,vec3(0)))||any(greaterThan(shadow,vec3(1))))return 1.0;
        float farPlane=drtShadowRange[slot]+0.5,b=2.0*farPlane*0.005/(0.005-farPlane);
        vec2 gradient=b*unitNormal.xy/(vec2(matrix[0][0],matrix[1][1])*planeDistance);
        return drtMovingPcf(shadow,gradient,slot,0,true);
    }
    int face=drtCubeFace(biased);
    float primary=drtMovingCubeFace(biased,unitNormal,planeDistance,slot,face);
    vec3 axes=abs(biased);float major=max(axes.x,max(axes.y,axes.z));
    float runnerUp=max(min(axes.x,axes.y),max(min(axes.y,axes.z),min(axes.x,axes.z)));
    if(major-runnerUp>=major*0.03)return primary;
    vec3 secondaryDirection=biased;
    if(face<2)secondaryDirection.x=0.0;else if(face<4)secondaryDirection.y=0.0;else secondaryDirection.z=0.0;
    float secondary=drtMovingCubeFace(biased,unitNormal,planeDistance,slot,drtCubeFace(secondaryDirection));
    return mix(secondary,primary,0.5+0.5*smoothstep(0.0,major*0.03,major-runnerUp));
}
// Debug interface; the lighting hot path below normalizes its normal once.
float drtCurrentFrameVisibility(vec3 receiver,vec3 emitter,int index,vec3 normal) {
    float n2=dot(normal,normal);
    return drtMovingVisibility(receiver,emitter,index,n2>1e-6?normal*inversesqrt(n2):normalize(emitter-receiver));
}
#define DRT_DYNAMIC_CAPACITY 16
#include drtagx_dynamic_accumulation.ash
// The default two-source case has only two ranks. Evaluate exactly the same
// smooth tie pooling and 1,1/2 harmonic weights without sorting eight scratch ranks.
vec3 drtAccumulateMovingPair(vec3 a,vec3 b) {
    float sa=dot(a,DRT_LUMINANCE),sb=dot(b,DRT_LUMINANCE);
    if(!(sa>0.0)||isnan(sa)||isinf(sa))return sb>0.0&&!isnan(sb)&&!isinf(sb)?b:vec3(0);
    if(!(sb>0.0)||isnan(sb)||isinf(sb))return a;
    vec3 winner=sa>=sb?a:b,loser=sa>=sb?b:a;
    float kernel=1.0-smoothstep(0.0,DRT_DYNAMIC_TIE_WIDTH,drtRelativeStrength(sa,sb));
    return ((1.0+0.5*kernel)*winner+(0.5+kernel)*loser)/(1.0+kernel);
}
vec3 drtCurrentFrameLights(vec3 receiver,vec3 normalView) {
    vec3 contributions[16];int count=clamp(drtPointCount,0,16);
    float n2=dot(normalView,normalView);vec3 normal=n2>1e-6?normalView*inversesqrt(n2):vec3(0);
    for(int i=0;i<count;++i) {
        vec3 delta=drtPointPos[i]-receiver;float d2=dot(delta,delta),range=drtPointRadiance[i].w;
        vec3 c=vec3(0);
        if(range>1e-6&&d2<range*range&&d2<=2304.0) {
            float d=sqrt(d2),w=max(0.0,1.0-d/range);
            float facing=n2>1e-6&&d2>1e-10?0.28+0.72*max(dot(normal,delta/d),0.0):1.0;
            vec3 shadowNormal=n2>1e-6?normal:(d2>1e-10?delta/d:vec3(0,1,0));
            float visibility=drtMovingVisibility(receiver,drtPointPos[i],i,shadowNormal);
            // One attenuation evaluation, with the original min(facing,visibility).
            c=DRT_DYNAMIC_GAIN*drtPointRadiance[i].xyz*(w*sqrt(w)/(1.0+0.25*d))*min(facing,clamp(visibility,0.0,1.0));
        }
        contributions[i]=c;
    }
    if(count==2)return drtAccumulateMovingPair(contributions[0],contributions[1]);
    return drtAccumulateDynamic(contributions,count);
}
