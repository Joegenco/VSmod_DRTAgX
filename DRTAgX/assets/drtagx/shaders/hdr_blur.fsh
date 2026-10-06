#version 330 core
uniform sampler2D source;
uniform vec2 blurStep;
uniform int radiusOne = 0;
in vec2 texCoord;
out vec4 outColor;
void main() {
    // At exactly one texel, +/-1.2 bilinear samples combine the .25/.0625 neighbors.
    // Larger radii retain five taps: their footprints cannot use this identity.
    vec3 color = texture(source, texCoord).rgb * 0.375;
    if (radiusOne != 0) {
        color += (texture(source, texCoord + 1.2 * blurStep).rgb
               + texture(source, texCoord - 1.2 * blurStep).rgb) * 0.3125;
        outColor = vec4(color, 1.0);
        return;
    }
    color += (texture(source, texCoord + blurStep).rgb
           + texture(source, texCoord - blurStep).rgb) * 0.25;
    color += (texture(source, texCoord + 2.0 * blurStep).rgb
           + texture(source, texCoord - 2.0 * blurStep).rgb) * 0.0625;
    outColor = vec4(color, 1.0);
}
