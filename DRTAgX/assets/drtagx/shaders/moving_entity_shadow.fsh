#version 430 core

uniform sampler2D entityTex;
in vec2 uv;

void main() {
    // Match native animated shadows: preserve holes in creature/player skins.
    // This depth-only program does not contribute emission or scene color.
    if (texture(entityTex, uv).a < 0.01) discard;
}
