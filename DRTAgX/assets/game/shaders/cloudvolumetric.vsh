#version 330 core
#extension GL_ARB_explicit_attrib_location: enable

layout(location = 0) in vec2 p;

out vec2 uv;
out vec2 ndc;

void main(){
    gl_Position = vec4(p, 0.0, 1.0);
    uv = p * 0.5 + 0.5;
    ndc = p;
}