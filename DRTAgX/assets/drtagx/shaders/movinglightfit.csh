#version 430 core
// Two reductions over current-frame terrain depth. std430 output is deliberately
// identical to the std140 held UBO: two mat4s followed by two vec4 status records.
layout(local_size_x = 64) in;
struct Bounds { vec4 slopes; vec4 info; }; // minXY,maxXY; minDepth,count,invalid,unused
layout(std430, binding = 6) buffer TileBounds { Bounds tiles[]; };
layout(std430, binding = 7) buffer HeldOutput { mat4 heldMatrices[2]; vec4 heldStatus[2]; };
uniform sampler2D terrainDepth;
uniform mat4 inverseProjection;
uniform vec4 handLight[2]; // xyz eye position, authored reach capped to 48
uniform ivec2 dimensions;
uniform int tileWidth;
uniform int tileCount;
uniform int heldSize;
shared vec4 slopeScratch[64];
shared vec4 infoScratch[64];

void mergeBounds(uint lane) {
    barrier();
    for (uint stride=32u; stride>0u; stride>>=1u) {
        if(lane<stride) {
            slopeScratch[lane].xy=min(slopeScratch[lane].xy,slopeScratch[lane+stride].xy);
            slopeScratch[lane].zw=max(slopeScratch[lane].zw,slopeScratch[lane+stride].zw);
            infoScratch[lane].x=min(infoScratch[lane].x,infoScratch[lane+stride].x);
            infoScratch[lane].yz+=infoScratch[lane+stride].yz;
        }
        barrier();
    }
}

void main() {
    uint lane=gl_LocalInvocationIndex;
 #ifdef DRT_FINAL_FIT
    int hand=int(gl_WorkGroupID.x);
    vec4 slopes=vec4(1e30,1e30,-1e30,-1e30),info=vec4(1e30,0,0,0);
    for(int t=int(lane);t<tileCount;t+=64) {
        Bounds b=tiles[t*2+hand];
        slopes.xy=min(slopes.xy,b.slopes.xy); slopes.zw=max(slopes.zw,b.slopes.zw);
        info.x=min(info.x,b.info.x); info.yz+=b.info.yz;
    }
    slopeScratch[lane]=slopes;infoScratch[lane]=info;mergeBounds(lane);
    if(lane!=0u)return;
    slopes=slopeScratch[0];info=infoScratch[0];
    if(info.z>0.0)return; // CPU-uploaded conservative fallback remains current-frame.
    if(info.y==0.0 || handLight[hand].w<=0.0) {
        heldStatus[hand]=vec4(0); heldMatrices[hand]=mat4(0); return;
    }
    // Ten-percent width margin, plus receiver offset and two filter texels.
    vec2 width=max(slopes.zw-slopes.xy,vec2(1e-4));
    vec2 pad=width*(0.1+4.0/float(heldSize)) + vec2(0.025/max(info.x,0.005));
    vec2 lo=slopes.xy-pad,hi=slopes.zw+pad;
    vec2 stepSize=(hi-lo)/float(max(heldSize-4,1));
    lo=floor(lo/stepSize)*stepSize;hi=ceil(hi/stepSize)*stepSize;
    const float nearPlane=0.005;
    float farPlane=handLight[hand].w+0.5;
    mat4 p=mat4(0);
    p[0][0]=2.0/(hi.x-lo.x);p[1][1]=2.0/(hi.y-lo.y);
    p[2][0]=(hi.x+lo.x)/(hi.x-lo.x);p[2][1]=(hi.y+lo.y)/(hi.y-lo.y);
    p[2][2]=(farPlane+nearPlane)/(nearPlane-farPlane);p[2][3]=-1.0;
    p[3][2]=2.0*farPlane*nearPlane/(nearPlane-farPlane);
    mat4 view=mat4(1);view[3]=vec4(-handLight[hand].xyz,1);
    heldMatrices[hand]=p*view;heldStatus[hand]=vec4(1,info.y,info.x,0);
 #else
    int t=int(gl_WorkGroupID.x+gl_WorkGroupID.y*65535u);
    if(t>=tileCount)return;
    ivec2 start=ivec2(t%tileWidth,t/tileWidth)*64;
    // Reconstruct each pixel once, then contribute to both independent hands.
    vec4 slopes[2];vec4 infos[2];
    for(int h=0;h<2;++h){slopes[h]=vec4(1e30,1e30,-1e30,-1e30);infos[h]=vec4(1e30,0,0,0);}
    for(int k=int(lane);k<4096;k+=64) {
        ivec2 pixel=start+ivec2(k%64,k/64);
        if(any(greaterThanEqual(pixel,dimensions)))continue;
        float d=texelFetch(terrainDepth,pixel,0).r;
        if(d==1.0)continue; // clear depth is sky
        vec2 ndc=(vec2(pixel)+0.5)/vec2(dimensions)*2.0-1.0;
        vec4 v=inverseProjection*vec4(ndc,d*2.0-1.0,1);
        bool bad=isnan(d)||isinf(d)||d<0.0||d>1.0||abs(v.w)<1e-20;
        vec3 receiver=v.xyz/v.w;
        bad=bad||any(isnan(receiver))||any(isinf(receiver));
        for(int h=0;h<2;++h) {
            if(handLight[h].w<=0.0)continue;
            if(bad){infos[h].z+=1.0;continue;}
            vec3 q=receiver-handLight[h].xyz;
            float reach=handLight[h].w+0.25;
            if(dot(q,q)>reach*reach)continue;
            float forward=-q.z;
            if(forward<=0.005){infos[h].z+=1.0;continue;}
            vec2 slope=q.xy/forward;
            slopes[h].xy=min(slopes[h].xy,slope);slopes[h].zw=max(slopes[h].zw,slope);
            infos[h].x=min(infos[h].x,forward);infos[h].y+=1.0;
        }
    }
    for(int h=0;h<2;++h) {
        slopeScratch[lane]=slopes[h];infoScratch[lane]=infos[h];mergeBounds(lane);
        if(lane==0u){tiles[t*2+h].slopes=slopeScratch[0];tiles[t*2+h].info=infoScratch[0];}
        barrier();
    }
 #endif
}

