// SheyderMod: SubSurface backlight (fake translucency for leaves and grass), fragment
// include. Adds a warm glow to foliage when the view direction points toward the sun:
// a sun cone (view . sun) shaped by a power falloff, gated by the sun-shadow term so
// only sunlit foliage lights up, and faded by weather, sun elevation, shadow-map
// strength (tl_EnvGate, folded on the CPU: the engine stops rendering the shadow maps
// under heavy overcast, so the backlight must fade out with DropShadowIntensity or the
// stale map reads "fully sunlit") and distance to the shadow-map caster range. Also
// feeds its luminance into the bloom mask. Provides
// value- and varying-based entry points so both the forward chunk shaders and the
// deferred relight can apply the identical backlight.

in float tl_isWind;

uniform float tl_Strength;
uniform float tl_Power;
uniform float tl_ConeMin;
uniform float tl_ConeMinGrass;
uniform float tl_PowerGrass;
uniform float tl_EnvGate;
uniform float tl_ShadowRangeFar;

uniform float tl_BloomScale;

uniform vec3 tl_eyeOffset;

#if SHADOWQUALITY > 0

vec3 tl_backlight(vec3 smoothBase, vec3 wpos, float shadowB, float blockBright, float windFade, float coneMin, float power) {

    vec3 viewDir = normalize(wpos - tl_eyeOffset);

    float camSun    = max(0.0, dot(viewDir, sunPosition));
    float coneFade  = smoothstep(coneMin, 1.0, camSun);
    float spot      = pow(coneFade, power);

    float occ = clamp((1.0 - shadowB) / max(shadowIntensity, 0.05), 0.0, 1.0);
    float lit = 1.0 - smoothstep(0.1, 0.35, occ);

    float vertFade = smoothstep(0.1, 0.5, windFade);

    float casterDist = tl_ShadowRangeFar * 0.5;

    float camDist  = length(wpos);
    float distFade = 1.0 - smoothstep(casterDist * 0.8, casterDist, camDist);

    float amount = spot * tl_Strength * tl_EnvGate * lit * vertFade * distFade;

    return smoothBase * amount * drtSunEffectColor();
}

void tl_applyBacklightValue(inout vec3 color, inout float bloomMask, vec3 smoothBase, vec3 wpos,
                            float shadowB, float blockBright, float isWind) {

    if (tl_Strength > 0.001 && tl_EnvGate > 0.001 && isWind > 0.001) {
        bool  isGrass = isWind < 0.75;
        float coneMin = isGrass ? tl_ConeMinGrass : tl_ConeMin;
        float power   = isGrass ? tl_PowerGrass   : tl_Power;
        vec3  add     = tl_backlight(smoothBase, wpos, shadowB, blockBright, isWind, coneMin, power);
        color += add;

        bloomMask += dot(add, vec3(0.2126, 0.7152, 0.0722)) * tl_BloomScale;
    }
}

void tl_applyBacklight(inout vec3 color, inout float bloomMask, vec3 smoothBase, vec3 wpos, float shadowB) {
    tl_applyBacklightValue(color, bloomMask, smoothBase, wpos, shadowB, blockBrightness, tl_isWind);
}
#endif
