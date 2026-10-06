// SheyderMod: vendored engine fogandlight.fsh
uniform float flatFogDensity;
uniform float flatFogStart;
uniform float viewDistance;
uniform float viewDistanceLod0;

uniform float zNear;
uniform float zFar;
uniform vec3 lightPosition;
uniform float shadowIntensity;

in float sm_voxSunLight;
#include drtagx_surface_lighting.fsh

#if SHADOWQUALITY > 0
in float blockBrightness;
in vec4 shadowCoordsFar;
uniform sampler2DShadow shadowMapFar;
uniform float shadowMapWidthInv;
uniform float shadowMapHeightInv;
#endif

#if SHADOWQUALITY > 1
in vec4 shadowCoordsNear;
uniform sampler2DShadow shadowMapNear;
#endif

const float Epsilon = 0.02;
const float SmoothK = 0.02;

uniform float windWaveCounter;
uniform float glitchStrength;
uniform float psychedelicStrength;

#include noise3d.ash
#include vertexflagbits.ash
#include fogspheres.ash
#include worldfog.fsh
#include drtagx_atmosphere_sampling.fsh
// Native sun sprites use standard, while the moon uses celestialobject.
// Both programs need the same authored HDR sprite gains after mod startup.
#include drtagx_celestial_balance.ash

// Polynomial smooth maximum between scalar 'a' and 'b' with smoothing factor 'k'
float smax(float a, float b, float k) {
    float h = clamp(0.5 + 0.5 * (b - a) / k, 0.0, 1.0);
    return mix(a, b, h) + k * h * (1.0 - h);
}

// Vector variant applying smooth max element-wise against a lower bound scalar
vec3 smaxVec3(vec3 v, float b, float k) {
    return vec3(
        smax(v.r, b, k),
        smax(v.g, b, k),
        smax(v.b, b, k)
    );
}

vec4 applyFrostEffect(float frostAlpha, vec4 texColor, vec3 normal, vec3 noisepos) {
    if (frostAlpha > 0) {
        noisepos = round(noisepos * 32.0) / 32;
        noisepos.xyz *= 1.5;
        
        frostAlpha*=1+max(0.0, normal.y/3);
        frostAlpha *= (valuenoise(noisepos * 2) + valuenoise(noisepos * 12)) * 1.25 - 0.25;
        
        float heretemp = -10;
        float w = clamp((0.333 - heretemp) * 15, 0, 1);
        
        vec3 frostColor = vec3(1);    
        float faw = frostAlpha * w;
        texColor.rgb = texColor.rgb * (1 - faw) + frostColor * faw;
    }
    
    return texColor;
}

vec3 palette( float t ){
     vec3 a = vec3((-sin(windWaveCounter/15.0*1.32456)), cos(1/25.0*0.76354), sin(windWaveCounter/14.5));
     vec3 b = vec3(.75,.25,.65);
     vec3 c = vec3(1.,1.,1.);
     vec3 d = vec3(0.263,0.416,0.557);
     return a*b-tan( 6.28318*(c*t+d) );
}

vec4 applyPsychedelicEffect(vec4 texColor, vec3 rustVec, int sub) {
    if (texColor.a <= 0) return texColor;

    float df = clamp((gl_FragCoord.w*20 - 0.3) * 20, 0.09, 1);
    
    vec3 uv = rustVec;
    vec3 uv0 = uv;
    vec3 fcol = vec3(-0.01, -0.01, -0.01);
    float f = max(5, 15.0 * clamp((df + 0.2)/3.0, 0, 1));
    
    float t = windWaveCounter / 15.5;
    
    for (float i =1.0; i<f; i++)
    {
        float luv = length(uv);
        uv.x += sin(uv.y*i-luv+t)/i;
        uv.y += sin(uv.x*i+luv+t)/i;
        uv.z += sin(uv.z*i+luv+t)/i;
   
        float d = luv*exp(-length(uv0));
        d = 1/8.0;
        d = pow(0.01+d,1.2);

        vec3 col = cos(palette(luv+i*0.4));
        fcol += col*(d + max(0, 0.1-df))/(f/i);
    }
    
    if (sub > 0) fcol = -2*fcol;
    float b = min(1, (texColor.r+texColor.g+texColor.b));
    vec3 outcolor = mix(texColor.rgb, texColor.rgb + b*fcol.rgb, psychedelicStrength);    

    return vec4(outcolor.r, outcolor.g, outcolor.b, texColor.a);
}

