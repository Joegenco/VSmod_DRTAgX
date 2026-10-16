#version 430 core

// Native entity ABI: one joint index per vertex, with the current animation
// palette in std140 binding 0. The native prefix supplies the skeleton limit.
layout(location = 0) in vec3 vertexPositionIn;
layout(location = 1) in vec2 uvIn;
layout(location = 5) in int jointId;
layout(std140, binding = 0) uniform Animation {
    mat4 values[MAXANIMATEDELEMENTS];
} ElementTransforms;

// Both caster types share the exact fitted hand projection used by receivers.
layout(std140, binding = 5) uniform DrtHeldProjections {
    mat4 drtHeldMatrices[2];
    vec4 drtHeldStatus[2];
};
uniform mat4 casterMatrix;
uniform int heldProjectionIndex;
out vec2 uv;
out vec3 drtMovingShadowOffset;

void main() {
    vec4 position = casterMatrix * ElementTransforms.values[jointId]
        * vec4(vertexPositionIn, 1.0);
    if (heldProjectionIndex >= 0) {
        int hand = heldProjectionIndex;
        position = drtHeldStatus[hand].x > 0.0
            ? drtHeldMatrices[hand] * position
            : vec4(2.0, 2.0, 2.0, 1.0);
    }
    // Cube orientation preserves vector length. For fitted hands undo the
    // asymmetric projection too, keeping the 32-block cutoff radial.
    vec2 side = position.xy / 0.94;
    if (heldProjectionIndex >= 0) {
        mat4 p = drtHeldMatrices[heldProjectionIndex];
        side = (position.xy + p[2].xy * position.w) / vec2(p[0][0], p[1][1]);
    }
    drtMovingShadowOffset = vec3(side, position.w);
    gl_Position = position;
    uv = uvIn;
}
