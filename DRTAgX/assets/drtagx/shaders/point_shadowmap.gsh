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
        drtShadowLocalPos = drtPointLocalPos[i];
        drtShadowWindMode = drtPointWindMode[i];
        EmitVertex();
    }
    EndPrimitive();
}