vec4 applyRustEffect(vec4 texColor, vec3 normal, vec3 rustVec, int spotty) {
        float f = clamp(gl_FragCoord.w*3 - 0.3, 0, 1);
        if (f <= 0) return texColor;
        
        float b = clamp(((texColor.r + texColor.g + texColor.b) / 3.0 ) * 10, 0, 1);
        float intensity = b * glitchStrength;
        if (spotty > 0) intensity *= max(0.0, cnoise(vec3(rustVec.x * 0.35, rustVec.y * 0.35, rustVec.z * 0.35)) + 0.3);
        else intensity *= 1.3 * max(0.0, cnoise(vec3(rustVec.x * 1.5, rustVec.y * 1.5, rustVec.z * 0.35)) + 0.35);
        
        if (intensity < 0.01) return texColor;
        
        float uvx = round(rustVec.x * 32.0) / 100.0 / 32;
        float uvy = round(rustVec.y * 32.0) / 100.0 / 32;
        float uvz = round(rustVec.z * 32.0) / 100.0 / 32;
        
        if (normal.y < 0.5) {
            float val = 0.2 * cnoise(vec3(uvx*3000, uvy*700 + windWaveCounter * 1.5, uvz*3000 + windWaveCounter / 5)) + 0.1 * cnoise(vec3(uvx*15000, uvy*3500 + windWaveCounter, uvz*15000 + windWaveCounter / 5));
            texColor.rgb += intensity * val;
        } else {
            float val = 0.2 * cnoise(vec3(uvx*700, uvy*700 + windWaveCounter / 2, uvz*700)) + 0.1 * cnoise(vec3(uvx*15000, uvy*3500 + windWaveCounter / 5, uvz*15000));
            texColor.rgb += intensity * val;
        }
    
        return texColor;
}

vec4 applyReflectiveEffect(vec4 texColor, inout float glow, int renderFlags, vec2 uv, vec3 normal, vec4 worldPos, vec4 camPos, vec3 blockLight) {
    if ((renderFlags & ReflectiveBitMask) == 0) return texColor;
    
    int windMode = (renderFlags >> 29) & 0x7;
    
    if (windMode == ReflectiveModeWeak) {
        vec3 worldVec = normalize(worldPos.xyz);
        float uvx = round(uv.x * 64 * 32 * 1.0) * 4.0 / 32;
        float uvy = round(uv.y * 64 * 32 * 1.0) * 4.0 / 32;
        float uvz = round(1.0 * 32) * 8.0 / 32;
        
        float fd = 1 * (gnoise(vec3(uvx, uvy, uvz)));    
        fd *= 25*gnoise(round(worldPos.xyz * 20.0) / 30);
        fd = max(0.0,fd + 1);
        float nb = max(0.1, 0.5 * dot(normal, lightPosition)) *
            clamp(dot(drtSunEffectColor(), DRT_LUMINANCE), 0.0, 1.0);
        
        texColor.rgb = drtSunOnlyMultiplier(texColor.rgb, vec3(1.0) + vec3(nb * fd) / 2.0);
        texColor.a = clamp(texColor.a + nb*fd, texColor.a/2, 1);
        glow+=nb*fd * 0.15;
        
        return texColor;
    }
    
    if (windMode == ReflectiveModeMedium) {
        vec3 worldVec = normalize(worldPos.xyz);
        float uvx = round(uv.x * 64 * 32 * 1.0) * 8.0 / 32;
        float uvy = round(uv.y * 64 * 32 * 1.0) * 8.0 / 32;
        float uvz = round(1 * 32.0) * 8.0 / 32;
        
        float fd = 1 * (gnoise(vec3(uvx, uvy, uvz)));    
        fd *= 15*gnoise(round(worldPos.xyz * 30.0) / 30);
        fd = max(0.0,fd + 1);
        float nb = max(0.1, 0.5 * dot(normal, lightPosition)) *
            clamp(dot(drtSunEffectColor(), DRT_LUMINANCE), 0.0, 1.0);
        
        if (windMode == ReflectiveModeMild) fd/=3;
        
        texColor.rgb = drtSunOnlyMultiplier(texColor.rgb, vec3(1.0) + vec3(nb * fd) / 2.0);
        glow+=nb*fd * 0.15;
        
        return texColor;
    }
    
    if (windMode == ReflectiveModeStrong || windMode == ReflectiveModeMild) {
        vec3 worldVec = normalize(worldPos.xyz);
        float uvx = round(uv.x * 64 * 32 * 1.0) * 8.0 / 32;
        float uvy = round(uv.y * 64 * 32 * 1.0) * 8.0 / 32;
        float uvz = round(1 * 32.0) * 8.0 / 32;
        
        float fd = 1 * (gnoise(vec3(uvx, uvy, uvz)));    
        fd *= 25*gnoise(round(worldPos.xyz * 100.0) / 30);
        fd = max(0.0,fd + 1);
        float nb = max(0.1, 0.5 * dot(normal, lightPosition)) *
            clamp(dot(drtSunEffectColor(), DRT_LUMINANCE), 0.0, 1.0);
        texColor.rgb = drtSunOnlyMultiplier(texColor.rgb, vec3(1.4) + vec3(nb * fd) / 2.0);
        glow+=nb*fd * 0.15;
        
        return texColor;
    }
    
    if (windMode == ReflectiveModeSparkly) {
        vec3 worldVec = normalize(worldPos.xyz);
        float mul=3;
        float uvx = round(uv.x * 64 * 32 * 2.0) * 8.0 / 32;
        float uvy = round(uv.y * 64 * 32 * 2.0) * 8.0 / 32;
        
        float fd = 1 * (gnoise(vec3(uvx, uvy, 0)));    
        fd *= 50*gnoise(round(camPos.xyz * 150.0) / 30);
        fd = max(0.0,fd + 1);
        float nb = max(0.1, 0.5 * dot(normal, lightPosition)) *
            clamp(dot(drtSunEffectColor(), DRT_LUMINANCE), 0.0, 1.0);
        texColor.rgb = drtSunOnlyMultiplier(texColor.rgb, vec3(1.0) + vec3(nb * fd) / 2.0);
        glow+=nb*fd * 0.03;    
    
        return texColor;
    }
    
    if (windMode==5) {
        texColor.rgb = vec3(1);
    }
    
    return texColor;
}

