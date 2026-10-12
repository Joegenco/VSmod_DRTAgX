// Sheyder volumetric fog: reject incompatible receivers BEFORE reconstructing
// the half-resolution scatter. Mip/bilinear reads mix sky into terrain before
// a depth test can remove it, so this pass reads individual level-zero texels.
#include drtagx_fog_transport.ash

uniform sampler2D vfScatterTex;
uniform sampler2D vfDepthTex;
// The existing atmosphere binder supplies the native liquid depth and validity.
uniform sampler2D liquidDepth;
uniform float vf_Enabled = 0.0;
uniform float vf_BlurRadius = 5.0;

float vf_receiverDepth(vec2 uv) {
    ivec2 size = textureSize(vfDepthTex, 0);
    ivec2 pixel = clamp(ivec2(floor(uv * vec2(size))), ivec2(0), size - 1);
    float depth = texelFetch(vfDepthTex, pixel, 0).r;
    if (drtAtmosphereScreen.w > 0.5) {
        ivec2 liquidSize = textureSize(liquidDepth, 0);
        ivec2 liquidPixel = clamp(ivec2(floor(uv * vec2(liquidSize))), ivec2(0), liquidSize - 1);
        depth = min(depth, texelFetch(liquidDepth, liquidPixel, 0).r);
    }
    return depth;
}

float vf_linearDepth(float depth) {
    // Positive camera-axis distance from the current projection. Hardware depth
    // approaches one with distance and cannot distinguish distant walls/sky.
    float clipZ = depth * 2.0 - 1.0;
    float z = drtAtmosphereInvProjection[2][2] * clipZ + drtAtmosphereInvProjection[3][2];
    float w = drtAtmosphereInvProjection[2][3] * clipZ + drtAtmosphereInvProjection[3][3];
    return abs(z) / max(abs(w), 1e-8);
}

void vf_tap(inout vec3 acc, inout float totalWeight, ivec2 pixel,
            ivec2 size, float centerDepth, bool centerSky, float spatialWeight) {
    if (spatialWeight <= 0.0) return;
    pixel = clamp(pixel, ivec2(0), size - 1);
    vec4 scatter = texelFetch(vfScatterTex, pixel, 0);
    if (scatter.a <= 0.0) return;

    // Exactly the UV of this scatter invocation, including odd framebuffer
    // sizes. Scatter and composite both select floor(uv * depthTextureSize).
    vec2 sampleUv = (vec2(pixel) + 0.5) / vec2(size);
    float depth = vf_receiverDepth(sampleUv);
    bool sampleSky = depth >= 1.0;
    if (sampleSky != centerSky) return;

    float weight = spatialWeight;
    if (!centerSky) {
        float sampleDepth = vf_linearDepth(depth);
        float relativeDifference = abs(sampleDepth - centerDepth) /
            max(min(sampleDepth, centerDepth), 0.1);
        // Broad tolerance keeps sloped terrain smooth; reject distinct depth
        // layers completely rather than retaining an exponential leakage tail.
        weight *= 1.0 - smoothstep(0.05, 0.20, relativeDifference);
    }
    acc += scatter.rgb * weight;
    totalWeight += scatter.a * weight;
}

vec3 vf_compositeVolumetric(vec3 sceneColor, vec2 uv) {
    if (vf_Enabled < 0.5) return sceneColor;

    float depth = vf_receiverDepth(uv);
    bool centerSky = depth >= 1.0;
    float centerDepth = centerSky ? 0.0 : vf_linearDepth(depth);
    ivec2 size = textureSize(vfScatterTex, 0);
    vec2 position = uv * vec2(size) - 0.5;
    ivec2 base = ivec2(floor(position));
    vec3 acc = vec3(0.0);
    float totalWeight = 0.0;

    if (vf_BlurRadius <= 0.0) {
        // Depth-aware bilinear upscale: four separate texels, rejected before
        // interpolation. Smoothing zero must still protect terrain silhouettes.
        vec2 f = fract(position);
        vf_tap(acc, totalWeight, base, size, centerDepth, centerSky, (1.0 - f.x) * (1.0 - f.y));
        vf_tap(acc, totalWeight, base + ivec2(1, 0), size, centerDepth, centerSky, f.x * (1.0 - f.y));
        vf_tap(acc, totalWeight, base + ivec2(0, 1), size, centerDepth, centerSky, (1.0 - f.x) * f.y);
        vf_tap(acc, totalWeight, base + ivec2(1, 1), size, centerDepth, centerSky, f.x * f.y);
    } else {
        // Continuous compact tent: support grows from one to two half-res
        // texels. Cap smoothing above two to keep at most sixteen exact taps;
        // broad mip footprints cannot preserve occlusion without more resources.
        float radius = 1.0 + 0.5 * min(vf_BlurRadius, 2.0);
        for (int y = -1; y <= 2; ++y) {
            for (int x = -1; x <= 2; ++x) {
                ivec2 pixel = base + ivec2(x, y);
                vec2 tent = max(vec2(radius) - abs(vec2(pixel) - position), vec2(0.0));
                vf_tap(acc, totalWeight, pixel, size, centerDepth, centerSky, tent.x * tent.y);
            }
        }
    }

    // A thin foreground feature may have no corresponding half-res ray. Do not
    // fall back to a background ray and paint its fog over that occluder.
    if (totalWeight <= 1e-6) return sceneColor;
    return sceneColor + acc / totalWeight;
}
