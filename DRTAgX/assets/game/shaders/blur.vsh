#version 330 core

uniform vec2 frameSize;
uniform int isVertical;

out vec2 vTexCoord;
out vec2 vBlurStep;
// Retain the native 21-element interface for shader consumers that still use
// the old blur taps. The maintained fragment uses vTexCoord/vBlurStep instead.
out vec2 texCoords[21];

void main(void)
{
    // Generating a full screen triangle based on gl_VertexID (Great technique!)
    float x = -1.0 + float((gl_VertexID & 1) << 2);
    float y = -1.0 + float((gl_VertexID & 2) << 1);
    gl_Position = vec4(x, y, 0.0, 1.0);
    
    vTexCoord = vec2((x + 1.0) * 0.5, (y + 1.0) * 0.5);
    
    // Calculate the size/direction of a single pixel step
    if (isVertical == 1) {
        vBlurStep = vec2(0.0, 1.0 / frameSize.y);
    } else {
        vBlurStep = vec2(1.0 / frameSize.x, 0.0);
    }
    // Native taps 0..15 are centered at index 8; initialize the complete array
    // so consumers never receive undefined trailing coordinates.
    for (int i = 0; i < 21; ++i)
        texCoords[i] = vTexCoord + float(i - 8) * vBlurStep;
}