float linearDepth(float depthSample)
{
    depthSample = 2.0 * depthSample - 1.0;
    float zLinear = 2.0 * zNear / (zFar + zNear - depthSample * (zFar - zNear));
    return zLinear;
}

float depthSample(float linearDepth)
{
    float nonLinearDepth = (zFar + zNear - 2.0 * zNear * zFar / linearDepth) / (zFar - zNear);
    nonLinearDepth = (nonLinearDepth + 1.0) / 2.0;
    return nonLinearDepth;
}

vec4 applyFog(vec4 rgbaPixel, float fogWeight) {
    return vec4(mix(rgbaPixel.rgb, rgbaFog.rgb, clamp(fogWeight, 0.0, 1.0)), rgbaPixel.a);
}

float sm_sunShadowBright = 1.0;
// Thin grass cards need more receiver bias than solid terrain or leaves.
float drtSunReceiverBiasScale = 1.0;
// Directional shadow strength is shared with vertex shading in light_balance.
// Local point-light occlusion is a separate path.
#include drtagx_sun_shadow_sampling.fsh
#ifndef DRT_DEFERRED_LIGHTING
#if SHADOWQUALITY > 0
uniform mat4 toShadowMapSpaceMatrixFar;
#endif
#if SHADOWQUALITY > 1
uniform mat4 toShadowMapSpaceMatrixNear;
#endif
vec3 drtForwardSunGridOffset = vec3(0.0);
vec3 drtForwardSunPlaneNormal = vec3(0.0);
void drtPrepareSunGrid(vec3 worldPos, vec3 normalWorld) {
    // Same receiving-plane grid as deferred, gated by the saved frame preference.
    // Only callers with a known geometric plane opt into analytic filtering.
    // Models may supply smooth material normals; preserve their derivative path.
    drtForwardSunPlaneNormal = vec3(0.0);
    drtForwardSunGridOffset = vec3(0.0);
    if (drtSunGridFrame.w > 0.5)
        drtForwardSunGridOffset = drtSunGridOffset(worldPos - drtAtmosphereCamera.xyz,
            normalWorld, drtSunGridFrame.xyz);
}
#endif
float getBrightnessFromShadowMap() {
    #if SHADOWQUALITY > 0
    vec3 farCoord = shadowCoordsFar.xyz;
    vec2 farGradient;
    #if SHADOWQUALITY > 1
    vec3 nearCoord = shadowCoordsNear.xyz;
    vec2 nearGradient;
    #endif
#ifndef DRT_DEFERRED_LIGHTING
    // Use the prepared geometric plane rather than derivatives after cutout
    // discards/material branches. Both cascades follow the actual wind bend.
    if (dot(drtForwardSunPlaneNormal, drtForwardSunPlaneNormal) > 0.0001) {
        farGradient = drtSunFoliageReceiver ?
            drtSunFoliagePlaneGradient(toShadowMapSpaceMatrixFar, shadowMapFar, drtForwardSunPlaneNormal) :
            drtSunPlaneGradient(toShadowMapSpaceMatrixFar, drtForwardSunPlaneNormal);
#if SHADOWQUALITY > 1
        nearGradient = drtSunFoliageReceiver ?
            drtSunFoliagePlaneGradient(toShadowMapSpaceMatrixNear, shadowMapNear, drtForwardSunPlaneNormal) :
            drtSunPlaneGradient(toShadowMapSpaceMatrixNear, drtForwardSunPlaneNormal);
#endif
    } else
#endif
    {
        // Unprepared/normal-free callers retain the native interpolated plane.
        farGradient = drtSunReceiverGradient(shadowCoordsFar.xyz);
#if SHADOWQUALITY > 1
        nearGradient = drtSunReceiverGradient(shadowCoordsNear.xyz);
#endif
    }
#ifndef DRT_DEFERRED_LIGHTING
    farCoord += (toShadowMapSpaceMatrixFar * vec4(drtForwardSunGridOffset, 0.0)).xyz;
#if SHADOWQUALITY > 1
    nearCoord += (toShadowMapSpaceMatrixNear * vec4(drtForwardSunGridOffset, 0.0)).xyz;
#endif
#endif
    float totalFar = 0.0;
    if (shadowCoordsFar.w > 0) {
        totalFar = 1.0 - drtSunShadowVisibility(shadowMapFar, farCoord,
            farGradient, 0.00003, drtSunReceiverBiasScale);
    }

    float b = 1.0 - shadowIntensity * totalFar * shadowCoordsFar.w * DRT_SUN_SHADOW_GAIN;
    #endif
    
    #if SHADOWQUALITY > 1
    float totalNear = 0.0;
    if (shadowCoordsNear.w > 0) {
        totalNear = 1.0 - drtSunShadowVisibility(shadowMapNear, nearCoord,
            nearGradient, 0.00002, drtSunReceiverBiasScale);
    }
    
    b -= shadowIntensity * totalNear * shadowCoordsNear.w * DRT_SUN_SHADOW_GAIN;
    #endif
    
    #if SHADOWQUALITY > 0
    sm_sunShadowBright = clamp(b, 0.0, 1.0);
    b = sm_sunShadowBright; // Local light never fills the directional visibility mask.
    return b;
    #endif
    
    return 1.0;
}

