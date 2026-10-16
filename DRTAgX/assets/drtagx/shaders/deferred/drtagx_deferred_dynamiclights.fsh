// Projection type affects visibility only; emission remains omnidirectional.
void drtMovingFace(vec3 q,int face,out vec2 side,out float forward) {
    if(face==0){side=vec2(q.z,-q.y);forward=q.x;}
    else if(face==1){side=vec2(-q.z,-q.y);forward=-q.x;}
    else if(face==2){side=vec2(-q.x,q.z);forward=q.y;}
    else if(face==3){side=vec2(-q.x,-q.z);forward=-q.y;}
    else if(face==4){side=vec2(-q.x,-q.y);forward=q.z;}
    else{side=vec2(q.x,-q.y);forward=-q.z;}
}
vec2 drtMovingGather(vec2 cell,vec2 origin,vec2 atlasSize,vec2 size,vec3 shadow,vec2 gradient) {
    // GLSL gather order is (0,1), (1,1), (1,0), (0,0). Read raw depth so
    // every texel can compare its own receiver plane, even at grazing contact.
    // Hardware PCF shares one reference and would require a leaking slope bias.
    vec4 stored=textureGather(drtShadowMaps,(origin+cell+1.0)/atlasSize);
    vec4 dx=fma(vec4(-shadow.x),vec4(size.x),cell.x+vec4(0.5,1.5,1.5,0.5));
    vec4 dy=fma(vec4(-shadow.y),vec4(size.y),cell.y+vec4(1.5,1.5,0.5,0.5));
    vec4 depth=fma(vec4(gradient.x),dx/size.x,fma(vec4(gradient.y),dy/size.y,vec4(shadow.z)));
    // Radius-two separable tent: [1,2,1]/4 at texel centres, with continuous
    // subtexel weights. A hard edge now transitions over three texels, not one.
    vec4 weight=max(vec4(0),2.0-abs(dx))*max(vec4(0),2.0-abs(dy));
    // A projected plane outside [0,1] has no represented receiver. Omit that
    // weight instead of blending a clipped ray into a valid contact shadow.
    weight*=vec4(greaterThanEqual(depth,vec4(0)))*vec4(lessThanEqual(depth,vec4(1)));
    return vec2(dot(weight,step(depth-0.00000024,stored)),dot(weight,vec4(1)));
}
float drtMovingPcf(vec3 shadow,vec2 gradient,int slot,int face,bool held) {
    vec2 size=held?drtMovingHeldSize:vec2(drtMovingAtlasInfo.z);
    float stride=drtMovingAtlasInfo.w;
    vec2 origin=vec2(slot%drtMovingAtlasColumns,slot/drtMovingAtlasColumns)*vec2(2,3)*stride+4.0;
    // Cube slots retain their six-face cells. Hand projectors use the
    // appended rectangular strip, with selected hand slots 0 and 1. Its row
    // stride follows map height; PCF offsets use separate X/Y texel spacing.
    if(held)origin=vec2(float(drtMovingAtlasColumns)*2.0*stride+4.0,float(slot)*(size.y+8.0)+4.0);
    else origin+=vec2(face%2,face/2)*stride;
    vec2 atlasSize=drtMovingAtlasInfo.xy;
    vec2 receiverPixel=shadow.xy*size-0.5;
    vec2 pixel=clamp(receiverPixel,vec2(0),size-1.0);
    // Four gathers cover all sixteen neighbours without a new texture/pass.
    // Clamp the complete footprint inside this face, never into atlas gutters
    // or an adjacent light. The tent naturally renormalizes at face boundaries.
    if(all(greaterThanEqual(size,vec2(4)))) {
        vec2 base=clamp(floor(pixel)-1.0,vec2(0),size-4.0);
        vec2 sum=vec2(0);
        for(int y=0;y<=1;++y)for(int x=0;x<=1;++x)
            sum+=drtMovingGather(base+2.0*vec2(x,y),origin,atlasSize,size,shadow,gradient);
        return sum.y>1e-6?clamp(sum.x/sum.y,0.0,1.0):1.0;
    }
    // Extremely small minimized-window hand maps retain bounded exact bilinear
    // comparisons; a 4x4 gather footprint cannot fit inside those allocations.
    vec2 base=floor(pixel),fraction=fract(pixel);
    float visibility=0.0,weightSum=0.0;
    // Each comparison samples an exact texel center and follows the receiver
    // plane to that center. Reconstruct bilinear filtering with four fetches:
    // sharing one reference across hardware-linear footprints required a bias
    // proportional to 1/light-plane-distance and erased close occluders.
    for(int y=0;y<=1;++y)for(int x=0;x<=1;++x) {
        vec2 cell=min(base+vec2(x,y),size-1.0);
        vec2 w=mix(1.0-fraction,fraction,vec2(x,y));
        float weight=w.x*w.y;
        // Fused subtraction keeps fractional receiver positions accurate too:
        // rounding UV*size before cancellation creates acne on steep planes.
        vec2 delta=fma(-shadow.xy,size,cell+0.5)/size;
        float depth=fma(gradient.x,delta.x,fma(gradient.y,delta.y,shadow.z));
        // A grazing plane can leave the projection's depth domain within one
        // texel. Such rays have no represented receiver, so omit their weight
        // instead of turning them into opaque wedges or diluting valid taps.
        if(depth<0.0||depth>1.0)continue;
        visibility+=weight*step(depth-0.00000024,texelFetch(drtShadowMaps,ivec2(origin+cell),0).r);
        weightSum+=weight;
    }
    return weightSum>1e-6?visibility/weightSum:1.0;
}
float drtMovingCubeFace(vec3 q,vec3 normal,float planeDistance,int slot,int face) {
    vec2 side,nside;float forward,nforward;
    drtMovingFace(q,face,side,forward);drtMovingFace(normal,face,nside,nforward);
    if(forward<=0.1)return 1.0;
    float farPlane=min(drtShadowRange[slot]+0.5,32.0);
    float a=(farPlane+0.1)/(0.1-farPlane),b=0.2*farPlane/(0.1-farPlane);
    vec3 shadow=vec3(0.5+0.47*side/forward,0.5*(1.0-a+b/forward));
    if(any(lessThan(shadow,vec3(0)))||any(greaterThan(shadow,vec3(1))))return 1.0;
    float visibility=drtMovingPcf(shadow,b*nside/(0.94*planeDistance),slot,face,false);
    // Receivers enter the cube's near clip domain continuously. Nearby casters
    // can still occlude distant receivers through the depth-clamped map.
    return mix(1.0,visibility,smoothstep(0.1,0.12,forward));
}
float drtMovingVisibility(vec3 receiver,vec3 emitter,int lightIndex,vec3 unitNormal) {
    int slot=drtShadowSlot[lightIndex];
    if(drtShadowCount<=0||slot<0)return 1.0;
    vec3 q=receiver-emitter;float distance=length(q);
    if(distance<0.00001||distance>=drtShadowRange[slot])return 1.0;
    float planeDistance=dot(unitNormal,q);
    // Diffuse lighting retains its stylized back-facing floor, but that floor
    // must still be occluded by walls. Orient only the shadow receiver plane
    // toward the source: N/dot(N,q) is unchanged, while the normal bias keeps
    // pointing toward the emitter on either side of a surface. The caller's
    // diffuse normal is unchanged because GLSL arguments are copied by value.
    if(planeDistance>0.0){unitNormal=-unitNormal;planeDistance=-planeDistance;}
    // Within one texture cell of the emitter plane, retain the contact fade
    // that prevents unstable grazing comparisons near a moving source.
    float contactWeight=smoothstep(0.0,1.0/32.0,-planeDistance);
    if(contactWeight<=1e-6)return 1.0;
    if(drtShadowGridEnabled!=0) {
        // Anchor to the world, never to the moving emitter. Work relative to
        // the exact double-camera phase to avoid large-world float rounding.
        // Tangent projection preserves the actual receiver plane and its bias.
        vec3 normalWorld=mat3(invModelViewMatrix)*unitNormal;
        vec3 position=mat3(invModelViewMatrix)*receiver+drtSunGridCameraPhase;
        vec3 offset=(floor(position*32.0)+0.5)/32.0-position;
        offset-=normalWorld*dot(normalWorld,offset)/max(dot(normalWorld,normalWorld),1e-6);
        // Inside two cells a half-cell shift can move an on-axis receiver
        // outside a close projector. Retain the exact styled grid farther away.
        float gridWeight=smoothstep(1.0/32.0,2.0/32.0,distance);
        q+=transpose(mat3(invModelViewMatrix))*offset*gridWeight;
    }
    // A fixed normal offset can cross the light's tangent plane at grazing
    // incidence. Scale it by incidence, then use the *biased* plane for PCF:
    // the old unshifted denominator diverged on steep edges and made bands.
    float comparisonDistance=max(length(q),0.00001);
    float incidence=clamp(-dot(unitNormal,q)/comparisonDistance,0.0,1.0);
    // At contact, the usual 10 mm radial + 3 mm normal bias must not cross
    // the light origin. Cap their combined displacement at 10% of distance.
    float biasScale=min(1.0,comparisonDistance/0.13);
    vec3 biased=q*(1.0-0.01*biasScale/comparisonDistance)+unitNormal*(0.003*incidence*biasScale);
    planeDistance=dot(unitNormal,biased);
    int kind=drtShadowKind[slot];
    if(kind>0) {
        int hand=kind-1;if(drtHeldStatus[hand].x<=0.0)return 1.0;
        mat4 matrix=drtHeldMatrices[hand];vec4 clip=matrix*vec4(emitter+biased,1);
        if(clip.w<=0.0)return 1.0;
        vec3 shadow=clip.xyz/clip.w*0.5+0.5;
        if(any(lessThan(shadow,vec3(0)))||any(greaterThan(shadow,vec3(1))))return 1.0;
        float farPlane=min(drtShadowRange[slot]+0.5,32.0),b=2.0*farPlane*0.005/(0.005-farPlane);
        vec2 gradient=b*unitNormal.xy/(vec2(matrix[0][0],matrix[1][1])*planeDistance);
        float visibility=drtMovingPcf(shadow,gradient,slot,0,true);
        // The single held projector intentionally has finite angular coverage.
        // Fade inside its padding: 5% of UV (at least three texels) leaves
        // normally padded receivers untouched, avoiding a hard coverage edge.
        vec2 edge=min(shadow.xy,1.0-shadow.xy)*drtMovingHeldSize;
        vec2 fadeWidth=max(vec2(3.0),drtMovingHeldSize*0.05);
        float edgeWeight=smoothstep(0.0,1.0,min(edge.x/fadeWidth.x,edge.y/fadeWidth.y));
        float nearWeight=smoothstep(0.005,0.012,clip.w);
        return mix(1.0,visibility,contactWeight*edgeWeight*nearWeight);
    }
    int face=drtCubeFace(biased);
    float primary=drtMovingCubeFace(biased,unitNormal,planeDistance,slot,face);
    vec3 axes=abs(biased);float major=max(axes.x,max(axes.y,axes.z));
    float runnerUp=max(min(axes.x,axes.y),max(min(axes.y,axes.z),min(axes.x,axes.z)));
    if(major-runnerUp>=major*0.03)return mix(1.0,primary,contactWeight);
    vec3 secondaryDirection=biased;
    if(face<2)secondaryDirection.x=0.0;else if(face<4)secondaryDirection.y=0.0;else secondaryDirection.z=0.0;
    float secondary=drtMovingCubeFace(biased,unitNormal,planeDistance,slot,drtCubeFace(secondaryDirection));
    float visibility=mix(secondary,primary,0.5+0.5*smoothstep(0.0,major*0.03,major-runnerUp));
    return mix(1.0,visibility,contactWeight);
}
#ifdef DRT_DEFERRED_DEBUG
// Debug interface; the lighting hot path below normalizes its normal once.
float drtCurrentFrameVisibility(vec3 receiver,vec3 emitter,int index,vec3 normal) {
    float n2=dot(normal,normal);
    return drtMovingVisibility(receiver,emitter,index,n2>1e-6?normal*inversesqrt(n2):normalize(emitter-receiver));
}
#endif
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
        if(range>1e-6&&d2<range*range&&d2<1024.0) {
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
