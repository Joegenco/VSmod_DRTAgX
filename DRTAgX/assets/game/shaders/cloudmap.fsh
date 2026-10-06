#version 330 core
#extension GL_ARB_explicit_attrib_location: enable

in vec2 uv;
in vec2 ndc;

layout(location = 0) out vec4 tile;
layout(location = 1) out vec4 colour;

uniform float dayLight;
uniform float globalCloudBrightness;
uniform float time;
uniform vec4 rgbaFogIn;
uniform float fogMinIn;
uniform float fogDensityIn;
uniform vec3 sunPosition;
uniform float nightVisionStrength;
uniform float alpha;

#define rgbaFog rgbaFogIn
#include fogandlight.fsh
#define NoiseFromPixelPosition(a, b, c) vec4(0.0)
#include skycolor.fsh
#include drtagx_cloud_lighting.fsh

uniform float width;
uniform sampler2D mapData1;
uniform sampler2D mapData2;
uniform vec3 mapOffset;
uniform vec2 mapOffsetCentre;
uniform mat4 viewMatrix;

uniform int pointLightQuantity;
#if DYNLIGHTS > 0
uniform vec3 pointLights[DYNLIGHTS];
uniform vec3 pointLightColors[DYNLIGHTS];
#endif

vec4 getPointLightRgbvl(vec3 worldPos) {
#if DYNLIGHTS == 0
    return vec4(0);
#else
    vec4 pointColSum = vec4(0);
    float bPointBrightSum = 0;

    for (int i = 0; i < pointLightQuantity; i++) {
        vec4 lightVec = -vec4(worldPos.x - pointLights[i].x, worldPos.y - pointLights[i].y, worldPos.z - pointLights[i].z, 1);
        vec3 color = pointLightColors[i];
        if (color.r > 10) {
            color /= 200; // This is a Lightning strike point light
        }

        float dist = pow(1.35, length(lightVec) / 4.0);
        float bright = (color.r + color.g + color.b);
        float strength = min(bright/3, bright / dist);

        pointColSum.w = max(pointColSum.w, strength);
        bPointBrightSum += strength;

        pointColSum.r += color.r * strength;
        pointColSum.g += color.g * strength;
        pointColSum.b += color.b * strength;
    }

    if (bPointBrightSum > 0) {
        pointColSum.rgb /= max(1, bPointBrightSum);
    }

//  pointColSum.w /= max(1, glitchStrengthFL * 2);

    return pointColSum;
#endif
}

float getFogLevel(vec4 worldPos, float fogMin, float fogDensity) {
    float depth = length(worldPos.xyz);
    float clampedDepth = min(250, depth);
    float heightDiff = worldPos.y - flatFogStart;
    float extraDistanceFog = max(-flatFogDensity * clampedDepth * (flatFogStart) / 60, 0); // div 60 was 160 before, at 160 thick flat fog looks broken when looking at trees
    float distanceFog = 1 - 1 / exp(clampedDepth * fogDensity + extraDistanceFog);

    float flatFog = 1 - 1 / exp(heightDiff * flatFogDensity);

    float val = max(flatFog, distanceFog);
    float nearnessToPlayer = clamp((8-depth)/8, 0, 0.9);
    val = max(min(0.04, val), val - nearnessToPlayer);

    // Needs to be added after so that underwater fog still gets applied.
    val += fogMin;

    return clamp(val, 0, 1);
}

void main(){

    const float cloudTileSize = 50.0;

    vec4 data1 = texelFetch(mapData1, ivec2(uv * width), 0);
    vec4 data2 = texelFetch(mapData2, ivec2(uv * width), 0);
    float thinCloudMode      = data1.r;
    float selfThickness      = data1.g;
    float cloudOpaqueness    = data1.b;
    float cloudBrightness    = data1.a;
    float undulatingModeness = data2.r;

    vec2 v = uv * width - width / 2.0;
    vec3 tilePosition = vec3(mapOffset.xz + v * cloudTileSize, mapOffset.y).xzy;
    vec3 viewSpace = vec3(viewMatrix * vec4(tilePosition, 1.0));

    float undulate = gnoise(vec3((v + mapOffsetCentre) * vec2(0.5, 0.2), time * 0.15)) * undulatingModeness;

    float linearfade = abs(length((uv + mapOffset.xz/cloudTileSize/width) * 2.0 - 1.0));

    float opaque = min(1.0, cloudOpaqueness * min(1.0, 10.0 * selfThickness)) * 2.0;
    opaque += undulatingModeness * 4.0;
    opaque *= alpha;
    opaque *= smoothstep(0.95, 0.9, linearfade);

    // Retain native weather/tile shading; solar elevation supplies illumination below.
    float greyscale = (0.1 + cloudBrightness * 0.7)
                    * globalCloudBrightness
	;
    greyscale *= mix(1, 0.4 + 0.5*(undulate + 0.5), undulatingModeness);

    float height = (500.0 - 500.0 * thinCloudMode)
                 * max(0.0, 1.0 - 3.0 * thinCloudMode)
                 * pow(selfThickness - 0.1, 2.0);

    float lo = (-12.5 - height * 0.05 - undulate * 25.0) / cloudTileSize;
    float hi = ( 12.5 + height        - undulate * 25.0) / cloudTileSize;

    colour = vec4(vec3(greyscale), 1.0);

    // Intrinsic cloud light only; fog visibility and directionality are resolved by the draw.
    // Local/gameplay lights stay additive, after environmental illumination.
    if (drtFogColor.w > 0.5) colour.rgb *= drtCloudLight();
    else {
        // Native startup/provider fallback also keeps the engine's fog/daylight setters active.
        colour.rgb *= smoothstep(0.0, 1.1, dayLight);
        vec4 skyGlow = getSkyGlowAt(vec3(tilePosition.x, 100.0, tilePosition.z), sunPosition, 0.25, 1.0, 0.0, 0.7);
        colour.rgb *= mix(vec3(1.0), 1.2 * skyGlow.rgb, skyGlow.a) * max(1.0, 0.9 + skyGlow.a / 10.0);
        float fogAmount = getFogLevel(vec4(tilePosition.x, 0.0, tilePosition.z, 1.0), fogMinIn, fogDensityIn);
        colour.rgb = mix(colour.rgb, rgbaFogIn.rgb, clamp(fogAmount - 0.04, 0.0, 1.0));
    }
    colour.rgb += getPointLightRgbvl(viewSpace).rgb * 0.7;
    colour.rgb += vec3(0.1, 0.5, 0.1) * nightVisionStrength;

    tile.r = opaque;
    tile.g = 0.0;
    tile.b = lo;
    tile.a = hi;

}