uniform float ox_Strength  = 0.0;
uniform float ox_BloomFeed = 0.0;
uniform vec3  ox_SunBias   = vec3(0.0);

const float OX_DIST_INV = 1.0 / 300.0;

float ox_blockLightGate = 1.0;
float ox_sunFacing      = 1.0;
float ox_glare          = 0.0;

void ox_prepare(vec3 nrm, float blockBright) {
    if (ox_Strength <= 0.0) return;

    float dp = dot(nrm, lightPosition);
    ox_sunFacing = 0.25 + 0.75 * clamp(dp * 20.0, 0.0, 1.0) * (0.5 + 0.5 * dp);
    ox_blockLightGate = 1.0; // Boost belongs to sunlight alone.
}

void ox_apply(inout vec3 color, vec3 worldPos) {
    ox_glare = 0.0;
    if (ox_Strength <= 0.0) return;

    float sun = sm_sunShadowBright * clamp(dot(drtSunEffectColor(), DRT_LUMINANCE), 0.0, 1.0);
    float dist = clamp(1.0 - length(worldPos) * OX_DIST_INV, 0.0, 1.0);
    float e    = sun * sun * ox_sunFacing * dist * ox_Strength;

    // Primary color 0 is RGBA16F: keep overbright sunlight for the AgX transform.
    color *= 1.0 + e * ox_SunBias;
    ox_glare = e * ox_BloomFeed;
}

