// SheyderMod: Specular sun highlight from a parallel R8 specular atlas, fragment include.
// The specular atlas mirrors the terrain atlas layout, so the fragment's existing uv also
// samples it; pages without data fall back to black, so non-specular pixels cost nothing.
// For specular pixels in direct sun it adds a Blinn-Phong highlight (fixed exponent),
// gated by the sun-only shadow factor and the normal-to-sun angle, faded by fog, tinted
// toward the surface hue, and fed into the bloom mask. Sun height, weather and master
// strength are folded into specularStrength on the CPU.

uniform sampler2D specularTex;
uniform float specularStrength;

uniform float sm_BloomScale;

uniform vec3 sm_eyeOffset;

const float SM_TINT = 0.5;

void sm_applySpecularValue(inout vec3 color, inout float bloomMask, float specMap, vec3 albedo, vec3 N, vec3 wpos,
                           float shadowB, float fog)
{

    if (specMap <= 0.0) return;
    if (fog >= 1.0)     return;

    float NdotL = max(dot(N, sunPosition), 0.0);
    if (NdotL <= 0.0) return;

    float shadowGate = smoothstep(0.7, 0.95, shadowB);
    if (shadowGate <= 0.0) return;

    vec3 V = normalize(sm_eyeOffset - wpos);
    vec3 H = normalize(sunPosition + V);
    float NdotH = max(dot(N, H), 0.0);

    float e = NdotH * NdotH;
    e *= e;
    e *= e;
    e *= e;
    e *= e;
    e *= e;

    float spec = e * smoothstep(0.0, 0.15, NdotL);

    float amount = spec * specMap * specularStrength * shadowGate * (1.0 - fog);

    vec3 tintHue = albedo / max(max(albedo.r, albedo.g), max(albedo.b, 1e-4));
    vec3 glint   = amount * drtSunEffectColor() * mix(vec3(1.0), tintHue, SM_TINT);
    color += glint;

    bloomMask += dot(glint, vec3(0.2126, 0.7152, 0.0722)) * sm_BloomScale;
}

void sm_applySpecular(inout vec3 color, inout float bloomMask, vec2 sampleUv, vec3 albedo, vec3 N, vec3 wpos,
                      float shadowB, float fog)
{
    if (specularStrength <= 0.0) return;
    sm_applySpecularValue(color, bloomMask, texture(specularTex, sampleUv).r, albedo, N, wpos, shadowB, fog);
}
