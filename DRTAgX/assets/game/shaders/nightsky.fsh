#version 330 core
#extension GL_ARB_explicit_attrib_location: enable

in vec3 texCoords;
in vec3 drtStarDirection;
in float worldPosY;
in float nightVisionStrengthv;

uniform vec4 rgbaFog;
uniform samplerCube ctex;
uniform int ditherSeed;
uniform int horizontalResolution;
uniform float dayLight;
uniform float horizonFog;
uniform float playerToSealevelOffset;
uniform float fogDensityIn;
uniform float fogMinIn;


out vec4 outColor;
#if SSAOLEVEL > 0
layout(location = 2) out vec4 outGNormal;
layout(location = 3) out vec4 outGPosition;
#endif


#include dither.fsh
#include fogandlight.fsh
#include underwatereffects.fsh

void main () {
    vec4 skyCol = texture(ctex, texCoords);
    // Remove the cube's black pedestal without subtracting radiance from the
    // atmosphere in empty texels. Negative star RGB left a bright terrain rim.
    skyCol.rgb = max(skyCol.rgb - vec3(0.03), vec3(0.0));
    skyCol.rgb *= 2.0;
    skyCol.a = max(0.0, 1.0 - 2.0 * (dayLight - 0.05));
    
    outColor = skyCol;
    outColor.rgb += vec3(0.1, 0.5, 0.1) * nightVisionStrengthv;

    // The following native sky overlay applies shared weather opacity once;
    // multiplying stars here as well would apply transmission twice.
    outColor.rgb *= 0.33;
    // Native sky later overlays this base with straight-alpha blending. The
    // clear atmospheric radiance belongs here too, so clear nights retain the
    // moon atmosphere while the overlay applies weather extinction once.
    if (drtAtmosphereFlags.x > 0.5) {
        outColor.rgb += drtClearSkyRadiance(drtStarDirection);
        // Atmosphere coverage is independent of star brightness. The later
        // sky overlay applies solar visibility and weather to stars once.
        outColor.a = 1.0;
    }
#if SSAOLEVEL > 0
    outGPosition = vec4(0);
    outGNormal = vec4(0);
#endif

}
