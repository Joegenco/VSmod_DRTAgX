// Capability marker: the engine compiles standard with native fog/light before
// Sheyder installs its include loader. Only this shared implementation provides
// the explicit sun/local interface used after that startup compile.
#define DRT_EXPLICIT_SURFACE_LIGHTING

// Light metadata is table-derived radiance; only authored textures use a transfer curve.
const float DRT_BASE_SUN_GAIN = 1.0;
// Linear native fallback for startup, previews or an unverified ambient provider.
const float DRT_AMBIENT_GAIN = 2.0;
// Minimum sky multiplier after the ambient overlays. Sky access still masks it
// on surfaces, so this adds no texture-independent floor or sealed-cave light.
const vec3 DRT_AMBIENT_MIN = vec3(0.05, 0.1, 0.15);
const float DRT_BASE_LOCAL_GAIN = 1.0;
// Increase moving/held light strength by 25% over the existing 1.5 gain.
const float DRT_DYNAMIC_GAIN = 1.5 * 1.25 * DRT_BASE_LOCAL_GAIN;
const float DRT_EMISSION_GAIN = 4.0;
// Shared by vertex/fragment directional shading and the sun CSM only.
const float DRT_SUN_SHADOW_GAIN = 0.65;
const vec3 DRT_LUMINANCE = vec3(0.2126, 0.7152, 0.0722);

float drtPlacedFoliageFacing(vec3 planeNormal, vec3 toLight, vec3 toEye) {
    float n2 = dot(planeNormal, planeNormal), l2 = dot(toLight, toLight), v2 = dot(toEye, toEye);
    if (n2 <= 1e-6 || l2 <= 1e-10 || v2 <= 1e-10) return 1.0;
    vec3 normal = planeNormal * inversesqrt(n2);
    float viewSide = dot(normal, toEye) * inversesqrt(v2);
    // A no-cull card has two visible sides. Orient to the camera in its rest
    // pose, not mesh winding or a synthetic shade:false authored normal.
    float facing = dot(normal, toLight) * inversesqrt(l2) * (viewSide < 0.0 ? -1.0 : 1.0);
    // A narrow edge-on band (~0.6..2.3 degrees) removes sign-flip flashes.
    // Ordinary PLS keeps exactly 1:0.25 for unobstructed facing/away sides.
    float edgeTolerance = smoothstep(0.01, 0.04, abs(viewSide));
    return 0.25 + 0.75 * max(facing, 0.0) * edgeTolerance;
}

// Share an eight-source harmonic budget across placed and moving lighting.
// Rank actual receiver radiance, so occluded/out-of-range lights use no rank.
const int DRT_LIGHT_BUDGET = 8;
const float DRT_LIGHT_TIE_WIDTH = 0.05;

float drtLightRelativeStrength(float a, float b) {
    return abs(a - b) / max(max(a, b), 1e-8);
}

void drtClearLightRanks(out vec3 colors[DRT_LIGHT_BUDGET], out float scores[DRT_LIGHT_BUDGET]) {
    for (int i = 0; i < DRT_LIGHT_BUDGET; ++i) { colors[i] = vec3(0.0); scores[i] = 0.0; }
}

int drtInsertLightRank(inout vec3 colors[DRT_LIGHT_BUDGET], inout float scores[DRT_LIGHT_BUDGET], vec3 contribution) {
    float score = dot(contribution, DRT_LUMINANCE);
    if (!(score > 0.0) || isnan(score) || isinf(score)) return -1;
    for (int i = 0; i < DRT_LIGHT_BUDGET; ++i) {
        // Resolve exact color ties consistently, independent of source order.
        bool colorFirst = contribution.r > colors[i].r ||
            (contribution.r == colors[i].r && (contribution.g > colors[i].g ||
            (contribution.g == colors[i].g && contribution.b > colors[i].b)));
        if (score > scores[i] || (score == scores[i] && colorFirst)) {
            for (int j = DRT_LIGHT_BUDGET - 1; j > i; --j) {
                colors[j] = colors[j - 1]; scores[j] = scores[j - 1];
            }
            colors[i] = contribution; scores[i] = score;
            return i;
        }
    }
    return -1; // Ninth and weaker sources cannot enter lighting or tie pools.
}

