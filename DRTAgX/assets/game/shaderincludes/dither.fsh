// Preserve the native include ABI for fallback and third-party callers.
// RGBA16F scene radiance and material alpha receive no quantization noise.
vec4 NoiseFromPixelPosition(ivec2 PixelsPosition, int ditherSeed, int horizontalResolution) {
    return vec4(0.0);
}
