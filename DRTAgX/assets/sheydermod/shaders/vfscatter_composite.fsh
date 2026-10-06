// SheyderMod: Volumetric fog - composite (included into the engine final pass).
// Bilinearly upscales the half-resolution scatter buffer, applies a depth-aware (bilateral)
// 3x3 tent blur to smooth away raymarch step structure, and adds the result to the
// linear HDR scene before AgX. Native mip/bilateral/validity filtering is intact;
// the display transform owns highlight compression. Gated off by vf_Enabled.

uniform sampler2D vfScatterTex;
uniform sampler2D vfDepthTex;
uniform float     vf_Enabled = 0.0;

// Blur radius in HALF-res texels (the scatter buffer's own grid), 0 = no blur taps at all.
// This used to be a hard-coded 0.5, i.e. a quarter of a FULL-res texel - inside the bilinear
// footprint the upscale already covers, so the blur did next to nothing. Values at or above 1
// actually reach neighbouring scatter texels.
uniform float vf_BlurRadius = 5.0;

const float VF_BLEED_REJECT = 50.0;


float vf_depthWeight(float sd, float cd) { return exp(-abs(sd - cd) * VF_BLEED_REJECT); }

// One blur tap. The kernel weight w0 is cut down by two rejections:
//   * depth, so the shafts do not bleed across a near silhouette, and
//   * the scatter buffer's own alpha, which is 0 only for invalid rays. Sky/cloud
//     background rays now march the bounded shadow segment and carry alpha 1.
//     Invalid texels must not dilute valid fog at a boundary; the depth term alone
//     cannot catch this when non-linear depth puts far receivers close together.
// The alpha rejection needs no threshold: the buffer is filtered, so a tap straddling the
// boundary returns rgb already scaled by the valid fraction and alpha equal to that fraction.
// Carrying the fraction in the WEIGHT (rather than testing it) therefore renormalises exactly,
// and that stays true through the mip chain, which box-filters rgb and a together.
void vf_tap(inout vec3 acc, inout float tw, vec2 uv, vec2 o, float cd, float w0, float lod) {
    vec4  s  = textureLod(vfScatterTex, uv + o, lod);
    float sd = texture(vfDepthTex, uv + o).r;
    float w  = w0 * vf_depthWeight(sd, cd);
    acc += s.rgb * w;
    tw  += w * s.a;
}

vec3 vf_compositeVolumetric(vec3 sceneColor, vec2 uv) {
    if (vf_Enabled < 0.5) return sceneColor;

    vec4 center = texture(vfScatterTex, uv);

    if (center.a < 0.5) return sceneColor;

    vec3 s = center.rgb;

    if (vf_BlurRadius > 0.0) {
        // 3x3 tent (centre 4, axial 2, diagonal 1) over the half-res grid, read from the scatter
        // buffer's MIP CHAIN. Nine taps spaced vf_BlurRadius texels apart would leave gaps between
        // them once the radius grows past ~2 - the result stops getting smoother and starts
        // getting blotchy. Taking each tap at lod = log2(radius) makes it average a box the same
        // size as the spacing, so the kernel stays gapless at any radius and the tap count never
        // has to grow. Skipped entirely at radius 0 (uniform branch), which leaves the raw
        // bilinear upscale.
        float cd    = texture(vfDepthTex, uv).r;
        vec2  texel = vf_BlurRadius / vec2(textureSize(vfScatterTex, 0));
        float lod   = log2(max(vf_BlurRadius, 1.0));

        vec3  acc = vec3(0.0);
        float tw  = 0.0;

        vf_tap(acc, tw, uv, vec2(0.0, 0.0), cd, 4.0, lod);
        vf_tap(acc, tw, uv, vec2( texel.x, 0.0), cd, 2.0, lod);
        vf_tap(acc, tw, uv, vec2(-texel.x, 0.0), cd, 2.0, lod);
        vf_tap(acc, tw, uv, vec2(0.0,  texel.y), cd, 2.0, lod);
        vf_tap(acc, tw, uv, vec2(0.0, -texel.y), cd, 2.0, lod);
        vf_tap(acc, tw, uv, vec2( texel.x,  texel.y), cd, 1.0, lod);
        vf_tap(acc, tw, uv, vec2(-texel.x,  texel.y), cd, 1.0, lod);
        vf_tap(acc, tw, uv, vec2( texel.x, -texel.y), cd, 1.0, lod);
        vf_tap(acc, tw, uv, vec2(-texel.x, -texel.y), cd, 1.0, lod);

        // tw carries the summed VALID fraction, which right at a skyline can be small (the mip
        // box is then mostly unmarched texels). acc shrinks with it, so the ratio stays the
        // correct average of the valid samples; the guard only covers the all-invalid case.
        s = acc / max(tw, 1e-4);
    }

    // Preserve HDR radiance before AgX; its display transform owns highlights.
    return sceneColor + s;
}