void drtInsertFacingLightRank(inout vec3 colors[DRT_LIGHT_BUDGET], inout float scores[DRT_LIGHT_BUDGET],
    inout vec2 facingMoments[DRT_LIGHT_BUDGET], vec3 contribution, vec2 moments) {
    int rank = drtInsertLightRank(colors, scores, contribution);
    if (rank < 0) return;
    // Keep unshaded energy/facing attached to the same selected light.
    for (int j = DRT_LIGHT_BUDGET - 1; j > rank; --j) facingMoments[j] = facingMoments[j - 1];
    facingMoments[rank] = moments;
}

void drtHarmonicLightWeights(float scores[DRT_LIGHT_BUDGET], out float weights[DRT_LIGHT_BUDGET]) {
    for (int i = 0; i < DRT_LIGHT_BUDGET; ++i) weights[i] = 0.0;
    for (int rank = 0; rank < DRT_LIGHT_BUDGET; ++rank) {
        if (!(scores[rank] > 0.0)) break;
        float rankWeight = 1.0 / float(rank + 1);
        bool tied = (rank > 0 && drtLightRelativeStrength(scores[rank], scores[rank - 1]) < DRT_LIGHT_TIE_WIDTH)
            || (rank + 1 < DRT_LIGHT_BUDGET && scores[rank + 1] > 0.0 &&
                drtLightRelativeStrength(scores[rank], scores[rank + 1]) < DRT_LIGHT_TIE_WIDTH);
        if (!tied) { weights[rank] += rankWeight; continue; }
        // Preserve smooth colored ties inside the selected eight only. Each
        // pool redistributes one rank's weight without increasing its energy.
        float kernels[DRT_LIGHT_BUDGET], total = 0.0;
        for (int i = 0; i < DRT_LIGHT_BUDGET; ++i) {
            kernels[i] = scores[i] > 0.0 ? 1.0 - smoothstep(0.0, DRT_LIGHT_TIE_WIDTH,
                drtLightRelativeStrength(scores[i], scores[rank])) : 0.0;
            total += kernels[i];
        }
        for (int i = 0; i < DRT_LIGHT_BUDGET; ++i) weights[i] += rankWeight * kernels[i] / max(total, 1e-8);
    }
}

vec3 drtSumLightRanks(vec3 colors[DRT_LIGHT_BUDGET], float weights[DRT_LIGHT_BUDGET]) {
    vec3 result = vec3(0.0);
    for (int i = 0; i < DRT_LIGHT_BUDGET; ++i) result += colors[i] * weights[i];
    return result;
}
uniform vec2 drtSunlightBounds = vec2(0.0, 1.0); // live world: zero/full sunlight

float drtSunAccess(float alpha) {
    return clamp((alpha - drtSunlightBounds.x) /
        max(drtSunlightBounds.y - drtSunlightBounds.x, 1e-6), 0.0, 1.0);
}

vec3 drtAmbientSky(vec3 nativeSky) {
#ifdef DRT_FOG_TRANSPORT
    if (drtFogColor.w > 0.5 && drtAmbientScale.w > 0.5 && drtAmbientResolved.w > 0.5) {
        vec3 resolved = drtAmbientResolved.rgb * drtAmbientScale.rgb + drtAmbientOverlay.rgb;
        // Preserve caller-specific native ambient multipliers (e.g. mod/entity tints).
        // Near-zero reference channels cannot define a ratio: use their native fallback.
        vec3 ratio = vec3(1.0);
        for (int i = 0; i < 3; ++i) {
            if (drtAmbientNative[i] > 0.00001) ratio[i] = max(nativeSky[i], 0.0) / drtAmbientNative[i];
            else if (nativeSky[i] > 0.00001) return max(DRT_AMBIENT_MIN, DRT_AMBIENT_GAIN * max(nativeSky, vec3(0.0)));
        }
        return max(DRT_AMBIENT_MIN, max(resolved * ratio, vec3(0.0)));
    }
#endif
    // No per-channel power curve: radiance and chromaticity remain linear.
    return max(DRT_AMBIENT_MIN, DRT_AMBIENT_GAIN * max(nativeSky, vec3(0.0)));
}

