#version 330 core

// SheyderMod: Water screen-space reflections - reflection generation.
// Reflects the view ray about the flat water surface and marches it in screen space
// against the opaque scene depth to build a clean (un-rippled) mirror image into a
// low-resolution buffer. Ray hits sample the scene colour; misses fall back to the
// mirrored sky, then to the player-centred environment cubemap, then to a flat sky
// colour so there is no dark cone. Reuses the engine's LiquidDepth and Primary
// buffers; ripples, blur and Fresnel come later in ssrcomposite.fsh.

uniform sampler2D liquidDepth;
uniform sampler2D sceneDepth;
uniform sampler2D sceneColor;

// Environment cubemap (ssrenvcapture.fsh) holding the distant scene by world direction,
// accumulated out of earlier frames. rgb is PREMULTIPLIED by the confidence in a, so it
// composites straight over the flat sky colour.
uniform samplerCube ssr_envCube;

uniform mat4 ssr_proj;
uniform mat4 ssr_invProj;
uniform mat4 ssr_modelView;
uniform mat4 ssr_invModelView;

uniform int   ssr_steps;
uniform float ssr_landSaturation;
uniform vec3  ssr_skyColor;
uniform float ssr_envStrength;   // 0 = cubemap off, and the sample below is never taken

// How far (screen uv) a directly visible water pixel may be for OCCLUDED water to still be
// traced. See visibleWaterOnRing below. Sized on the CPU from everything that pulls data
// sideways out of this buffer (blur taps, TAA neighbourhood, composite wave distortion).
uniform vec2  ssr_gateRadius;

in  vec2 texcoord;
out vec4 outColor;

vec3 nvec3(vec4 p){ return p.xyz / p.w; }
vec4 nvec4(vec3 p){ return vec4(p, 1.0); }

float edgeFade(vec2 c){
    float x  = max(abs(c.x - 0.5), abs(c.y - 0.5)) * 2.0;
    float x2 = x * x;
    float x4 = x2 * x2;
    return clamp(1.0 - x4 * x4, 0.0, 1.0);
}

float linDepth(float w){ return ssr_proj[3].z / (ssr_proj[2].z + 2.0 * w - 1.0); }

// Ring offsets, diagonals scaled so all eight land on the same radius.
const float GATE_DIAG = 0.70710678;
const vec2 GATE_DIRS[8] = vec2[8](
    vec2( 1.0, 0.0), vec2(-1.0, 0.0), vec2( 0.0, 1.0), vec2( 0.0, -1.0),
    vec2( GATE_DIAG,  GATE_DIAG), vec2(-GATE_DIAG,  GATE_DIAG),
    vec2( GATE_DIAG, -GATE_DIAG), vec2(-GATE_DIAG, -GATE_DIAG)
);

// Finds a DIRECTLY VISIBLE water pixel on a ring of radius ssr_gateRadius; returns its uv, or
// vec2(-1) if the whole ring is occluded / dry.
//
// Water hidden behind an occluder is still in liquidDepth (the engine renders the liquid pass
// into its own cleared buffer, so nothing -- terrain, entities, not even a boat's hide-water
// mesh -- can remove it). Its reflection is therefore computable, and it has to be: the blur,
// the TAA neighbourhood and the composite's wave distortion all read sideways across the
// occluder silhouette, and every texel we leave at zero there punches a hole into the visible
// water next to it. So we trace the rim of the occluded region -- as far out as anything can
// reach -- and leave the interior, which nothing samples, alone.
//
// Up to 16 fetches, returning on the first hit, and only ever for occluded texels. Filtered
// samples are fine here: liquidDepth is a QUARTER-res buffer sampled GL_LINEAR, so its edges
// are already a few pixels soft, and the rim this sizes is tens of pixels wide. A miss by half
// a texel changes nothing; an exact texelFetch would only buy false precision.
vec2 visibleWaterOnRing(vec2 uv){
    for (int i = 0; i < 8; i++){
        // Clamped to the screen: sampling past the edge is meaningless (the samplers clamp
        // anyway), and it keeps every returned uv inside [0,1] so a negative x is unambiguously
        // the miss below. Near an edge some directions collapse onto the same point, which only
        // costs a redundant test.
        vec2  p = clamp(uv + GATE_DIRS[i] * ssr_gateRadius, 0.0, 1.0);
        float w = texture(liquidDepth, p).r;
        if (w < 0.99999 && w < texture(sceneDepth, p).r) return p;
    }
    return vec2(-1.0);
}

