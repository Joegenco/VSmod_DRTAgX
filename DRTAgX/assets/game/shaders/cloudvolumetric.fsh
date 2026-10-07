#version 330 core
#extension GL_ARB_explicit_attrib_location: enable

// Extend cloud concealment to four times terrain range before shared fog includes.
#define DRT_BOUNDARY_DISTANCE_SCALE 4.0

uniform mat4 iMvpMatrix;
uniform sampler2D depthTex;
uniform sampler2D cloudMap;
uniform sampler2D cloudCol;
uniform sampler2D liquidDepth;
uniform float cloudMapWidth;
uniform vec3 cloudOffset;
uniform int frame;
uniform float time;
uniform int FrameWidth;
uniform float PerceptionEffectIntensity;

in vec2 uv;
in vec2 ndc;

#include oit.fsh
#include drtagx_cloud_lighting.fsh

vec3 hash(vec3 p){

    // https://www.shadertoy.com/view/XlXcW4
    
    // The MIT License
    // Copyright 2017 Inigo Quilez
    // Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated documentation files (the "Software"), to deal in the Software without restriction, including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so, subject to the following conditions: The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software. THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.

    const uint k = 1103515245U;
    uvec3 x = floatBitsToUint(p);
    x = ((x>>8U)^x.yzx)*k;
    x = ((x>>8U)^x.yzx)*k;
    x = ((x>>8U)^x.yzx)*k;
    return vec3(x)/float(0xffffffffU);

}

float noise(vec3 p){
    vec3 f = smoothstep(0.0, 1.0, fract(p));
    vec3 x = floor(p);
    return mix(mix(mix(hash(x + vec3(0, 0, 0)).x,
                       hash(x + vec3(1, 0, 0)).x, f.x),
                   mix(hash(x + vec3(0, 1, 0)).x,
                       hash(x + vec3(1, 1, 0)).x, f.x), f.y),
               mix(mix(hash(x + vec3(0, 0, 1)).x,
                       hash(x + vec3(1, 0, 1)).x, f.x),
                   mix(hash(x + vec3(0, 1, 1)).x,
                       hash(x + vec3(1, 1, 1)).x, f.x), f.y), f.z);
}

float octave(vec3 p){
    return (noise(p * 2.0) * 0.66 + noise(p * 6.0) * 0.33) * 2.0 - 1.0;
}

mat2 rot(float n){
    return mat2(cos(n), -sin(n), sin(n), cos(n));
}

vec3 warp(vec3 d, float f){
    if(f < 0.0001) return d;
    d.xz *= rot(octave(d * 2.0 + time * 0.05) * f);
    d.xy *= rot(octave(d * 1.5 + time * 0.04) * f);
    d.zy *= rot(octave(d * 1.5 - time * 0.04) * f);
    return normalize(d);
}

vec3 curve(vec3 d, float f){
    d.xy *= rot(d.x * f);
    d.zy *= rot(d.z * f);
    return normalize(d);
}

vec3 unproject(vec4 x){
    return x.xyz / max(x.w, 0.0001);
}

float volume(float o, float d, vec2 m, float t, float f){
    m = (m - o) / d;
    return 1.0 - exp(-max(0.0, min(max(m.x, m.y), t) - max(0.0, min(m.x, m.y))) * f);
}

vec2 intersect(float o, float d, vec2 m){
    m = (m - o) / d;
    float near = min(m.x, m.y);
    float far = max(m.x, m.y);
    if(near > far || far < 0.0) return vec2(-1.0);
    return vec2(max(0.0, near), max(0.0, far - max(0.0, near)));
}

float halfsmooth(float x, float t){
    return x > t ? (x - t / 2.0) : (x * x * x * (1.0 - x * 0.5 / t) / t / t);
}