vec3 drtSunRadiance(float access, vec3 sky) {
    float horizonGain = 1.0;
#ifdef DRT_FOG_TRANSPORT
    if (drtFogColor.w > 0.5) {
        // One stop of surface skylight at sunset elevations (-2..+3 degrees).
        // Fade smoothly to the existing gain at -6 degrees and +10 degrees.
        float elevation = drtAtmosphereSun.y;
        float twilight = smoothstep(sin(radians(-6.0)), sin(radians(-2.0)), elevation) *
            (1.0 - smoothstep(sin(radians(3.0)), sin(radians(10.0)), elevation));
        horizonGain += twilight;
    }
#endif
    return DRT_BASE_SUN_GAIN * horizonGain * access * max(sky, vec3(0.0));
}

float drtSunFaceBrightness(float nativeBrightness, float facing, float shadowDarkness) {
    // A tangent face reaches the same darkness as a fully occluded sun map.
    // This hides the brighter contact strip exposed by CSM receiver/caster bias;
    // fully sun-facing surfaces retain native brightness and their cast shadows.
    float faceBrightness = 1.0 - clamp(shadowDarkness, 0.0, 1.0) *
        (1.0 - clamp(facing, 0.0, 1.0));
    return min(nativeBrightness, faceBrightness);
}

float drtCombineSunDarkening(float shadowBrightness, float faceBrightness) {
    // Native-style maximum darkness, expressed as brightness for the existing
    // sun-only compositor. Do not multiply two occlusion terms or lift shadows.
    float darkness = max(1.0 - clamp(shadowBrightness, 0.0, 1.0),
        1.0 - clamp(faceBrightness, 0.0, 1.0));
    return 1.0 - darkness;
}

// Continuous deferred sun occupies 0..63; leave a full unit before the +64
// caster tag and +128 forward-foliage tag. RGBA16F retains about 1000 steps
// even with +64, instead of rounding the interpolated value to 32 levels.
float drtPackDeferredSun(float access) {
    return clamp(access, 0.0, 1.0) * 63.0;
}

float drtUnpackDeferredSun(float value) {
    return clamp(value / 63.0, 0.0, 1.0);
}

// Historical two-value packing retained for before-state numerical probes.
// Current terrain carries blocklight RGB in Color RGB, not in this fraction.
float drtPackSunBlock(float access, float blockBrightness) {
    // 5-bit normalized sky access uses a 2-unit stride. With +64/+128 tags,
    // RGBA16F has up to 1/8-unit spacing; .875 prevents rounding the fraction to 1.
    return floor(clamp(access, 0.0, 1.0) * 31.0 + 0.5) * 2.0 +
        clamp(blockBrightness, 0.0, 0.875);
}

vec2 drtUnpackSunBlock(float packedLightValue) {
    float level = floor(packedLightValue / 2.0 + 0.01);
    return vec2(clamp(level / 31.0, 0.0, 1.0), clamp(packedLightValue - level * 2.0, 0.0, 0.875));
}

vec3 drtDynamicContribution(vec3 delta, vec3 authored, vec3 normal, float visibility) {
    float range = length(authored);
    float peak = max(authored.r, max(authored.g, authored.b));
    float d2 = dot(delta, delta);
    if (range <= 1e-6 || peak <= 1e-6 || d2 >= range * range || d2 > 2304.0)
        return vec3(0.0);
    float d = sqrt(d2);
    float w = max(0.0, 1.0 - d / range);
    float n2 = dot(normal, normal);
    // At the emitter center no direction is defined; retain finite, full radiance.
    float facing = n2 > 1e-6 && d2 > 1e-10
        ? 0.28 + 0.72 * max(dot(normal * inversesqrt(n2), delta / d), 0.0) : 1.0;
    return DRT_DYNAMIC_GAIN * max(authored / peak, vec3(0.0)) *
        (w * sqrt(w) / (1.0 + 0.25 * d)) * min(facing, clamp(visibility, 0.0, 1.0));
}

