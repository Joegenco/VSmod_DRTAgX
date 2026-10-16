#version 430 core

// Native model ABI, intentionally without joint/bone inputs: uploaded vertices
// are already the authored closed/rest mesh. Lid movement never changes depth.
layout(location = 0) in vec3 vertexPositionIn;
layout(location = 1) in vec2 uvIn;
#ifdef DRT_CONTAINER_MOVING
layout(std140, binding = 5) uniform DrtHeldProjections {
    mat4 drtHeldMatrices[2];
    vec4 drtHeldStatus[2];
};
uniform int heldProjectionIndex;
out vec3 drtMovingShadowOffset;
#endif
uniform mat4 casterMatrix;
out vec2 uv;

void main() {
    vec4 position = casterMatrix * vec4(vertexPositionIn, 1.0);
#ifdef DRT_CONTAINER_MOVING
    if (heldProjectionIndex >= 0) {
        int hand = heldProjectionIndex;
        position = drtHeldStatus[hand].x > 0.0
            ? drtHeldMatrices[hand] * position
            : vec4(2.0, 2.0, 2.0, 1.0);
    }
    // Undo only projection scaling, retaining a perspective-correct radial
    // vector for the fragment cutoff. Static PLS has no such varying or gate.
    vec2 side = position.xy / 0.94;
    if (heldProjectionIndex >= 0) {
        mat4 p = drtHeldMatrices[heldProjectionIndex];
        side = (position.xy + p[2].xy * position.w) / vec2(p[0][0], p[1][1]);
    }
    drtMovingShadowOffset = vec3(side, position.w);
#endif
    gl_Position = position;
    uv = uvIn;
}
