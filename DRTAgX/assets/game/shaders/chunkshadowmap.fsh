#version 330 core

uniform sampler2D tex2d;
uniform int drtExcludeEmitter;
uniform vec3 drtEmitterBlockMin;
// -1 keeps native/moving/default PLS coverage; all-pass PLS sets this per draw.
uniform int drtShadowTerrainPass = -1;

in vec2 uv;
in vec3 drtShadowLocalPos;
flat in int drtShadowWindMode;
out vec4 outColor;

void main () {
	if (drtExcludeEmitter != 0) {
		vec3 localPosition = drtShadowLocalPos - drtEmitterBlockMin;
		// Classify the owning voxel, including every boundary face of the
		// emitter. Move one centimetre behind the face so a neighbouring
		// block on the same plane remains a shadow caster.
		vec3 normal = cross(dFdx(drtShadowLocalPos), dFdy(drtShadowLocalPos));
		vec3 ownerPosition = localPosition;
		if (dot(normal, normal) > 1e-10) {
			normal = normalize(normal) * (gl_FrontFacing ? 1.0 : -1.0);
			ownerPosition -= normal * 0.01;
		}
		if (all(greaterThanEqual(ownerPosition, vec3(0.0))) &&
		    all(lessThan(ownerPosition, vec3(1.0)))) discard;
	}
    // Main no-cull foliage cuts at .42 (mode 13 divides that threshold by four).
    // Implicit grazing-angle mip selection averages empty alpha into the card;
    // the former .02 threshold then treated most of that card as solid depth.
    bool grass = drtShadowTerrainPass != 4 && drtShadowWindMode != 0 && drtShadowWindMode != 3 &&
        drtShadowWindMode != 6 && drtShadowWindMode != 12;
    outColor = grass ? textureLod(tex2d, uv, 0.0) : texture(tex2d, uv);
    float cutout = grass ? (drtShadowWindMode == 13 ? 0.105 : 0.42) : 0.02;
    // Match native no-cull/decor alpha tests. A cached depth map represents
    // transparent/liquid casters as alpha-tested depth, without opacity layers.
    if (drtShadowTerrainPass == 1) cutout = drtShadowWindMode == 13 ? 0.105 : 0.42;
    else if (drtShadowTerrainPass == 2) cutout = drtShadowWindMode == 13 ? 0.0625 : 0.25;
    else if (drtShadowTerrainPass == 8) cutout = drtShadowWindMode == 13 ? 0.05 : 0.2;
    if (outColor.a < cutout) discard;

}