vec4 traverse(vec3 o, vec3 d, float far, float T, vec3 rayOrigin){

    ivec2 p = ivec2(floor(o.xz));
    ivec2 istep = ivec2(sign(d.xz));
    vec2 tdelta, tmax;
    tdelta.x = 1.0 / max(0.0001, abs(d.x));
    tdelta.y = 1.0 / max(0.0001, abs(d.z));
    tmax.x = (d.x > 0.0 ? floor(o.x) + 1.0 - o.x : o.x - floor(o.x)) * tdelta.x;
    tmax.y = (d.z > 0.0 ? floor(o.z) + 1.0 - o.z : o.z - floor(o.z)) * tdelta.y;
    float t = 0.0;
    vec4 k = vec4(0.0);
    // Fog attenuates the visible cloud contribution, not cloud-on-cloud
    // occlusion. Keep intrinsic transmission separate from fogged coverage.
    float cloudTransmittance = 1.0;
    vec4 cloudBinTransmittance = vec4(1.0);
    ivec2 mapSize = textureSize(cloudMap, 0);

    for(int i = 0; i < 200; i++){

        // texelFetch has no wrap/clamp protection outside the finite cloud map.
        if (any(lessThan(p, ivec2(0))) || any(greaterThanEqual(p, mapSize))) break;

        vec4 map = texelFetch(cloudMap, p, 0);

    if(map.r > 0.0){

            vec4 col = texelFetch(cloudCol, p, 0);

            vec2 slab = drtCloudInterval(o.y + d.y * t, d.y, map.ba,
                min(far, min(tmax.x, tmax.y)) - t);
            float v = 1.0 - exp(-(slab.y - slab.x) * map.r);

            if(v > 0.0){

                float cloudOpacity = col.a * v;

                // Sample inside the occupied slab, not at the tile's empty front edge.
                float sampleDistance = (T + t + 0.5 * (slab.x + slab.y)) * 50.0;
                vec3 worldPos = rayOrigin + d * sampleDistance;
                // Bounded density-gradient relief: four neighboring map texels, no new shadow pass.
                float nx = texelFetch(cloudMap, clamp(p - ivec2(1,0), ivec2(0), mapSize-1), 0).r -
                    texelFetch(cloudMap, clamp(p + ivec2(1,0), ivec2(0), mapSize-1), 0).r;
                float nz = texelFetch(cloudMap, clamp(p - ivec2(0,1), ivec2(0), mapSize-1), 0).r -
                    texelFetch(cloudMap, clamp(p + ivec2(0,1), ivec2(0), mapSize-1), 0).r;
                col.rgb *= drtCloudDirection(normalize(vec3(nx, -0.35, nz)), d);
                DrtFogTransport fog;
                if (drtAtmosphereView.z > 0.7) {
                    fog = drtWaterTransport(length(worldPos - drtAtmosphereCamera.xyz), drtFogColor.rgb, true);
                } else {
                    // Apply the fourfold horizontal horizon fade per occupied slab;
                    // cloud premultiplication and OIT revealage follow its opacity.
                    fog = drtSurfaceAirTransport(worldPos, true);
                }
                col = drtCloudThroughFog(col, fog);
                // Native k is premultiplied. Weight fogged contributions by the
                // unfogged foreground clouds so rear cells cannot fill the fade
                // back in or expose each 50-block volume as a separate band.
                col.rgb *= col.a;
                k += cloudTransmittance * col * v;
                cloudTransmittance *= 1.0 - cloudOpacity;

                float bin = log(halfsmooth((T + t) * 50.0, 500.0) / OIT_BIN_SCALE + 1.0);
                for(int i = 0; i < OIT_BINS; i++){
                    float b = OITbellcurve(bin - float(i));
                    if(i == (OIT_BINS-1) && bin > float(OIT_BINS-1)) b = 1.0;
                    // Each bin retains its own intrinsic prefix. With uniform
                    // fog visibility F this yields 1-F*(1-nativeReveal), rather
                    // than repeatedly reducing opacity inside the product.
                    OITreveal[i] -= cloudBinTransmittance[i] * col.a * v * b;
                    cloudBinTransmittance[i] *= 1.0 - cloudOpacity * b;
                }

            }

            if(cloudTransmittance < 0.01) break;

        }

        if(tmax.x < tmax.y){
            p.x += istep.x;
            t = tmax.x;
            tmax.x += tdelta.x;
        }else{
            p.y += istep.y;
            t = tmax.y;
            tmax.y += tdelta.y;
        }

        if(t > far) break;

    }

    return k;

}

float drtCloudLiquidDepth(vec2 scenePixel) {
    // Derive the mapping from the actual scene/depth textures, including odd
    // target sizes. A depth discontinuity must not become an interpolated ray.
    ivec2 liquidSize = textureSize(liquidDepth, 0);
    ivec2 liquidPixel = clamp(ivec2(scenePixel / vec2(textureSize(depthTex, 0)) *
        vec2(liquidSize)), ivec2(0), max(liquidSize - 1, ivec2(0)));
    return drtAtmosphereScreen.w > 0.5 ? texelFetch(liquidDepth, liquidPixel, 0).r : 1.0;
}

void main(){

    const float cloudTileSize = 50.0;

    vec3 origin = unproject(iMvpMatrix * vec4(ndc, -1.0, 1.0));
    vec3 rayOrigin = origin; // Save render-origin coordinates before cloud-map translation/scaling.
    vec3 direction = normalize(unproject(iMvpMatrix * vec4(ndc, 1.0, 1.0)) - origin);
    vec3 world = unproject(iMvpMatrix * vec4(ndc, texelFetch(depthTex, ivec2(gl_FragCoord), 0).r * 2.0 - 1.0, 1.0));
    // Both depths must describe this pixel's ray. Final window dimensions can
    // differ from the scene target; using them samples unrelated terrain/liquid
    // silhouettes. Fetch one bounded texel, without interpolating nonlinear Z.
    float liquidZ = drtCloudLiquidDepth(gl_FragCoord.xy);
    vec3 liquid = unproject(iMvpMatrix * vec4(ndc, liquidZ * 2.0 - 1.0, 1.0));

    float far = min(
        distance(origin, world),
        distance(origin, liquid)
    );

    direction = curve(direction, 0.07);
    direction = warp(direction, PerceptionEffectIntensity * 0.03);

    float height = max(0.0, cloudOffset.y - origin.y);

    origin.y -= cloudOffset.y;

    vec2 plane = intersect(
        origin.y,
        direction.y,
        vec2(-12.5 - 500 * 0.1, 12.5 + 500.0)
    );

    float near = plane.x;

    if(near < 0.0 || far < near) discard;

    origin += direction * near;
    origin.xz -= cloudOffset.xz;
    origin /= cloudTileSize;
    origin.xz += cloudMapWidth / 2.0;

    far -= near;
    far = min(far, plane.y);
    far = min(far, cloudMapWidth * cloudTileSize / 2.0 - near);
    far /= cloudTileSize;

    outGlow = OITaccumulation0 = OITaccumulation1 = OITaccumulation2 = vec4(0.0);
    OITreveal = outReveal = vec4(1.0);

    vec4 k = traverse(origin, direction, far, plane.x / cloudTileSize, rayOrigin);

    if(k.a <= 0.0) discard;

    k = exp(log(k + 1.0)) - 1.0;

    if(k.a <= 0.0) discard;

    for(int i = 0; i < OIT_BINS; i++)
        OITaccumulate(i, k * (1.0 - OITreveal[i]));

    outReveal = vec4(1.0 - k.a);
    outGlow.a = k.a;
}
