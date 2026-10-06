#version 330 core
uniform sampler2D aoLow;
uniform sampler2D gPosition;
in vec2 texCoord;
layout(location=0) out vec4 outColor;
void main() {
    vec3 receiver = texture(gPosition, texCoord).xyz;
    // Native sky/celestial zero sentinels always preserve unit visibility.
    if (all(equal(receiver, vec3(0.0)))) { outColor = vec4(1.0); return; }
    vec2 size = vec2(textureSize(aoLow, 0));
    vec2 pixel = texCoord * size - 0.5, base = floor(pixel), fraction = fract(pixel);
    float visibility = 0.0, total = 0.0;
    for (int y=0; y<2; ++y) for (int x=0; x<2; ++x) {
        vec2 uv = (base + vec2(x,y) + 0.5) / size;
        vec3 guide = texture(gPosition, uv).xyz;
        if (all(equal(guide, vec3(0.0)))) continue;
        // Scale view-depth rejection with distance while retaining near silhouettes.
        float depthWeight = exp(-abs(guide.z-receiver.z)*8.0/(1.0+.02*abs(receiver.z)));
        float weight = depthWeight * (x==0 ? 1.0-fraction.x : fraction.x) * (y==0 ? 1.0-fraction.y : fraction.y);
        visibility += texture(aoLow, uv).r * weight; total += weight;
    }
    float ao = total > 1e-5 ? clamp(visibility/total, 0.0, 1.0) : 1.0;
    outColor = vec4(ao); // Native and deferred consumers continue sampling their original R target.
}
