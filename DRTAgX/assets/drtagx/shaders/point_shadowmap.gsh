#version 330 core

// This stage is compiled only into owned placed/moving point-shadow programs.
// Reject a complete triangle, including VBO triangles with mixed roots/tips;
// the packed vertex route has already classified the complete four-corner quad.
layout(triangles) in;
layout(triangle_strip, max_vertices = 3) out;
uniform int drtExcludeWindCasters = 1;
uniform int drtExcludeEmitter;
uniform int drtShadowTerrainPass = -1;
in vec2 drtPointUv[];
in vec3 drtPointLocalPos[];
flat in int drtPointWindMode[];
flat in int drtPointWindCaster[];
out vec2 uv;
out vec3 drtShadowLocalPos;
flat out int drtShadowWindMode;

#ifdef DRT_MOVING_SHADOWS
uniform int drtHeldProjectionIndex = -1;
layout(std140, binding = 5) uniform DrtHeldProjections {
    mat4 drtHeldMatrices[2];
    vec4 drtHeldStatus[2];
};

vec3 drtMovingRadialOffset(vec4 clip) {
    // Recover emitter-relative distances before perspective division. Cube
    // views rotate this vector but preserve its length; a held fit may be
    // asymmetric. Interpolating this vector gives the fragment's true radius.
    vec2 side = clip.xy / 0.94;
    if (drtHeldProjectionIndex >= 0) {
        mat4 p = drtHeldMatrices[drtHeldProjectionIndex];
        side = (clip.xy + p[2].xy * clip.w) / vec2(p[0][0], p[1][1]);
    }
    return vec3(side, clip.w);
}
#endif

void main()
{
    // Placed maps store binary depth, so native Transparent(3) glass must
    // transmit light instead of becoming an opaque blocker at alpha > .02.
    // The native glass shapes route solid frames separately (opaque/blend
    // cutouts); retain those passes, including doors, chests and mod terrain.
    // Emitter exclusion identifies the placed-only program's bake mode.
    if (drtExcludeEmitter != 0 && drtShadowTerrainPass == 3) return;
    if (drtExcludeWindCasters != 0 &&
        (drtPointWindCaster[0] != 0 || drtPointWindCaster[1] != 0 || drtPointWindCaster[2] != 0)) return;
    for (int i = 0; i < 3; ++i) {
        gl_Position = gl_in[i].gl_Position;
        uv = drtPointUv[i];
#ifdef DRT_MOVING_SHADOWS
        drtShadowLocalPos = drtMovingRadialOffset(gl_Position);
#else
        drtShadowLocalPos = drtPointLocalPos[i];
#endif
        drtShadowWindMode = drtPointWindMode[i];
        EmitVertex();
    }
    EndPrimitive();
}
