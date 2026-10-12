#version 330 core
uniform sampler2D source;
uniform sampler2D currentLevel;
uniform vec2 texelSize;
uniform float levelWeight;
uniform float lowerWeight;
in vec2 texCoord;
out vec4 outColor;
void main() {
    // The separable 3x3 tent has weights [1,2,1]x[1,2,1]/16.
    // Coarse contributions pass through it at every reconstruction scale,
    // producing smooth broad halos without separate horizontal/vertical draws.
    // A 2.5-texel coarse radius retains a broad halo rather than concentrating
    // all the lower-scale energy close to the unblurred source silhouette.
    vec2 stepSize = texelSize * 2.5;
    vec3 lower = texture(source, texCoord).rgb * 4.0;
    lower += (texture(source, texCoord + vec2(-1.0, 0.0) * stepSize).rgb
            + texture(source, texCoord + vec2( 1.0, 0.0) * stepSize).rgb
            + texture(source, texCoord + vec2(0.0, -1.0) * stepSize).rgb
            + texture(source, texCoord + vec2(0.0,  1.0) * stepSize).rgb) * 2.0;
    lower += texture(source, texCoord + vec2(-1.0, -1.0) * stepSize).rgb
           + texture(source, texCoord + vec2( 1.0, -1.0) * stepSize).rgb
           + texture(source, texCoord + vec2(-1.0,  1.0) * stepSize).rgb
           + texture(source, texCoord + vec2( 1.0,  1.0) * stepSize).rgb;
    // The same-size source is pixel-aligned. Four half-texel bilinear fetches
    // implement [1,2,1]x[1,2,1]/16 exactly, smoothing the finest core in this draw.
    vec2 halfTexel = 0.5 / vec2(textureSize(currentLevel, 0));
    vec3 current = (texture(currentLevel, texCoord + vec2(-halfTexel.x, -halfTexel.y)).rgb
                  + texture(currentLevel, texCoord + vec2( halfTexel.x, -halfTexel.y)).rgb
                  + texture(currentLevel, texCoord + vec2(-halfTexel.x,  halfTexel.y)).rgb
                  + texture(currentLevel, texCoord + vec2( halfTexel.x,  halfTexel.y)).rgb) * 0.25;
    // Weight each source scale exactly once; an already weighted accumulation
    // uses lowerWeight=1, preserving the original .95 constant-scene gain.
    outColor = vec4(current * levelWeight
                  + lower * (lowerWeight / 16.0), 1.0);
}