// Fixed eight-rank state keeps receiver scratch out of dynamically indexed
// arrays. The fourth component stores the unchanged luminance ordering score.
struct DrtLightRanks { vec4 q0,q1,q2,q3,q4,q5,q6,q7; };
void drtClearRanks(out DrtLightRanks ranks) {
    ranks.q0 = vec4(0.0);
    ranks.q1 = vec4(0.0);
    ranks.q2 = vec4(0.0);
    ranks.q3 = vec4(0.0);
    ranks.q4 = vec4(0.0);
    ranks.q5 = vec4(0.0);
    ranks.q6 = vec4(0.0);
    ranks.q7 = vec4(0.0);
}
int drtInsertRank(inout DrtLightRanks ranks, vec3 contribution) {
    float score = dot(contribution, DRT_LUMINANCE);
    if (!(score > 0.0) || isnan(score) || isinf(score)) return -1;
    vec4 candidate = vec4(contribution, score);
    int first = -1;
    {
        bool colorFirst = candidate.r > ranks.q0.r ||
            (candidate.r == ranks.q0.r && (candidate.g > ranks.q0.g ||
            (candidate.g == ranks.q0.g && candidate.b > ranks.q0.b)));
        if (candidate.w > ranks.q0.w || (candidate.w == ranks.q0.w && colorFirst)) {
            vec4 displaced = ranks.q0; ranks.q0 = candidate; candidate = displaced;
            if (first < 0) first = 0;
        }
    }
    {
        bool colorFirst = candidate.r > ranks.q1.r ||
            (candidate.r == ranks.q1.r && (candidate.g > ranks.q1.g ||
            (candidate.g == ranks.q1.g && candidate.b > ranks.q1.b)));
        if (candidate.w > ranks.q1.w || (candidate.w == ranks.q1.w && colorFirst)) {
            vec4 displaced = ranks.q1; ranks.q1 = candidate; candidate = displaced;
            if (first < 0) first = 1;
        }
    }
    {
        bool colorFirst = candidate.r > ranks.q2.r ||
            (candidate.r == ranks.q2.r && (candidate.g > ranks.q2.g ||
            (candidate.g == ranks.q2.g && candidate.b > ranks.q2.b)));
        if (candidate.w > ranks.q2.w || (candidate.w == ranks.q2.w && colorFirst)) {
            vec4 displaced = ranks.q2; ranks.q2 = candidate; candidate = displaced;
            if (first < 0) first = 2;
        }
    }
    {
        bool colorFirst = candidate.r > ranks.q3.r ||
            (candidate.r == ranks.q3.r && (candidate.g > ranks.q3.g ||
            (candidate.g == ranks.q3.g && candidate.b > ranks.q3.b)));
        if (candidate.w > ranks.q3.w || (candidate.w == ranks.q3.w && colorFirst)) {
            vec4 displaced = ranks.q3; ranks.q3 = candidate; candidate = displaced;
            if (first < 0) first = 3;
        }
    }
    {
        bool colorFirst = candidate.r > ranks.q4.r ||
            (candidate.r == ranks.q4.r && (candidate.g > ranks.q4.g ||
            (candidate.g == ranks.q4.g && candidate.b > ranks.q4.b)));
        if (candidate.w > ranks.q4.w || (candidate.w == ranks.q4.w && colorFirst)) {
            vec4 displaced = ranks.q4; ranks.q4 = candidate; candidate = displaced;
            if (first < 0) first = 4;
        }
    }
    {
        bool colorFirst = candidate.r > ranks.q5.r ||
            (candidate.r == ranks.q5.r && (candidate.g > ranks.q5.g ||
            (candidate.g == ranks.q5.g && candidate.b > ranks.q5.b)));
        if (candidate.w > ranks.q5.w || (candidate.w == ranks.q5.w && colorFirst)) {
            vec4 displaced = ranks.q5; ranks.q5 = candidate; candidate = displaced;
            if (first < 0) first = 5;
        }
    }
    {
        bool colorFirst = candidate.r > ranks.q6.r ||
            (candidate.r == ranks.q6.r && (candidate.g > ranks.q6.g ||
            (candidate.g == ranks.q6.g && candidate.b > ranks.q6.b)));
        if (candidate.w > ranks.q6.w || (candidate.w == ranks.q6.w && colorFirst)) {
            vec4 displaced = ranks.q6; ranks.q6 = candidate; candidate = displaced;
            if (first < 0) first = 6;
        }
    }
    {
        bool colorFirst = candidate.r > ranks.q7.r ||
            (candidate.r == ranks.q7.r && (candidate.g > ranks.q7.g ||
            (candidate.g == ranks.q7.g && candidate.b > ranks.q7.b)));
        if (candidate.w > ranks.q7.w || (candidate.w == ranks.q7.w && colorFirst)) {
            vec4 displaced = ranks.q7; ranks.q7 = candidate; candidate = displaced;
            if (first < 0) first = 7;
        }
    }
    return first;
}
float drtRankKernel(float score, float center) {
    return score > 0.0 ? 1.0 - smoothstep(0.0, DRT_LIGHT_TIE_WIDTH, drtLightRelativeStrength(score, center)) : 0.0;
}
void drtShareRankWeights(float center, float harmonic, vec4 low, vec4 high, inout vec4 lowWeight, inout vec4 highWeight) {
    vec4 a = vec4(drtRankKernel(low.x,center),drtRankKernel(low.y,center),drtRankKernel(low.z,center),drtRankKernel(low.w,center));
    vec4 b = vec4(drtRankKernel(high.x,center),drtRankKernel(high.y,center),drtRankKernel(high.z,center),drtRankKernel(high.w,center));
    // Preserve sequential kernel sums and weight accumulation order.
    float total=0.0; total+=a.x; total+=a.y; total+=a.z; total+=a.w;
    total+=b.x; total+=b.y; total+=b.z; total+=b.w;
    lowWeight += harmonic*a/max(total,1e-8); highWeight += harmonic*b/max(total,1e-8);
}
struct DrtRankWeights { vec4 low,high; };
DrtRankWeights drtRankWeights(DrtLightRanks ranks) {
    vec4 low=vec4(ranks.q0.w,ranks.q1.w,ranks.q2.w,ranks.q3.w);
    vec4 high=vec4(ranks.q4.w,ranks.q5.w,ranks.q6.w,ranks.q7.w);
    vec4 lowWeight=vec4(0.0),highWeight=vec4(0.0);
    if (ranks.q0.w > 0.0) {
        if ((ranks.q1.w > 0.0 && drtLightRelativeStrength(ranks.q0.w, ranks.q1.w) < DRT_LIGHT_TIE_WIDTH)) drtShareRankWeights(ranks.q0.w,1.0/float(1),low,high,lowWeight,highWeight);
        else lowWeight.x += 1.0/float(1);
    }
    if (ranks.q1.w > 0.0) {
        if (drtLightRelativeStrength(ranks.q1.w, ranks.q0.w) < DRT_LIGHT_TIE_WIDTH || (ranks.q2.w > 0.0 && drtLightRelativeStrength(ranks.q1.w, ranks.q2.w) < DRT_LIGHT_TIE_WIDTH)) drtShareRankWeights(ranks.q1.w,1.0/float(2),low,high,lowWeight,highWeight);
        else lowWeight.y += 1.0/float(2);
    }
    if (ranks.q2.w > 0.0) {
        if (drtLightRelativeStrength(ranks.q2.w, ranks.q1.w) < DRT_LIGHT_TIE_WIDTH || (ranks.q3.w > 0.0 && drtLightRelativeStrength(ranks.q2.w, ranks.q3.w) < DRT_LIGHT_TIE_WIDTH)) drtShareRankWeights(ranks.q2.w,1.0/float(3),low,high,lowWeight,highWeight);
        else lowWeight.z += 1.0/float(3);
    }
    if (ranks.q3.w > 0.0) {
        if (drtLightRelativeStrength(ranks.q3.w, ranks.q2.w) < DRT_LIGHT_TIE_WIDTH || (ranks.q4.w > 0.0 && drtLightRelativeStrength(ranks.q3.w, ranks.q4.w) < DRT_LIGHT_TIE_WIDTH)) drtShareRankWeights(ranks.q3.w,1.0/float(4),low,high,lowWeight,highWeight);
        else lowWeight.w += 1.0/float(4);
    }
    if (ranks.q4.w > 0.0) {
        if (drtLightRelativeStrength(ranks.q4.w, ranks.q3.w) < DRT_LIGHT_TIE_WIDTH || (ranks.q5.w > 0.0 && drtLightRelativeStrength(ranks.q4.w, ranks.q5.w) < DRT_LIGHT_TIE_WIDTH)) drtShareRankWeights(ranks.q4.w,1.0/float(5),low,high,lowWeight,highWeight);
        else highWeight.x += 1.0/float(5);
    }
    if (ranks.q5.w > 0.0) {
        if (drtLightRelativeStrength(ranks.q5.w, ranks.q4.w) < DRT_LIGHT_TIE_WIDTH || (ranks.q6.w > 0.0 && drtLightRelativeStrength(ranks.q5.w, ranks.q6.w) < DRT_LIGHT_TIE_WIDTH)) drtShareRankWeights(ranks.q5.w,1.0/float(6),low,high,lowWeight,highWeight);
        else highWeight.y += 1.0/float(6);
    }
    if (ranks.q6.w > 0.0) {
        if (drtLightRelativeStrength(ranks.q6.w, ranks.q5.w) < DRT_LIGHT_TIE_WIDTH || (ranks.q7.w > 0.0 && drtLightRelativeStrength(ranks.q6.w, ranks.q7.w) < DRT_LIGHT_TIE_WIDTH)) drtShareRankWeights(ranks.q6.w,1.0/float(7),low,high,lowWeight,highWeight);
        else highWeight.z += 1.0/float(7);
    }
    if (ranks.q7.w > 0.0) {
        if (drtLightRelativeStrength(ranks.q7.w, ranks.q6.w) < DRT_LIGHT_TIE_WIDTH) drtShareRankWeights(ranks.q7.w,1.0/float(8),low,high,lowWeight,highWeight);
        else highWeight.w += 1.0/float(8);
    }
    return DrtRankWeights(lowWeight,highWeight);
}
vec3 drtRankRadiance(DrtLightRanks ranks,DrtRankWeights weights) {
    vec3 result=vec3(0.0);
    result += ranks.q0.rgb*weights.low.x;
    result += ranks.q1.rgb*weights.low.y;
    result += ranks.q2.rgb*weights.low.z;
    result += ranks.q3.rgb*weights.low.w;
    result += ranks.q4.rgb*weights.high.x;
    result += ranks.q5.rgb*weights.high.y;
    result += ranks.q6.rgb*weights.high.z;
    result += ranks.q7.rgb*weights.high.w;
    return result;
}
vec3 drtRankRadiance(DrtLightRanks ranks) { return drtRankRadiance(ranks,drtRankWeights(ranks)); }