vec4 raytrace(vec3 startView, vec3 rayDir, vec2 uv0, float z0, out vec4 skyHit){
    skyHit = vec4(0.0);

    float tMax = 100000.0;
    if (rayDir.z > 0.0) tMax = min(tMax, (-0.1 - startView.z) / max(rayDir.z, 1e-6));
    vec4 clip1 = ssr_proj * nvec4(startView + rayDir * tMax);
    if (clip1.w <= 0.0) return vec4(0.0);
    vec3 end = nvec3(clip1) * 0.5 + 0.5;

    vec2  duv = end.xy - uv0;
    float dz  = end.z  - z0;

    float sEnd = 1.0;
    if (duv.x >  1e-6) sEnd = min(sEnd, (1.0 - uv0.x) / duv.x);
    if (duv.x < -1e-6) sEnd = min(sEnd,       -uv0.x  / duv.x);
    if (duv.y >  1e-6) sEnd = min(sEnd, (1.0 - uv0.y) / duv.y);
    if (duv.y < -1e-6) sEnd = min(sEnd,       -uv0.y  / duv.y);
    if (dz    >  1e-9) sEnd = min(sEnd, (1.0 - z0) / dz);

    float stepS  = sEnd / float(ssr_steps);
    // Use a fixed first step: no pixel hash perturbs reflection-ray coverage.

    float sPrev = 0.0;
    float s     = stepS;
    for (int i = 0; i < ssr_steps; i++){
        if (s > sEnd) break;
        vec2  uv     = uv0 + duv * s;
        float rayZ   = z0  + dz  * s;
        float sceneZ = texture(sceneDepth, uv).r;

        if (rayZ > sceneZ && sceneZ > z0){

            float a = sPrev, b = s;
            vec2  huv  = uv;
            float hitZ = sceneZ;
            for (int j = 0; j < 6; j++){
                float m  = (a + b) * 0.5;
                vec2  mu = uv0 + duv * m;
                float mz = texture(sceneDepth, mu).r;
                if (z0 + dz * m > mz && mz > z0){ b = m; huv = mu; hitZ = mz; }
                else a = m;
            }

            float linScene = linDepth(hitZ);
            if (linDepth(z0 + dz * b) - linScene < 0.5 + 0.03 * linScene){
                return vec4(texture(sceneColor, huv).rgb, edgeFade(huv));
            }
        }
        sPrev = s;
        s += stepS;
    }

    vec2 uvEnd = uv0 + duv * sEnd;
    if (texture(sceneDepth, uvEnd).r > z0){
        skyHit = vec4(texture(sceneColor, uvEnd).rgb, edgeFade(uvEnd));
    }
    return vec4(0.0);
}

void main(void){

    float wd = texture(liquidDepth, texcoord).r;
    if (wd >= 0.99999){ outColor = vec4(0.0); return; }
    float sd = texture(sceneDepth, texcoord).r;
    // Occluded water is traced only on the rim, where visible water can still sample it; the
    // composite keeps its own wd >= sd test, so none of it is ever drawn directly. Visible
    // water (the common case) short-circuits past the ring entirely.
    // waterUv is where this texel's own water surface is on screen -- itself when visible, the
    // ring hit when not, since an occluded texel has an occluder at texcoord and no water.
    vec2 waterUv = texcoord;
    if (wd >= sd){
        waterUv = visibleWaterOnRing(texcoord);
        if (waterUv.x < 0.0){ outColor = vec4(0.0); return; }
    }

    vec3 viewPos = nvec3(ssr_invProj * nvec4(vec3(texcoord, wd) * 2.0 - 1.0));
    vec3 upView  = mat3(ssr_modelView) * vec3(0.0, 1.0, 0.0);
    vec3 viewR   = reflect(normalize(viewPos), upView);

    vec4 skyHit;
    vec4 hit = raytrace(viewPos, viewR, texcoord, wd, skyHit);

    float hitLuma = dot(hit.rgb, vec3(0.2126, 0.7152, 0.0722));
    hit.rgb = max(vec3(0.0), mix(vec3(hitLuma), hit.rgb, ssr_landSaturation));

    const float SKY_VALIDITY = 0.25;

    // Cubemap fallback. This is what the march structurally cannot deliver: look down at the
    // water and the reflected ray points up and BEHIND the camera, so raytrace bails on clip.w
    // before it takes a single step and skyHit stays empty. The cube still holds that direction
    // from whenever the player last had it on screen.
    // Uniform branch, so with the feature off the samplerCube is never touched at all.
    vec3  envCol      = ssr_skyColor;
    float envValidity = SKY_VALIDITY;
    if (ssr_envStrength > 0.0){
        vec3  worldR = mat3(ssr_invModelView) * viewR;
        vec4  env    = texture(ssr_envCube, worldR);
        float envA   = clamp(env.a, 0.0, 1.0) * ssr_envStrength;
        // Premultiplied "over": env.rgb already carries its own confidence factor, so the sky
        // only fills whatever confidence is missing -- a half-filled direction reads as a
        // half-blend towards the sky, never as a dark patch.
        envCol      = env.rgb * ssr_envStrength + ssr_skyColor * (1.0 - envA);
        envValidity = mix(SKY_VALIDITY, 1.0, envA);
    }

    // On-screen data still wins over the cube: skyHit has real parallax, the cube does not.
    vec3  skyCol      = mix(envCol,      skyHit.rgb, skyHit.a);
    float skyValidity = mix(envValidity,        1.0, skyHit.a);
    vec3  col      = mix(skyCol,      hit.rgb, hit.a);
    float validity = mix(skyValidity, 1.0,     hit.a);

    // Tint by the water this reflection sits on. Sampling at texcoord on an occluded texel would
    // read the OCCLUDER (hull, reed) and stain the fill with it; sampling the ring hit instead
    // takes the colour of the same water body a few pixels over, so the rim carries the same
    // tint as the visible water it feeds and blends into it without a brightness seam.
    vec3 waterColor = texture(sceneColor, waterUv).rgb;
    col *= mix(vec3(1.0), waterColor, 0.2);

    outColor = vec4(col, validity);
}
