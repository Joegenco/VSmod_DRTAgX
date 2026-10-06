#version 330 core

// SheyderMod: Water SSR - composite.
// Blurs the clean reflection, then samples it at a wave-distorted UV (world-anchored
// noise matching vanilla water) to lay crisp ripples over the soft image, and blends
// the result onto the water weighted by Fresnel, reflection strength and validity,
// faded out by the engine fog at the surface. Non-water pixels are discarded.

uniform sampler2D ssrTex;
uniform sampler2D liquidDepth;
uniform sampler2D sceneDepth;

uniform mat4  ssr_invProj;
uniform mat4  ssr_invModelView;
uniform vec3  ssr_playerPos;
uniform float ssr_waveCounter;
uniform float ssr_windWaveCounter;
uniform float ssr_flowCounter;
uniform float ssr_distort;
uniform float ssr_strength;
uniform float ssr_grain;
uniform float ssr_debug;
uniform float ssr_fogDensity;
uniform float ssr_fogMin;
uniform float ssr_flatFogDensity;
uniform float ssr_flatFogStart;

in  vec2 texcoord;
layout(location = 0) out vec4 outColor;
// Primary is MRT: unwritten auxiliary outputs are undefined, not preserved.
// Zero RGBA contributes nothing under Sheyder's SrcAlpha/OneMinusSrcAlpha
// blend, keeping existing emission and SSAO metadata while adding reflections.
layout(location = 1) out vec4 outGlow;
layout(location = 2) out vec4 outGNormal;
layout(location = 3) out vec4 outGPosition;

#include noise3d.ash
// SheyderMod: SSR reflection break-up. rp_a/rp_b/rp_count here carry the SSR ring set that
// WaterRippleRenderer packs for this pass (every in-water source incl. fish) -- whatever swims there
// disturbs the reflection so it stays visible, and it rides the same Fresnel fade as the reflection.
#include ripples.fsh

// Break-up tuning (starting values; per-source strength comes from the ripple intensities on the CPU):
const float RP_SSR_BREAK   = 1.0;    // ripple envelope -> how strongly the reflection is dimmed
const float RP_SSR_MAXCUT  = 0.85;   // max fraction of the reflection removed
const float RP_SSR_DISTORT = 0.02;   // ripple gradient -> reflection UV wobble
const float RP_SSR_FRESMIN = 0.02;   // skip the ripple where the reflection (Fresnel) is negligible

vec3 nvec3(vec4 p){ return p.xyz / p.w; }
vec4 nvec4(vec3 p){ return vec4(p, 1.0); }

