// DRTAgX deferred include: Default uniforms, sampler reservations and static SSBO ABI. Names remain stable for C# callers.

// Current-frame native light uniforms and owned point-light depth cascades.
uniform int drtPointCount;
uniform vec3 drtPointPos[16];
uniform vec3 drtPointColor[16];
uniform vec4 drtPointRadiance[16]; // normalized RGB and unchanged authored range
uniform mat4 drtProjectionMatrix;
uniform int drtShadowCount;
// Shared style switch and double-camera fractional phase, also valid with sun shadows disabled.
uniform int drtShadowGridEnabled = 1;
uniform vec3 drtSunGridCameraPhase;
uniform int drtDebugView;
uniform int drtContactShadowsEnabled;
uniform int drtShadowSlot[16];
uniform sampler2DShadow drtShadowMaps;
uniform int drtShadowKind[10]; // 0 cube, 1 right hand, 2 left hand
uniform float drtShadowRange[10];
uniform vec4 drtMovingAtlasInfo; // width, height, face size, tile stride with gutters
uniform int drtMovingAtlasColumns;
layout(std140, binding = 5) uniform DrtHeldProjections {
    mat4 drtHeldMatrices[2];
    vec4 drtHeldStatus[2];
};

// Cached placed-light depth array and per-screen-tile source masks.
layout(std430, binding = 4) readonly buffer DrtStaticSourceBuffer { vec4 drtStaticSourceData[]; };
layout(std430, binding = 5) readonly buffer DrtStaticTileBuffer { uint drtStaticTileData[]; };
uniform int drtStaticCount;
uniform int drtStaticAllTerrainPasses = 0;
uniform float drtStaticBlend;
uniform int drtStaticTileWidth;
uniform sampler2DArrayShadow drtStaticMaps;
uniform int drtStaticDebugState;
uniform vec4 drtStaticDebugCounts; // scan progress, found, selected, cached

// Native terrain sky RGB captured after its draw; calibration comes from live world tables.
uniform vec3 drtSkyColor = vec3(0.0);
uniform vec2 drtPlacedCalibration[32]; // direct amplitude, owning-voxel amplitude
// Shared with forward programs; mode changes require no shader reload.
#include drtagx_frame_quality.ash
