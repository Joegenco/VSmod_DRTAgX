#version 330 core
#extension GL_ARB_shader_storage_buffer_object : require
#extension GL_ARB_shading_language_420pack : require

// SheyderMod: vendored engine chunkliquid.fsh (water / lava pass) with mod hooks: analytic
// entity/player water ripples (ripples.fsh) folded into the foam, and the atmospheric WorldFog
// applied to the liquid surface. Mod additions are wrapped in SheyderMod markers; re-sync
// against the engine copy on updates.

uniform sampler2D terrainTex;
uniform sampler2D depthTex;

uniform vec2 blockTextureSize;
uniform vec2 textureAtlasSize;
uniform float waterFlowCounter;
uniform vec3 sunPosRel;
uniform vec3 sunColor;
uniform vec3 reflectColor;
uniform float waterWaveCounter;
uniform float sunSpecularIntensity;
uniform float dropletIntensity = 0;
uniform float windSpeed;

in vec4 rgba;
in vec4 rgbaFog;
in float fogAmount;
in vec2 uv;
in vec2 uvSize;
in float waterStillCounterOff;
flat in vec2 uvBase;
in vec3 fragWorldPos;
in vec3 drtPlacedRestPos;
in vec3 fWorldPos;
in vec3 fragNormal;
in float fresnel;
flat in int skyExposed;

in vec2 flowVectorf;
in float glowLevel;

flat in int waterFlags;
flat in int renderFoam;

#define DRT_TERRAIN_PLACED_OCCLUSION
#include fogandlight.fsh
#include noise3d.ash
#include colormap.fsh
#include underwatereffects.fsh
#include drtagx_terrain_boundary.fsh
#include drtagx_terrain_placed_occlusion.fsh
#include oit.fsh
// SheyderMod: analytic entity/player water ripples (rippleField()).
#include ripples.fsh

vec2 droplethash3( vec2 p )
{
    vec2 q = vec2(dot(p,vec2(12.71,31.17)), dot(p,vec2(26.95,18.33)));
    return fract(sin(q)*43758.5453);
}

float dropletnoise(in vec2 x)
{
    if (dropletIntensity < 0.001) return 0.;
    
    x *= dropletIntensity;
    
    vec2 p = floor(x);
    vec2 f = fract(x);

    float va = 0.0;
    for( int j=-1; j<=1; j++ )
    for( int i=-1; i<=1; i++ )
    {
        vec2 g = vec2(float(i), float(j));
        vec2 o = droplethash3(p + g);
        vec2 r = g - f + o;
        float d = length(r) / dropletIntensity;
        
        float a = max(cos(d - waterWaveCounter * 2.7 + (o.x + o.y) * 5.0), 0.);
        a = smoothstep(0.99, 0.999, a);
        
        float ripple = mix(a, 0., d);
        va += max(ripple, 0.);
    }
    
    return va;
}

