#version 430 core
#define DRT_PAGE_WORKER
#include drtagx_placed_light_pages.ash
// Runtime injects matching CPU storage sizes. Defaults retain the ordinary ABI
// for standalone consumers; the manual experiment supplies a much larger burst.
#ifndef DRT_PAGE_JOB_CAPACITY
#define DRT_PAGE_JOB_CAPACITY 4
#endif
#ifndef DRT_PAGE_QUEUE_CAPACITY
#define DRT_PAGE_QUEUE_CAPACITY 64
#endif
layout(std430,binding=5) coherent buffer DrtPageQueue {
    uvec4 queueControl; // monotonic head/tail, overflows, completed pages
    uvec4 queueLimits; // immutable job count and reserved diagnostic words
    uvec4 jobSlots[DRT_PAGE_JOB_CAPACITY]; // copy before advancing ring head
    uvec4 requests[DRT_PAGE_QUEUE_CAPACITY]; // page slot, epoch, hash, reserved
};
uniform uint drtPageBudget=4u;
uniform uint drtMemoDenseSourceRevision=1u;
uniform uint drtMemoEvaluationRevision=1u;
uniform uint drtPageVisibleFrames=64u;
#ifdef DRT_PAGE_COLLECT
bool drtQueueVisiblePage(uvec4 request) {
    // Collection and scheduling are separate ordered dispatches: head cannot
    // move during this append phase. One reservation admits every request that
    // fits, independent of SIMD width or competing waves. The scheduler clamps
    // the tail after overflow; rejected reservations never touch ring storage.
    uint head=atomicAdd(queueControl.x,0u),tail=atomicAdd(queueControl.y,1u);
    if(tail-head>=uint(DRT_PAGE_QUEUE_CAPACITY)) {
        atomicAdd(queueControl.z,1u);return false;
    }
    requests[tail&uint(DRT_PAGE_QUEUE_CAPACITY-1)]=request;return true;
}
#endif
#ifdef DRT_PAGE_COLLECT
layout(local_size_x=8,local_size_y=8) in;
uniform sampler2D drtPageNormal;
uniform ivec2 drtPageExtent;
uniform int drtPagePixelStep=8;
uniform mat4 drtPageInverseProjection,drtPageInverseView;
bool drtPageInfluenced(vec3 p) {
    if(drtMemoBoundsReady==0||any(lessThan(p,drtMemoBoundsMin))||any(greaterThan(p,drtMemoBoundsMax)))return false;
    ivec3 r=ivec3(floor(p/8.0))+(drtMemoAnchorCells>>7);
    uint b=drtMemoHash(uvec4(uvec3(r),0x51ed270bu))&65535u;
    // True sphere/AABB cells are uploaded event-only. Hash collisions can
    // admit harmless extra work, but cannot reject an influenced receiver.
    return floatBitsToUint(drtMemoSourceData[drtMemoRegionOffset+16384+int(b>>2u)][int(b&3u)])!=0u;
}
void main() {
    // Unchanged frames rotate their sparse pixel phase. A bounded edit burst
    // supplies step1 so even a one-pixel receiver is requested immediately;
    // eligibility below still rejects surfaces outside current source influence.
    uint step=uint(max(drtPagePixelStep,1));
    ivec2 pixel=ivec2(gl_GlobalInvocationID.xy)*int(step)+
        ivec2(int(drtMemoFrame%step),int((drtMemoFrame/step)%step));
    if(any(greaterThanEqual(pixel,drtPageExtent)))return;
    int plane=texelFetch(drtReceiverPlane,pixel,0).r;
    if(plane==(-2147483647-1))return;
    uint token;vec3 p;uvec2 support;
    if(drtIsFoliagePlane(plane)) {
        // Read the rest pose directly; intersecting the wind-bent screen ray
        // with the rest plane would slide cached shadows as the card moves.
        p=drtUnpackFoliagePosition(plane,texelFetch(drtReceiverBounds,pixel,0).rg,token,plane);
        if(token==0u||dot(drtPlacedSurfaceNormalVector(token),drtPlacedSurfaceNormalVector(token))<=0.0)return;
        token|=DRT_FOLIAGE_PAGE_TOKEN;
        support=drtFoliagePageBounds(p,token);
    } else {
    token=drtPlacedSurfaceNormalToken(texelFetch(drtPageNormal,pixel,0).w);
    vec3 n=drtPlacedSurfaceNormalVector(token);if(token==0u||token>=4096u||dot(n,n)<=0.0)return;
    vec2 uv=(vec2(pixel)+0.5)/vec2(drtPageExtent);
    vec4 v=drtPageInverseProjection*vec4(uv*2.0-1.0,-1,1);
    vec3 ray=mat3(drtPageInverseView)*(v.xyz/v.w);float denominator=dot(n,ray);
    if(abs(denominator)<1e-8)return;
    float distance=(float(plane)/1024.0-dot(n,drtMemoCameraOffset))/denominator;
    if(distance<=0.0)return;
    p=drtMemoCameraOffset+ray*distance;
    support=texelFetch(drtReceiverBounds,pixel,0).rg;
    }
    if(!drtPageInfluenced(p))return;
    if(all(equal(support,uvec2(0xffffffffu))))return;
    uvec4 key=drtPageKey(p,token,plane);uint hash=drtPageHash(key,support);
    int first=int(hash&uint(drtMemoCapacityMask))*4,slot=-1,victim=-1;
    uint oldest=0u,expected=0u;int victimRank=-1;bool busyUnknown=false;
    for(int bucket=0;bucket<2;++bucket) {
        if(bucket==1)first=drtPageSecondaryBucket(hash)*4;
        for(int way=0;way<4;++way) {
            int index=first+way;uvec4 s=drtMemoPages[index].state;
            bool ours=(s.x&0x3fffffffu)==drtMemoGeneration;
            if(ours&&all(equal(drtMemoPages[index].key,key))&&all(equal(drtMemoPages[index].support.xy,support))) {slot=index;expected=s.x;break;}
            if(ours&&(s.x&DRT_PAGE_BUSY)!=0u) {busyUnknown=true;continue;}
            uint age=ours?drtMemoAge(s.y):0x3fffffffu;
            // Age is only a collision guard, never a page lifetime. Prefer an
            // unused epoch, then cold unfilled/negative entries, before losing
            // complete lighting from a view the player may turn back toward.
            // Ineligible young entries cannot hide an eligible complete victim.
            if(ours&&age<120u)continue;
            int rank=!ours?2:((s.z==0u||s.z==3u)?1:0);
            if(victim<0||rank>victimRank||(rank==victimRank&&age>oldest)) {
                victim=index;victimRank=rank;oldest=age;expected=s.x;
            }
        }
        // Search both identities before choosing a victim: a previously stored
        // secondary page must never be duplicated into an available primary slot.
        if(slot>=0||drtMemoCapacityMask==0)break;
    }
    bool fresh=slot<0;
    if(!fresh) {
        // Most neighboring pixels share a page. Its coherent last-touch value
        // avoids millions of redundant global atomics during an edit burst;
        // the atomic below still arbitrates simultaneous first observations.
        if(drtMemoPages[slot].state.y==drtMemoFrame)return;
        // Every collector in this ordered dispatch observes the same frame.
        // Exchange also advances correctly when the 30-bit frame clock wraps;
        // atomicMax would leave a visible page stuck at the previous maximum.
        if(atomicExchange(drtMemoPages[slot].state.y,drtMemoFrame)==drtMemoFrame)return;
        if((expected&DRT_PAGE_BUSY)!=0u)return;
        // Missing depth is a prerequisite, not useful queued work. A completed
        // bank stays visible while this page waits for a new source/depth snapshot.
        if(drtMemoPages[slot].bankMeta[1].w==drtMemoEvaluationRevision)return;
        uint published=drtMemoPages[slot].state.z;
        // Dense fallback depends on source geometry/count, never depth or RGB.
        // Retain it across unified regional lighting/depth revisions.
        if(published==3u&&drtMemoPages[slot].bankMeta[0].z==drtMemoDenseSourceRevision)return;
        uint stamp=drtPageStamp(key,support);
        if(published!=0u&&published<3u&&(drtMemoPages[slot].state.w==stamp||
            (published<3u&&drtMemoAge(drtMemoPages[slot].bankMeta[int(published)-1].y)<uint(max(drtMemoTransitionFrames,1)))))return;
    } else {
        // Reclaim cold whole pages only. The ordered compute->relight barrier
        // makes reuse fence-safe without a render-thread wait or readback.
        if(busyUnknown||victim<0||oldest<120u)return;
        // Keep the non-BUSY snapshot used to select this victim. Re-reading
        // BUSY here could turn a competing writer's BUSY->BUSY CAS into a claim.
        slot=victim;
    }
    if(atomicCompSwap(drtMemoPages[slot].state.x,expected,DRT_PAGE_BUSY|drtMemoGeneration)!=expected)return;
    // A refused enqueue releases the same world epoch. Another collector may
    // have reused this slot between our snapshot and claim (same-epoch ABA).
    // Validate the claimed identity/age before writing or queuing its hash.
    memoryBarrierBuffer();
    bool changedIdentity=!fresh&&(!all(equal(drtMemoPages[slot].key,key))||
        !all(equal(drtMemoPages[slot].support.xy,support)));
    bool recentlyTouchedVictim=fresh&&(expected&0x3fffffffu)==drtMemoGeneration&&
        drtMemoAge(atomicAdd(drtMemoPages[slot].state.y,0u))<120u;
    if(changedIdentity||recentlyTouchedVictim) {
        atomicExchange(drtMemoPages[slot].state.x,expected);
        return;
    }
    if(fresh) {
        drtMemoPages[slot].key=key;drtMemoPages[slot].support=uvec4(support,0,0);drtMemoPages[slot].state.zw=uvec2(0);
        drtMemoPages[slot].bankMeta[0]=uvec4(0);drtMemoPages[slot].bankMeta[1]=uvec4(0);
    }
    drtMemoPages[slot].state.y=drtMemoFrame;
    if(!drtQueueVisiblePage(uvec4(uint(slot),drtMemoGeneration,hash,0))) {
        // A full ring leaves this page retryable and preserves any complete bank.
        memoryBarrierBuffer();
        atomicExchange(drtMemoPages[slot].state.x,drtMemoGeneration);
    }
}
#elif defined(DRT_PAGE_SCHEDULE)
layout(local_size_x=1) in;
bool drtQueuedPageValid(uvec4 request) {
    if(request.y!=drtMemoGeneration||request.x>=uint(drtMemoPages.length()))return false;
    int slot=int(request.x);
    return drtMemoPages[slot].state.x==(DRT_PAGE_BUSY|drtMemoGeneration)&&
        drtPageHash(drtMemoPages[slot].key,drtMemoPages[slot].support.xy)==request.z;
}
void main() {
    // Collection and scheduling are ordered dispatches. Prefer visible dirty
    // history over cold pages, then retain FIFO order within each priority.
    // Off-screen requests release only their lock; completed banks are retained.
    uint head=queueControl.x,tail=queueControl.y;
    uint count=min(tail-head,uint(DRT_PAGE_QUEUE_CAPACITY));
    // Overflow reservations own no ring entry. Repair the monotonic tail at
    // this dispatch boundary before compacting the valid contiguous prefix.
    tail=head+count;queueControl.y=tail;
    uint limit=min(drtPageBudget,uint(DRT_PAGE_JOB_CAPACITY)),jobs=0u;
    for(uint i=0u;i<count;++i) {
        uint at=(head+i)&uint(DRT_PAGE_QUEUE_CAPACITY-1);uvec4 r=requests[at];
        if(!drtQueuedPageValid(r)) {requests[at].x=0xffffffffu;continue;}
        int slot=int(r.x);uint age=drtMemoAge(drtMemoPages[slot].state.y);
        if(age>max(drtPageVisibleFrames,1u)) {
            atomicExchange(drtMemoPages[slot].state.x,drtMemoGeneration);
            requests[at].x=0xffffffffu;
        }
    }
    for(int priority=0;priority<4&&jobs<limit;++priority)for(uint i=0u;i<count&&jobs<limit;++i) {
        uint at=(head+i)&uint(DRT_PAGE_QUEUE_CAPACITY-1);uvec4 r=requests[at];
        if(r.x==0xffffffffu)continue;
        int slot=int(r.x);uint published=drtMemoPages[slot].state.z;
        bool refresh=published>0u&&published<3u;
        bool current=drtMemoPages[slot].state.y==drtMemoFrame;
        int rank=(refresh?0:2)+(current?0:1);
        if(rank!=priority)continue;
        jobSlots[jobs++]=r;requests[at].x=0xffffffffu;
    }
    // Compact backwards toward the unchanged tail. Each destination is at or
    // after its source, so an unread ring entry cannot be overwritten, including
    // a full wrapped ring. The head advances monotonically by removed requests.
    uint retained=0u;
    for(uint i=count;i>0u;--i) {
        uvec4 r=requests[(head+i-1u)&uint(DRT_PAGE_QUEUE_CAPACITY-1)];
        if(r.x==0xffffffffu)continue;
        requests[(tail-1u-retained)&uint(DRT_PAGE_QUEUE_CAPACITY-1)]=r;++retained;
    }
    queueLimits.x=jobs;
    memoryBarrierBuffer();
    queueControl.x=tail-retained;
}
#else
layout(local_size_x=64) in;
uniform sampler2DArrayShadow drtStaticMaps;
uniform vec2 drtPlacedCalibration[32];
#include drtagx_light_balance.ash
#include drtagx_deferred_cube.fsh
#include drtagx_deferred_staticshadows.fsh
shared int pageSlot,bank,localCount;
shared bool postponed,denseNative;
shared uvec4 pageKey;
shared uvec2 pageSupport;
shared uint pageStamp;
// Match the previous 128-light publication budget without truncating a field.
// Larger truthful neighborhoods retain explicit native fallback, never a top-N sum.
const int DRT_PAGE_SOURCE_LIMIT=128;
shared int localSources[DRT_PAGE_SOURCE_LIMIT];
vec3 drtPageStyledRadiance(vec3 rgb,float strength,float visibility) {
    // Fully occluded sources contribute zero PLS radiance. Partial PCF
    // visibility still softens shadow edges; emitter self-light is separate.
    return strength*rgb*visibility;
}
void main() {
    if(gl_LocalInvocationIndex==0u) {
        pageSlot=-1;localCount=0;postponed=false;denseNative=false;
        if(gl_WorkGroupID.x<queueLimits.x) {
            uvec4 r=jobSlots[gl_WorkGroupID.x];int candidate=int(r.x);
            if(r.y==drtMemoGeneration&&drtMemoPages[candidate].state.x==(DRT_PAGE_BUSY|drtMemoGeneration)&&
                drtPageHash(drtMemoPages[candidate].key,drtMemoPages[candidate].support.xy)==r.z)pageSlot=candidate;
        }
        if(pageSlot>=0) {
            pageKey=drtMemoPages[pageSlot].key;pageSupport=drtMemoPages[pageSlot].support.xy;pageStamp=drtPageStamp(pageKey,pageSupport);
            bank=drtMemoPages[pageSlot].state.z==1u?1:0;
            vec3 center=drtPageReceiver(pageKey,ivec2(8),pageSupport);
            // Prepare the complete local set once per brick. More than 128
            // potential contributors uses native lighting for this revision;
            // a page-centred truncated set can disagree at shared halos.
            int potentialCount=0;
            for(int i=0;i<drtMemoSourceCount;++i) {
                vec4 s=drtMemoSourceData[i*4];vec3 delta=s.xyz-center;float d2=dot(delta,delta);
                // A one-block dominant-axis brick has half-width .5. Its
                // steepest eligible plane reaches sqrt(1.5) from the centre;
                // 1.25 blocks conservatively covers that halo without PCF work
                // for an individual node outside the actual source radius.
                float radius=s.w+1.25*DRT_PAGE_BLOCKS;if(s.w<=0.0||d2>=radius*radius)continue;
                if(++potentialCount>DRT_PAGE_SOURCE_LIMIT) {denseNative=true;postponed=false;break;}
                // A known source with dirty/missing depth is a prerequisite,
                // not a zero contribution. Retain the whole completed bank.
                if(drtMemoSourceData[i*4+2].x<0.5) {postponed=true;continue;}
                localSources[localCount++]=i;
            }
        }
    }
    barrier();if(pageSlot<0)return;
    if(denseNative) {
        if(gl_LocalInvocationIndex==0u) {
            // Bank3 is a completed native-fallback sentinel, never a node bank.
            // Clear prior fades so a later sparse revision starts from native.
            drtMemoPages[pageSlot].bankMeta[0]=uvec4(0);drtMemoPages[pageSlot].bankMeta[1]=uvec4(0);
            drtMemoPages[pageSlot].bankMeta[0].z=drtMemoDenseSourceRevision;
            drtMemoPages[pageSlot].state.zw=uvec2(3u,pageStamp);
            memoryBarrierBuffer();atomicExchange(drtMemoPages[pageSlot].state.x,drtMemoGeneration);
            atomicAdd(queueControl.w,1u);atomicAdd(drtMemoControl.w,1u);
        }
        return;
    }
    if(postponed) {
        if(gl_LocalInvocationIndex==0u) {
            // Retrying the same unavailable snapshot would keep this page BUSY
            // forever and let old jobs crowd out newly changed visible surfaces.
            // The collector retries when coherent source/depth publication changes.
            drtMemoPages[pageSlot].bankMeta[1].w=drtMemoEvaluationRevision;
            memoryBarrierBuffer();atomicExchange(drtMemoPages[pageSlot].state.x,drtMemoGeneration);
        }
        return;
    }
    vec3 normal=normalize(drtPlacedSurfaceNormalVector(pageKey.w));
    for(uint node=gl_LocalInvocationIndex;node<289u;node+=64u) {
        vec3 p=drtPageReceiver(pageKey,ivec2(int(node)%17,int(node)/17),pageSupport);
        DrtPlacedLightSum sum;drtClearPlacedSum(sum);vec4 self=vec4(0);float coverage=0.0;
        for(int rank=0;rank<localCount;++rank) {
            int i=localSources[rank];vec4 s=drtMemoSourceData[i*4],c=drtMemoSourceData[i*4+1],prepared=drtMemoSourceData[i*4+3];
            vec3 q=p-prepared.xyz;float d2=dot(q,q);if(d2>=s.w*s.w)continue;
            float distance=sqrt(d2);int level=clamp(int(prepared.w),0,31);
            coverage=max(coverage,1.0-smoothstep(0.82*s.w,s.w,distance));
            if(all(lessThan(abs(q-normal*0.003),vec3(0.5)))) {
                // Source-derived self-light shares the placed RGB gain; material glow is separate.
                self.rgb=max(self.rgb,c.rgb*drtPlacedCalibration[level].y*DRT_PLACED_INTENSITY*max(1.0,0.2+0.05*float(level)));self.a=1.0;continue;
            }
            float planeDistance=dot(normal,q),facing=0.28+0.72*max(-planeDistance/max(distance,1e-5),0.0);
            float strength=drtPlacedCalibration[level].x*(1.0-distance/s.w)*(0.75*facing/(1.0+0.25*d2)+0.2);
            if(strength<0.0005)continue;
            // Foliage receives walls and solid terrain from either side of its
            // immutable card; it remains absent from the static caster atlas.
            float visibility=(pageKey.w&DRT_FOLIAGE_PAGE_TOKEN)!=0u ?
                drtStaticTwoSidedVisibilityWithTolerance(q,normal,int(c.w),-1,0.0,0.02) :
                drtStaticVisibility(q,normal,distance,planeDistance,int(c.w),-1,0.0);
            drtAddPlacedLight(sum,drtPageStyledRadiance(c.rgb,strength,visibility));
        }
        int at=bank*289+int(node);drtMemoPages[pageSlot].nodes[at].radianceCoverage=vec4(drtPlacedSumRadiance(sum),coverage);
        // A retains only emitter classification. The uniform half-voxel floor
        // needs no per-source blocked-energy mask or extra storage.
        drtMemoPages[pageSlot].nodes[at].selfLightEmitter=self;
    }
    memoryBarrierBuffer();barrier();
    if(gl_LocalInvocationIndex==0u) {
        // One ordered dispatch uses one source/depth snapshot. No job carries
        // partial PCF results across frames or observes a reused atlas slot.
        drtMemoPages[pageSlot].bankMeta[bank]=uvec4(pageStamp,drtMemoFrame,0,0);
        drtMemoPages[pageSlot].state.zw=uvec2(uint(bank+1),pageStamp);
        memoryBarrierBuffer();atomicExchange(drtMemoPages[pageSlot].state.x,drtMemoGeneration);
        atomicAdd(queueControl.w,1u);atomicAdd(drtMemoControl.w,1u);
    }
}
#endif