void main(void){
    outGlow = vec4(0.0);
    outGNormal = vec4(0.0);
    outGPosition = vec4(0.0);
    if (ssr_debug > 0.5){ outColor = vec4(texture(ssrTex, texcoord).rgb, 1.0); return; }

    float wd = texture(liquidDepth, texcoord).r;
    if (wd >= 0.99999) discard;
    float sd = texture(sceneDepth, texcoord).r;
    if (wd >= sd) discard;

    vec3  viewPos  = nvec3(ssr_invProj * nvec4(vec3(texcoord, wd) * 2.0 - 1.0));
    float viewDist = length(viewPos);
    vec3  worldPos = (ssr_invModelView * vec4(viewPos, 1.0)).xyz;

    float clampedDepth     = min(250.0, viewDist);
    float heightDiff       = worldPos.y - ssr_flatFogStart;
    float extraDistanceFog = max(-ssr_flatFogDensity * clampedDepth * ssr_flatFogStart / 60.0, 0.0);
    float distanceFog      = 1.0 - exp(-(clampedDepth * ssr_fogDensity + extraDistanceFog));
    float flatFog          = 1.0 - exp(-heightDiff * ssr_flatFogDensity);
    float fogAmount        = max(flatFog, distanceFog);
    float nearnessToPlayer = clamp((8.0 - viewDist) / 8.0, 0.0, 0.9);
    fogAmount    = max(min(0.04, fogAmount), fogAmount - nearnessToPlayer);
    float fogVis = 1.0 - clamp(fogAmount + ssr_fogMin, 0.0, 1.0);
    if (fogVis <= 0.002) discard;

    vec3  worldViewDir = mat3(ssr_invModelView) * (viewPos / viewDist);
    vec2  wp           = worldPos.xz + ssr_playerPos.xz;

    float fineW   = 1.0 - smoothstep(32.0, 80.0, viewDist);
    float coarseW = (1.0 - fineW) * (1.0 - smoothstep(192.0, 256.0, viewDist));
    float tz      = ssr_waveCounter / 8.0 + ssr_windWaveCounter / 6.0;
    float e       = 0.6;
    vec2  grad    = vec2(0.0);
    if (fineW > 0.0){
        float hC = gnoise(vec3(wp,                tz));
        float hX = gnoise(vec3(wp + vec2(e, 0.0), tz));
        float hZ = gnoise(vec3(wp + vec2(0.0, e), tz));
        grad = vec2(hX - hC, hZ - hC) * fineW;
    }
    if (coarseW > 0.0){
        vec2  cp = wp * 0.125;
        float ct = tz * 0.4;
        float hC = gnoise(vec3(cp,                ct));
        float hX = gnoise(vec3(cp + vec2(e, 0.0), ct));
        float hZ = gnoise(vec3(cp + vec2(0.0, e), ct));
        grad += vec2(hX - hC, hZ - hC) * coarseW;
    }

    // Fresnel: the reflection only appears at grazing angles. Computed here (before the reflection
    // sample) so the SSR break-up can ride the SAME fade -- the ripple emerges with the reflection.
    float ndv  = clamp(-worldViewDir.y, 0.0, 1.0);
    float f1   = 1.0 - ndv;
    float f2   = f1 * f1;
    float fres = f2 * f2 * f1;

    // SSR reflection break-up: every in-water source (creatures, boats, rafts, surface fish) uploaded
    // into rp_* dims + wobbles the reflection above it so it stays visible where the mirror is strong.
    // Gated by Fresnel: no reflection (looking down) -> no ripple, which is also the common perf skip
    // (fres is view-coherent). rp_count==0 -> rippleSsr early-outs.
    vec3  rpLocal  = worldPos + ssr_playerPos;
    float rippleCut = 1.0;
    vec2  rippleUV  = vec2(0.0);
    if (fres > RP_SSR_FRESMIN){
        vec3 rp = rippleSsr(rpLocal);
        rippleCut = 1.0 - clamp(rp.x * RP_SSR_BREAK, 0.0, RP_SSR_MAXCUT);
        rippleUV  = rp.yz * (RP_SSR_DISTORT * fres);   // wobble fades in with the reflection
    }

    vec4 refl = texture(ssrTex, texcoord + grad * (ssr_distort * 0.06) + rippleUV);

    if (ssr_grain > 0.0 && fineW > 0.0){
        float ga = rpLocal.x + rpLocal.y, gb = rpLocal.z;
        float noise1 = gnoise(vec3(ga * 15.0, -abs(gb * 15.0), ssr_flowCounter))
                     + gnoise(vec3(ga *  5.0,      gb *  5.0,   ssr_flowCounter));
        refl.rgb *= max(0.0, 1.0 + noise1 * (ssr_grain * fineW));
    }

    vec2  vh       = worldViewDir.xz;
    float vhl      = length(vh);
    float facing   = vhl > 1e-4 ? dot(grad, vh / vhl) : 0.0;
    float waveMask = 1.0 - clamp(facing * 0.4, 0.0, 1.0);

    // rippleCut multiplies the (Fresnel-weighted) reflection alpha, so its effect is proportional to
    // the reflection -- it breaks up exactly where/when the reflection is actually visible.
    outColor = vec4(refl.rgb, clamp(fres * ssr_strength * waveMask * refl.a, 0.0, 1.0) * fogVis * rippleCut);
}