void main() 
{
    // Discard only at the fully hidden endpoint; fog fades RGB continuously.
    drtCaptureTerrainPlacedPlane(drtPlacedRestPos);
    drtDiscardTerrainBoundary(fWorldPos.xyz);
    if (rgba.a < 0.005) discard;
    float murkiness = getUnderwaterMurkiness();
    if (murkiness > 0.05) discard;

    vec4 texColor;
    
    float vn = max(0, 0.9 - abs(fragNormal.y));
    float wfc;
    
    bool isLava = (waterFlags & LiquidIsLavaBitMask) > 0;
    bool fullAlpha = (waterFlags & LiquidFullAlphaBitMask) > 0;
    
    if (isLava) wfc = waterFlowCounter * 0.1 * (1 + 5 * vn);
    else wfc = waterFlowCounter * (1 + 5 * vn);

    float flowSpeed = length(flowVectorf);
    if (flowSpeed > 0.001) {
        vec2 flowVec = normalize(flowVectorf) * flowSpeed;
        
        if (fragNormal.y < 0) wfc*=-1;
        
        vec2 uvxOffset = 
            clamp(
                mod((uv - uvBase) + flowVec * wfc * blockTextureSize, blockTextureSize),
                vec2(1 / textureAtlasSize), 
                blockTextureSize - 1 / textureAtlasSize)
        ;
        
        texColor = texture(terrainTex, uvBase + uvxOffset);
        
    } else {
        vec2 uvxOffset = 
            clamp(
                blockTextureSize - uvSize,
                vec2(1 / textureAtlasSize), 
                blockTextureSize - 1 / textureAtlasSize)
        ;
        
        texColor = texture(terrainTex, uv) * waterStillCounterOff + (1-waterStillCounterOff) * texture(terrainTex, uvBase + uvxOffset);                
    }
    
    texColor = getColorMapped(terrainTex, texColor);

    if (psychedelicStrength > Epsilon) texColor = applyPsychedelicEffect(texColor, fragWorldPos, 0);

    vec4 rgbaFinal = rgba;
#if FOAMEFFECT > 0
    rgbaFinal.a = rgba.a + max(0, texColor.a - 0.4);
#else
    rgbaFinal.a = rgba.a + texColor.a;
#endif
    float bright = (rgba.r + rgba.g + rgba.b)/3;
    
    // Smooth square-root sunlight falloff
    float sunFactor = sqrt(clamp(sm_voxSunLight, 0.0, 1.0));

    // Disable shadow map depth darkening/artifacts in caves
    drtPrepareTerrainPlacedVisibility(drtPlacedRestPos);
    drtPrepareSunGrid(fWorldPos, fragNormal);
    float shadowBright = min(getBrightnessFromShadowMap(), getBrightnessFromNormal(fragNormal, 1.0, 0.45));
    shadowBright = mix(1.0, shadowBright, sunFactor);

    float x = gl_FragCoord.x / frameSize.x;
    float y = gl_FragCoord.y / frameSize.y;

    if (fullAlpha) {
        rgbaFinal.a=1;
    }

    if (isLava) {
        texColor *= vec4(vec3(dot(drtShadeSurface(rgbaFinal.rgb, shadowBright, vec3(1.0)), vec3(1.0 / 3.0))), rgbaFinal.a);
    } else {
        texColor *= vec4(drtShadeSurface(rgbaFinal.rgb, shadowBright, vec3(1.0)), rgbaFinal.a);
    }
    
    if (flowSpeed > 0) {
        texColor.a *= 1.2 * flowSpeed;
    }

    bool doLightFoam = (waterFlags & LiquidWeakFoamBitMask) != 0;
    
    float accuWeight = 1;
    
#if FOAMEFFECT > 0
    if (rgbaFinal.a > 0) {
        float ownDepth = linearDepth(gl_FragCoord.z);
        float diffTotal = 0;
        int range = 2;
        for (int dx = -range; dx <= range; dx++) {
            for (int dy = -range; dy <= range; dy++) {
                float diff = ownDepth - linearDepth(texture(depthTex, vec2(x + dx/frameSize.x, y + dy/frameSize.y)).x);
                if (diff < 0.001) {
                    diffTotal += abs(diff);
                }
            }
        }
        
        diffTotal /= (4*range * range);
        diffTotal = min(diffTotal, -vn/10 + 0.05);
        
        if (isLava) {
            float intensity = clamp(dot(fragNormal, vec3(0, 1, 0)), 0, 1) * 0.5;
            float a = fragWorldPos.x + fragWorldPos.y - 1.5 * flowVectorf.x * wfc;
            float b = fragWorldPos.z - 1.5 * flowVectorf.y * wfc;
            
            float diff = intensity * clamp(1 - diffTotal*1000 - gnoise(vec3(a*35, b*35, wfc))/2 + gnoise(vec3(a*2, b*2, wfc))/2, 0, 1);
            float noise = intensity * (gnoise(vec3(a, b, wfc)) + 0.5) / 2;
            float rgbAdd = bright*(diff * 0.3 + noise/10);
            texColor.rgb -= vec3(rgbAdd, rgbAdd, rgbAdd);
            texColor.a=1;
            accuWeight=1;
            
            float blackSpots = gnoise(fragWorldPos.xyz) + 0.5;
            
            texColor.rgb -= blackSpots * 1.5 * 0.5;
            texColor.g += 0.2 * 0.5;
            
        } else {
            // Cold liquids
            vec3 localPos = fragWorldPos.xyz;

            // SheyderMod: ripple field (.x signed crest, .y overlap density)
            vec2 rf = rippleField(localPos);
            float rfield = rf.x;
            float rdens  = rf.y;
            
            // Foam
            float intensity = clamp(dot(fragNormal, vec3(0, 1, 0)), 0, 1);
            
            float a = localPos.x + localPos.y - 1.5 * flowVectorf.x * wfc;
            float b = localPos.z - 1.5 * flowVectorf.y * wfc;
            
            float noise1 = gnoise(vec3(a*15, -abs(b*15), wfc)) + gnoise(vec3(a*5, b*5, wfc));
            float noise2 = gnoise(vec3(a, b, wfc));
            
            float diff = intensity * clamp(1 - diffTotal*1500 - noise1/2 + noise2/2, 0, 1);
            float noise = intensity * (gnoise(vec3(a * 0.4, b * 0.4, wfc))/2 + gnoise(vec3(a, b, wfc))/2 + 0.5) / 2;
            
            float rgbAdd = max(0, bright*(diff * 0.3 + noise/10));
            if (doLightFoam) {
                rgbAdd *= 0.5;
            }
            
            texColor.rgb += vec3(rgbAdd, rgbAdd, rgbAdd);
            texColor.a += (max(0, diff/16 + noise/(12 - 8*min(1,windSpeed))) + vn / 4) / clamp(fresnel, 0.5, 1);

            // Droplet noise
            float f = 0;
            if (skyExposed > 0) {
                vec2 uv = localPos.xz * (5 + noise1/20000.0);
                f = dropletnoise(uv);
            }

            // Specular reflection
            #if SHADOWQUALITY == 0
            if (skyExposed > 0) {
            #endif
                vec3 noisepos = vec3(localPos.x , localPos.z, waterWaveCounter / 8 + windWaveCounter / 6);
                float dy = noise2 / 20 + clamp(gnoise(noisepos) / 10, 0, 0.6);
                
                vec3 normal = normalize(vec3(dy, 1, -dy));
                float upness = max(0, dot(fragNormal, vec3(0,1,0)));
                
                vec3 eye = normalize(vec3(fWorldPos.x, fWorldPos.y - 2, fWorldPos.z));
                vec3 reflectionVec = reflect(sunPosRel, normal);
                float p = dot(reflectionVec, eye);
                if (p > 0) {
                    float sunb = clamp(sunPosRel.y * 10, 0, 1) * clamp(1.5 - sunPosRel.y, 0, 1) * sunSpecularIntensity;
                    
                    // Sun glints require both sky access and current sky radiance.
                    sunb *= clamp(dot(drtSunEffectColor(), DRT_LUMINANCE), 0.0, 1.0);

                    float specular = pow(p, 50) * sunb;
                    
                    #if SHADOWQUALITY > 0
                    float weight = upness * clamp(specular * clamp(pow(shadowBright, 4), 0, 1) * clamp(1.5 * shadowIntensity, 0, 1), 0, 1);
                    #else
                    float weight = upness * clamp(specular * clamp(pow(shadowBright, 4), 0, 1) * clamp(1.5, 0.0, 1.0), 0, 1);
                    #endif
                    
                    vec3 sunColf = reflectColor;
                    
                    texColor.rgb = mix(texColor.rgb, sunColf + noise1 * 0.2, weight);
                    texColor.a = mix(texColor.a, texColor.a + specular/2, weight);
                }
            #if SHADOWQUALITY == 0
            }
            #endif
            
            // SheyderMod: Water Ripples (analytic entity/player wake)
            texColor.rgb *= 1 + f;
            texColor.a += max(0, 0.5 - texColor.a)*0.5*f;

            float w = clamp(rfield * RP_FOAM, -RP_FOAMDN, RP_FOAMUP) * bright;
            texColor.rgb *= 1.0 + w;
            texColor.a += max(0, 0.5 - texColor.a)*0.5*max(0.0, w);

            float dens = clamp(rdens * RP_DENS, 0.0, RP_DENSMAX) * bright;
            texColor.rgb *= 1.0 + dens;
            texColor.a += max(0, 0.5 - texColor.a) * 0.5 * dens;
        }
    }
    
#else
    if (isLava) {
        texColor.a=1;
        accuWeight=1;
    }
#endif
    
    // Surface coverage remains material-authored, even in dense weather.
    texColor = drtApplySurfaceFog(texColor, fWorldPos.xyz, gl_FragCoord.z, true, sm_voxSunLight);

    OIT(texColor, glowLevel);
}
