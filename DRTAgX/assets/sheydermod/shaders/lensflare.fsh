// Caller keys are retained even though shape and tint are now computed in the vertex stage.
uniform float lf_Intensity = 0.0;
uniform float lf_Brightness = 0.3;
uniform float lf_StreakBrightness = 0.6;
uniform float lf_PointBrightness = 1.2;
uniform float lf_StreakLen = 0.3;
uniform vec3 lf_TintCenter = vec3(1.00, 0.70, 0.35);
uniform vec3 lf_TintMid = vec3(0.30, 0.85, 0.40);
uniform vec3 lf_TintEdge = vec3(0.20, 0.50, 0.95);
uniform float lf_ColorMid1 = 0.40;
uniform float lf_ColorMid2 = 0.75;
flat in vec3 lf_sunPosScreen;
flat in float lf_sunInFront;
flat in float lf_sunVisible;
flat in vec4 lf_shape;
flat in vec3 lf_streakTint;
flat in vec3 lf_pointTint;
flat in float lf_gain;

vec3 lf_additive(float celestialVisibility) {
    float gain = lf_gain * celestialVisibility;
    // Reject fully hidden/offscreen contributions before evaluating any Gaussian.
    if (gain == 0.0) return vec3(0.0);
    vec2 sun = clamp(lf_sunPosScreen.xy, vec2(-10.0), vec2(10.0));
    vec2 ndc = texCoord * 2.0 - 1.0;
    vec2 d = ndc - sun;
    float exponent = d.x * d.x * lf_shape.x + d.y * d.y * lf_shape.y;
    // RGB streak widths remain 1.15/1.20, 1.00/1.00 and 0.85/0.85.
    vec3 streak = exp(-vec3(d.x*d.x*lf_shape.x/(1.15*1.15) + d.y*d.y*lf_shape.y/(1.20*1.20),
        exponent, exponent/(0.85*0.85)));
    vec2 point = (ndc + sun) * vec2(lf_shape.w, 1.0);
    float blob = exp(-dot(point, point) * lf_shape.z);
    return (streak * lf_streakTint + blob * lf_pointTint) * gain;
}

// Keep the public include helper for known external final-shader integrations.
vec3 lf_apply(vec3 sceneColor) { return sceneColor + lf_additive(1.0); }
