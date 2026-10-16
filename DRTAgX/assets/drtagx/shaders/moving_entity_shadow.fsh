#version 430 core

uniform sampler2D entityTex;
in vec2 uv;
#ifdef DRT_MOVING_SHADOWS
in vec3 drtMovingShadowOffset;
#endif

void main() {
#ifdef DRT_MOVING_SHADOWS
    // Shared rest-container fragments use this only for moving-light maps;
    // their static PLS program retains its original range and coverage.
    if (dot(drtMovingShadowOffset, drtMovingShadowOffset) > 32.0 * 32.0) discard;
#endif
    // Match native animated shadows: preserve holes in creature/player skins.
    // This depth-only program does not contribute emission or scene color.
    if (texture(entityTex, uv).a < 0.01) discard;
}
