#version 330 core
uniform sampler2D meter;
uniform sampler2D previousEv;
uniform float meterLod;
uniform float deltaTime;
uniform float adaptationSeconds;
uniform vec2 evBounds;
uniform int resetExposure;
in vec2 texCoord;
out vec4 outColor;
void main() {
    vec2 statistic = textureLod(meter, vec2(0.5), meterLod).rg;
    float meanLuminance = statistic.x / max(statistic.y, 0.00001);
    // Convert the center-weighted linear mean to EV only after spatial averaging.
    // The authored luminance target is 0.35; this is not an albedo gamma curve.
    float target = clamp(log2(0.35 / max(meanLuminance, 0.00001)), evBounds.x, evBounds.y);
    // Clamp a persisted previous EV immediately when the configured ceiling is lowered.
    float previous = clamp(texture(previousEv, vec2(0.5)).r, evBounds.x, evBounds.y);
    float blend = resetExposure != 0 ? 1.0 : 1.0 - exp(-max(deltaTime, 0.0) / max(adaptationSeconds, 0.1));
    outColor = vec4(mix(previous, target, blend), 0.0, 0.0, 1.0);
}
