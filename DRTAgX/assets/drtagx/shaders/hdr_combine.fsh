#version 330 core
uniform sampler2D level0;
uniform sampler2D level1;
uniform sampler2D level2;
uniform sampler2D level3;
uniform sampler2D level4;
uniform int performanceMode = 0;
in vec2 texCoord;
out vec4 outColor;
void main() {
    // Each level contributes a distinct angular bloom radius. The authored
    // weights sum to 0.95; preserve that gain when maintaining the pyramid.
    if (performanceMode != 0) {
        // Quarter through 1/32 retain total gain .95 and the original angular blur radii.
        outColor = vec4(texture(level0, texCoord).rgb * .35 + texture(level1, texCoord).rgb * .30
            + texture(level2, texCoord).rgb * .20 + texture(level3, texCoord).rgb * .10, 1.0);
        return;
    }
    vec3 bloom = texture(level0, texCoord).rgb * 0.05
               + texture(level1, texCoord).rgb * 0.30
               + texture(level2, texCoord).rgb * 0.30
               + texture(level3, texCoord).rgb * 0.20
               + texture(level4, texCoord).rgb * 0.10;
    outColor = vec4(bloom, 1.0);
}
