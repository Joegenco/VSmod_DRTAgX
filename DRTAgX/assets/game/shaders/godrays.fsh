#version 330 core

uniform sampler2D inputTexture;
uniform sampler2D glowParts;

// Native/third-party god-ray callers may still set this key after DRTAgX
// replaces the effect. The engine records declared uniforms even at GL
// location -1; retaining this unused declaration prevents a dictionary crash.
uniform int maxGodRaySamples;

// Unreal Bloom Parameters
uniform float bloomThreshold = 0.0; // Min brightness before blooming starts
uniform float bloomSoftKnee  = 0.5; // Knee width for smooth transition curve
uniform float bloomIntensity = 0.1; // Overall bloom scale

in vec2 texCoord;
out vec4 outColor;

// Rec.709 Luminance
const vec3 LUM_VEC = vec3(0.2126, 0.7152, 0.0722);

vec3 applyUnrealThreshold(vec3 color) {
    float brightness = dot(color, LUM_VEC);
    
    // Soft Knee Curve Calculation (UE4 / ACES style)
    float knee = bloomThreshold * bloomSoftKnee;
    float soft = brightness - bloomThreshold + knee;
    soft = clamp(soft, 0.0, 2.0 * knee);
    soft = (soft * soft) / (4.0 * knee + 0.00001);

    // Calculate contribution factor above threshold
    float weight = max(soft, brightness - bloomThreshold) / max(brightness, 0.00001);

    return color * weight;
}

void main(void) {
    vec4 sceneColor = texture(inputTexture, texCoord);
    vec4 glowColor  = texture(glowParts, texCoord);

    // Extract bright areas using the UE4 threshold curve
    vec3 bloomExtracted = applyUnrealThreshold(sceneColor.rgb);

    // Add emissive/glow parts directly so they bloom cleanly regardless of threshold
    bloomExtracted += glowColor.rgb * glowColor.a;

    outColor = vec4(bloomExtracted * bloomIntensity, 1.0);
}