float getBrightnessFromNormal(vec3 normal, float normalShadeIntensity, float minNormalShade) {
    // Model/skinning transforms can scale normals. Terrain deferred already
    // normalizes them; forward entities, held items and particles need the same
    // unit-length contract or small models receive weaker sunlight.
    float normalLength2 = dot(normal, normal);
    if (normalLength2 > 1e-8) normal *= inversesqrt(normalLength2);
    float facing = dot(normal, lightPosition);
    float nb = max(minNormalShade, 0.5 + 0.5 * facing);
    nb = max(nb, normal.y * 0.95);
#if SHADOWQUALITY > 0
    // Match grazing face darkness to the actual CSM strength. Sunlight access
    // and radiance gate this later; local light/emission never receive this shade.
    nb = max(minNormalShade, drtSunFaceBrightness(nb, facing,
        shadowIntensity * DRT_SUN_SHADOW_GAIN));
#endif
    float normalBrightness = mix(1.0, nb, normalShadeIntensity);
    return clamp(1.0 - (1.0 - normalBrightness) * 1.0, 0.0, 1.0);
}

vec4 applyFogAndShadow(vec4 rgbaPixel, float fogWeight) {
    float b = mix(1.0, getBrightnessFromShadowMap(), sqrt(clamp(drtSurfaceSunAccess, 0.0, 1.0)));
    rgbaPixel.rgb = drtShadeSurface(rgbaPixel.rgb, b, vec3(1.0));
    return applyFog(rgbaPixel, fogWeight);
}

vec4 applyFogAndShadowFromBrightness(vec4 rgbaPixel, float fogAmount, float b, vec3 worldPos) {
    b = mix(1.0, b, sqrt(clamp(drtSurfaceSunAccess, 0.0, 1.0)));
    // Retain the native sunlight boost, confined to the sun component.
    b *= 1.0 + max(0.0, shadowIntensity * 2.0 - 1.66) / 1.5;
    vec3 boost = vec3(1.0);
    ox_apply(boost, worldPos);
    rgbaPixel.rgb = drtShadeSurface(rgbaPixel.rgb, b, boost);
#ifdef DRT_SURFACE_FOG_AFTER_LIGHTING
    return rgbaPixel; // Caller completes all surface radiance before transport.
#else
    return applySpheresFog(drtApplyAirFog(rgbaPixel, worldPos, true, drtSurfaceSunAccess), 0.0, worldPos);
#endif
}

vec4 applyFogAndShadowWithNormal(vec4 rgbaPixel, float fogAmount, vec3 normal, float normalShadeIntensity, float minNormalShade, vec3 worldPos) {
#ifndef DRT_DEFERRED_LIGHTING
    drtPrepareSunGrid(worldPos, normal);
#endif
    ox_prepare(normal, 0.0);
    float b = drtCombineSunDarkening(getBrightnessFromShadowMap(),
        getBrightnessFromNormal(normal, normalShadeIntensity, minNormalShade));
    return applyFogAndShadowFromBrightness(rgbaPixel, fogAmount, b, worldPos);
}

float getFogLevel(float fogMin, float fogDensity, float worldPosY) {
    // Compatibility ABI: reconstruct a physical view-space ray; raw device Z
    // is non-linear and must never be treated as a distance in blocks.
    vec2 uv = gl_FragCoord.xy / max(drtAtmosphereScreen.xy, vec2(1.0));
    vec4 view = drtAtmosphereInvProjection * vec4(uv * 2.0 - 1.0, gl_FragCoord.z * 2.0 - 1.0, 1.0);
    vec4 world = drtAtmosphereInvView * vec4(view.xyz / max(abs(view.w), 0.00001), 1.0);
    if (drtFogColor.w > 0.5) return drtFogOpacity(world.xyz, true, drtSurfaceSunAccess);
    float depth = 2.0 * zNear * zFar / max(zFar + zNear - (2.0 * gl_FragCoord.z - 1.0) * (zFar - zNear), 0.00001);
    return clamp(1.0 - exp(-max(depth * fogDensity, 0.0)) + fogMin, DRT_MIN_FOG_OPACITY, 1.0);
}
