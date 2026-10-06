#version 330 core

#include drtagx_atmosphere_sampling.fsh
#include drtagx_volumetric_radiance.fsh

// SheyderMod: Volumetric fog - half-resolution scatter pass.
// Reconstructs each fragment's world position from scene depth and ray-marches from the
// camera toward it through the sun's shadow map, accumulating the sunlit fraction to
// form light shafts. The march is clamped to the shadow range and to the water surface,
// dithered to hide step banding, tinted warm toward the sun / cool away from it, and
// scaled by a soft near onset and a distance-density term. Writes the finished additive
// god-ray colour; vfscatter_composite.fsh upscales and adds it.

uniform sampler2D       depthTexture;
uniform sampler2DShadow shadowMapFar;

uniform mat4  vf_InvViewProj;
uniform mat4  vf_ToShadowSpaceFar;
uniform vec3  vf_ShadowRayStart;
uniform vec3  vf_CameraWorldPos;
uniform vec3  vf_SunDir;
uniform vec3  vf_FrontColor = vec3(1.0, 0.85, 0.6);
uniform vec3  vf_BackColor  = vec3(0.4, 0.55, 0.9);
uniform float vf_Intensity   = 0.0;
uniform float vf_FogStart    = 8.0;
uniform float vf_FogEnd      = 18.0;
uniform int   vf_StepCount   = 12;
uniform float vf_InvSteps    = 0.0833;
uniform float vf_MaxRange    = 128.0;
uniform float vf_RenderRange = 512.0;
uniform float vf_InvRenderRange = 0.001953125;
uniform float vf_DistantDensity = 1.0;
uniform float vf_ShadowBias  = 0.0009;

uniform sampler2D vf_LiquidDepth;
uniform float vf_HasWaterDepth = 0.0;

in  vec2 texcoord;
layout(location = 0) out vec4 outScatter;

float bayer2(vec2 a)  { a = floor(a); return fract(a.x * 0.5 + a.y * a.y * 0.75); }
float bayer4(vec2 a)  { return bayer2(0.5 * a) * 0.25 + bayer2(a); }

vec3 reconstructWorldPos(vec2 uv, float depth) {
    vec4 clip  = vec4(uv * 2.0 - 1.0, depth * 2.0 - 1.0, 1.0);
    vec4 world = vf_InvViewProj * clip;
    return world.xyz / world.w;
}

// Atmosphere-only color handoff; native step placement and shadow visibility remain intact.
vec3 drtVolumetricSunRadiance(vec3 rayDir) {
    // The fog helper extends the solar phase below the horizon without repeating
    // the sky LUT's horizon halo across terrain.
    if (drtAtmosphereFlags.x > 0.5) return drtVolumetricSkyRadiance(rayDir);
    float sunAlign = dot(rayDir, vf_SunDir);
    float ringT = sunAlign * 0.5 + 0.5;
    float colorMix = ringT * ringT * ringT;
    return mix(vf_BackColor, vf_FrontColor * 1.5, colorMix);
}

void main(void)
{

    float depth = texture(depthTexture, texcoord).r;

    float effDepth = depth;
    if (vf_HasWaterDepth > 0.5)
        effDepth = min(depth, texture(vf_LiquidDepth, texcoord).r);

    vec3  fragWorld = reconstructWorldPos(texcoord, effDepth);
    vec3  ray       = fragWorld - vf_CameraWorldPos;
    float dist      = length(ray);
    // Sky and OIT clouds have no opaque receiver inside the terrain range.
    // Keep their ray direction and march the same bounded shadow segment;
    // the final pass adds these shafts over the already merged sky/cloud scene.
    if (dist < 1e-4) { outScatter = vec4(0.0); return; }
    vec3  rayDir = ray / dist;

    float marchDist = min(dist, vf_MaxRange);

    if (marchDist <= vf_FogStart) { outScatter = vec4(0.0, 0.0, 0.0, 1.0); return; }

    int steps = vf_StepCount;

    // Native spatial Bayer offsets decorrelate neighboring march rays. They
    // alter sample positions only; no display-color noise or temporal jitter.
    float dither = bayer4(gl_FragCoord.xy);

    vec3 shadowDir = mat3(vf_ToShadowSpaceFar) * rayDir;
    vec3 dStep     = (marchDist * vf_InvSteps) * shadowDir;
    vec3 p         = vf_ShadowRayStart + dStep * dither;

    p.z -= vf_ShadowBias;

    float litAccum = 0.0;
    for (int i = 0; i < steps; i++) {

        bool inside = all(lessThanEqual(abs(p - vec3(0.5)), vec3(0.5)));
        litAccum += texture(shadowMapFar, p) * float(inside);
        p += dStep;
    }

    float startFade = smoothstep(vf_FogStart, vf_FogEnd, marchDist);

    float normalOut = litAccum * vf_InvSteps;

    vec3 useColor = drtVolumetricSunRadiance(rayDir);

    float phase    = 3.0;

    // Far-plane/LOD receivers must not amplify density beyond the configured
    // terrain envelope. Rays within that range retain the native density.
    float distNorm = min(dist, vf_RenderRange) * vf_InvRenderRange;
    float distGain = 1.0 + (vf_DistantDensity - 1.0) * distNorm;

    float scatter = min(0.9, sqrt(phase * normalOut)) * startFade * vf_Intensity * distGain;

    outScatter = vec4(useColor * scatter, 1.0);
}
