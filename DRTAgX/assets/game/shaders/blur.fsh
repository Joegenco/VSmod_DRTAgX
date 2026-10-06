#version 330 core

uniform sampler2D inputTexture;

in vec2 vTexCoord;
in vec2 vBlurStep;

out vec4 outColor;

// Helper function: Fetches the texture, deducts 0.03, and clamps to 0.
// This ensures only the brightest pixels contribute to the bloom.
vec3 getThresholdedColor(vec2 uv) {
    vec3 color = texture(inputTexture, uv).rgb;
    return max(color - 0.03, 0.0);
}

void main(void)
{
    // Standard 11-tap Gaussian weights (1 center tap + 5 neighbors on each side)
    float weights[6] = float[](0.227027, 0.1945946, 0.1216216, 0.054054, 0.016216, 0.002271);
    
    // Initialize the center tap for all three scales using the thresholded color
    vec3 centerColor = getThresholdedColor(vTexCoord) * weights[0];
    vec3 scale1 = centerColor;
    vec3 scale2 = centerColor;
    vec3 scale3 = centerColor;
    
    for (int i = 1; i < 6; i++) {
        // Scale 1: Tight blur (Standard 1x pixel step)
        vec2 offset1 = vBlurStep * (float(i) * 0.5);
        scale1 += getThresholdedColor(vTexCoord + offset1) * weights[i];
        scale1 += getThresholdedColor(vTexCoord - offset1) * weights[i];
        
        // Scale 2: Medium blur (Spread out by 5.0x)
        vec2 offset2 = vBlurStep * (float(i) * 4.0);
        scale2 += getThresholdedColor(vTexCoord + offset2) * weights[i];
        scale2 += getThresholdedColor(vTexCoord - offset2) * weights[i];
        
        // Scale 3: Wide bloom tail (Spread out by 10.0x)
        vec2 offset3 = vBlurStep * (float(i) * 16.0);
        scale3 += getThresholdedColor(vTexCoord + offset3) * weights[i];
        scale3 += getThresholdedColor(vTexCoord - offset3) * weights[i];
    }
    
    // Combine the three scales to simulate a multi-pass bloom.
    // Note: Converted integers (2, 3) to floats (2.0, 3.0) to prevent GLSL type-casting errors.
    vec3 finalBloom = ((scale2 * 2.0) - (scale1) + (scale3)) * 0.5;
    
    // Optional: Boost the overall brightness if you want a more intense glowing effect.
    // finalBloom *= 1.2;
    
    outColor = vec4(finalBloom, 1.0);
}