// Directional ownership follows each selected color through insertion. These
// are the same two energy moments as before, with constant-index fields.
struct DrtRankMoments { vec2 q0,q1,q2,q3,q4,q5,q6,q7; };
void drtClearRankMoments(out DrtRankMoments moments) {
    moments.q0=vec2(0.0);
    moments.q1=vec2(0.0);
    moments.q2=vec2(0.0);
    moments.q3=vec2(0.0);
    moments.q4=vec2(0.0);
    moments.q5=vec2(0.0);
    moments.q6=vec2(0.0);
    moments.q7=vec2(0.0);
}
void drtInsertFacingRank(inout DrtLightRanks ranks,inout DrtRankMoments moments,vec3 contribution,vec2 energy) {
    int rank=drtInsertRank(ranks,contribution);
    if(rank<0)return;
    if(rank<7)moments.q7=moments.q6;
    if(rank<6)moments.q6=moments.q5;
    if(rank<5)moments.q5=moments.q4;
    if(rank<4)moments.q4=moments.q3;
    if(rank<3)moments.q3=moments.q2;
    if(rank<2)moments.q2=moments.q1;
    if(rank<1)moments.q1=moments.q0;
    switch(rank) {
        case 0: moments.q0=energy; break;
        case 1: moments.q1=energy; break;
        case 2: moments.q2=energy; break;
        case 3: moments.q3=energy; break;
        case 4: moments.q4=energy; break;
        case 5: moments.q5=energy; break;
        case 6: moments.q6=energy; break;
        case 7: moments.q7=energy; break;
    }
}
vec2 drtRankMomentSum(DrtRankMoments moments,DrtRankWeights weights) {
    vec2 energy=vec2(0.0);
    energy+=moments.q0*weights.low.x;
    energy+=moments.q1*weights.low.y;
    energy+=moments.q2*weights.low.z;
    energy+=moments.q3*weights.low.w;
    energy+=moments.q4*weights.high.x;
    energy+=moments.q5*weights.high.y;
    energy+=moments.q6*weights.high.z;
    energy+=moments.q7*weights.high.w;
    return energy;
}
