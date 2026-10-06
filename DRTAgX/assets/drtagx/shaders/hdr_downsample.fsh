#version 330 core
uniform sampler2D source;
uniform sampler2D glowParts;
uniform vec2 texelSize;
uniform int meterFirstLevel;
uniform int emissiveFirstLevel;
uniform int reduction = 2;
uniform int alignedHalf = 0;
in vec2 texCoord;
layout(location = 0) out vec4 sceneColor;
layout(location = 1) out vec2 meterData;
void main() {
    // Four bilinear taps cover the source footprint without a brightness threshold.
    // Four bilinear taps at +/-one source texel cover a true 4x4 quarter-resolution footprint.
    // An aligned 2:1 box is exactly one bilinear sample; odd dimensions keep the prior four taps.
    vec2 d = texelSize * (reduction == 4 ? 1.0 : 0.5);
    vec3 rgb = reduction == 2 && alignedHalf != 0 ? texture(source, texCoord).rgb : (texture(source, texCoord + vec2(-d.x, -d.y)).rgb
              + texture(source, texCoord + vec2( d.x, -d.y)).rgb
              + texture(source, texCoord + vec2(-d.x,  d.y)).rgb
              + texture(source, texCoord + vec2( d.x,  d.y)).rgb) * 0.25;
    if (emissiveFirstLevel != 0) {
        // Primary color 1 carries the same glow metadata used by final.fsh.
        // Apply its gain only once, before the bloom pyramid blurs this RGB.
        vec4 Emissives = texture(glowParts, texCoord);
        rgb += rgb * Emissives.r * 3.0;
        // G can carry signed native fog/deferred metadata. Only positive
        // authored extraction contributes; a fractional power of negatives is undefined.
        rgb += rgb * pow(max(Emissives.g, 0.0), 1.0) * vec3(1.2, 1.0, 0.85);
    }
    sceneColor = vec4(max(rgb, vec3(0.0)), 1.0);
    if (meterFirstLevel != 0) {
        vec2 centered = (texCoord - 0.5) * 7.0;
        float weight = exp(-0.5 * dot(centered, centered));
        float luminance = dot(sceneColor.rgb, vec3(0.2126, 0.7152, 0.0722));
        // Both channels average through the mip chain to retain the weighted linear mean.
        meterData = vec2(weight * luminance, weight);
    } else {
        meterData = vec2(0.0);
    }
}
