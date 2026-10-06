// SheyderMod: WorldFog (atmospheric distance fog), fragment include.
// Blends each pixel toward a fog colour with distance (fog = 1 - exp(-fallOff*dist),
// beginning past a fade-start distance). The fog colour is an azimuthal gradient ring
// computed per frame on the CPU: warm toward the sun, cool on the opposite side, with
// adjustable sun-cone sharpness and saturation. Early-outs when off or in the near
// clear zone.

#include drtagx_fog_transport.ash
uniform vec3  wf_ColSun;
uniform vec3  wf_ColOpp;
uniform vec3  wf_SunAz;
uniform float wf_SunScatterExp;
uniform float wf_Saturation;

vec3 wf_fogColor(vec3 viewDir) {

    vec3  vh  = vec3(viewDir.x, 0.0, viewDir.z);
    float vhl = length(vh);
    vh = (vhl > 1e-4) ? vh / vhl : vec3(1.0, 0.0, 0.0);

    float t = 0.5 + 0.5 * dot(vh, wf_SunAz);

    t = pow(clamp(t, 0.0, 1.0), wf_SunScatterExp);

    vec3 col = mix(wf_ColOpp, wf_ColSun, t);

    float luma = dot(col, vec3(0.2126, 0.7152, 0.0722));
    return max(vec3(0.0), mix(vec3(luma), col, wf_Saturation));
}

vec4 applyWorldFog(vec4 color, vec3 worldPos) {
    // Updated atmosphere callers integrate this effect into shared extinction.
    // Legacy callers retain bounded attenuation instead of additive brightness.
    float distance = length(worldPos);
    float opacity = clamp(wf_Density * wf_Intensity *
        (1.0 - exp(-max(wf_FallOff, 0.0) * max(distance - wf_FadeStart, 0.0))), 0.0, 1.0);
    return vec4(mix(color.rgb, wf_fogColor(worldPos / max(distance, 0.0001)), opacity), color.a);
}
