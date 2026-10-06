#version 330 core

uniform vec2 frameSize;
uniform int isVertical;

out vec2 vTexCoord;
out vec2 vBlurStep;

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
}