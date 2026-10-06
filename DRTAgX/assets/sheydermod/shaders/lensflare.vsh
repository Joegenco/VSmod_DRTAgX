// Final-triangle vertex work: keep Sheyder caller uniforms and celestial/cloud occlusion.
uniform sampler2D primaryScene;
uniform float lf_SunOcclusion = 1.0;
uniform float lf_CloudFade = 1.0;
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
flat out vec3 lf_sunPosScreen;
flat out float lf_sunInFront;
flat out float lf_sunVisible;
flat out vec4 lf_shape; // inverse squared horizontal/vertical width, point radius and aspect.
flat out vec3 lf_streakTint;
flat out vec3 lf_pointTint;
flat out float lf_gain;

void lf_forward(vec3 sunPosScreenIn, vec3 sunPos3dIn, vec3 playerViewVector) {
    lf_sunPosScreen = sunPosScreenIn;
    lf_sunInFront = step(0.0, dot(sunPos3dIn, playerViewVector));
    vec2 sunUV = clamp(sunPosScreenIn.xy * 0.5 + 0.5, vec2(0.0), vec2(1.0));
    float luma = dot(textureLod(primaryScene, sunUV, 0.0).rgb, vec3(0.2126, 0.7152, 0.0722));
    lf_sunVisible = smoothstep(0.6, 1.0, luma) * smoothstep(0.02, 0.3, lf_SunOcclusion) * lf_CloudFade;
    vec2 sun = clamp(sunPosScreenIn.xy, vec2(-10.0), vec2(10.0));
    float distance = length(sun);
    float edge = clamp(distance / 1.4142, 0.0, 1.0);
    float horizontal = mix(1.50, 0.55, edge) * max(0.05, lf_StreakLen);
    float vertical = mix(0.011, 0.038, edge);
    float radius = mix(0.10, 0.45, edge);
    // Uniform-only tint/shape calculations used to repeat at every final pixel.
    lf_shape = vec4(1.0 / (horizontal * horizontal), 1.0 / (vertical * vertical),
        1.0 / (radius * radius), invFrameSize.y > 0.0 ? invFrameSize.y / max(invFrameSize.x, 1e-6) : 1.0);
    float m1 = clamp(lf_ColorMid1, 0.0, 1.0);
    float m2 = clamp(max(lf_ColorMid2, m1 + 0.001), 0.0, 1.0);
    vec3 tint = mix(lf_TintCenter, lf_TintMid, smoothstep(0.0, m1, edge));
    lf_pointTint = mix(tint, lf_TintEdge, smoothstep(m1, m2, edge)) * max(0.0, lf_PointBrightness);
    lf_streakTint = lf_TintCenter * max(0.0, lf_StreakBrightness);
    // Preserve the original threshold on visibility before applying spatial/brightness gains.
    float gate = lf_Intensity * lf_sunInFront * lf_sunVisible;
    lf_gain = gate <= 0.001 ? 0.0 : gate * (1.0 - smoothstep(1.2, 1.6, distance)) * (1.0 - edge) * lf_Brightness;
}
