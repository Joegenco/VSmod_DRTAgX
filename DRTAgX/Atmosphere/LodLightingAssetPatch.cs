using System;

namespace DRTAgX;

/// <summary>Replace recognized provider lighting with shared terrain radiance, retaining materials and fallbacks.</summary>
internal static class LodLightingAssetPatch
{
    internal const string Marker = "// DRTAgX shared LOD lighting v2";

    internal static bool TryPatch(string source, bool chunkLod, out string patched)
    {
        patched = source;
        const string include = "#include underwatereffects.fsh";
        const string main = "void main()\n{";
        string material = chunkLod ? """
                terraColor.rgb = mix(terraColor.rgb, finalColor, 0.90);
                terraColor.rgb = mix(terraColor.rgb, colorTint.rgb, colorTint.a * 0.45);
            """ : "    terraColor.rgb = mix(terraColor.rgb, colorTint.rgb, colorTint.a);";
        string lighting = chunkLod ? """
                terraColor.rgb *= clamp(sunColor, 0.0, 1.0) * bias(clamp(dayLight, 0.0, 1.0), lightLevelBias);

                vec3 sunDirL = normalize(sunPosition);
                float vsNb = max(max(0.45, 0.5 + 0.5 * dot(faceNormal, sunDirL)), faceNormal.y * 0.95);
                terraColor.rgb *= mix(1.0, vsNb, chunkLodFaceLighting);

                {

                    float SHADOW_MAX = 1900.0;
                    float distFade = 1.0 - smoothstep(SHADOW_MAX * 0.8, SHADOW_MAX, distXZ);
                    if (distFade > 0.001) {
                        float sunVis = computeSunVisibility(vWorldAbs, normalize(sunPosition));
                        float SHADOW_DARK = 0.58;
                        float strength = clamp(dayLight * 1.5, 0.0, 1.0) * distFade;
                        terraColor.rgb *= mix(1.0, mix(SHADOW_DARK, 1.0, sunVis), strength);
                    }
                }
            """ : "    terraColor.rgb *= bias(clamp(sunColor * dayLight, 0, 1), lightLevelBias);";
        if (!Single(source, material) || !Single(source, lighting) || !Single(source, include) || !Single(source, main)) return false;

        string authored = chunkLod ? "mix(finalColor, colorTint.rgb, colorTint.a * 0.45)" : "mix(vec3(1.0), colorTint.rgb, colorTint.a)";
        string text = source.Replace(include, include + "\n#include drtagx_lod_lighting.fsh", StringComparison.Ordinal);
        text = text.Replace(main, main + "\n    " + Marker, StringComparison.Ordinal);
        // The shared path lights material albedo, not the provider's displayed
        // sky color. Preserve every native branch/setter until publication.
        text = text.Replace(material, "    if (drtFogColor.w > 0.5) {\n        terraColor.rgb = drtLodMaterialAlbedo(" + authored + ");\n    } else {\n" + material + "\n    }", StringComparison.Ordinal);
        string shared = chunkLod ? """
                    float shadowDarkness = drtLodSunShadowDarkness();
                    float faceShade = mix(1.0, drtLodFaceShade(faceNormal, sunPosition,
                        shadowsEnabled > 0.5 ? shadowDarkness : 0.0), chunkLodFaceLighting);
                    float shadowShade = 1.0;
                    float distFade = 1.0 - smoothstep(1520.0, 1900.0, distXZ);
                    if (distFade > 0.001 && shadowDarkness > 0.0 && shadowsEnabled > 0.5) {
                        // Keep the provider's height-map visibility and its coverage.
                        float sunVis = computeSunVisibility(vWorldAbs, normalize(sunPosition));
                        shadowShade = 1.0 - shadowDarkness * distFade * (1.0 - sunVis);
                    }
                    // Real terrain combines face/cast darkness with a maximum,
                    // avoiding a second darkening where those shadows overlap.
                    terraColor.rgb *= drtLodSkyLight() * drtCombineSunDarkening(shadowShade, faceShade);
            """ : """
                    vec3 faceNormal = cross(dFdx(worldPos.xyz), dFdy(worldPos.xyz));
                    terraColor.rgb *= drtLodSkyLight() * drtLodFaceShade(faceNormal, sunPosition, 0.0);
            """;
        text = text.Replace(lighting, "    if (drtFogColor.w > 0.5) {\n" + shared + "\n    } else {\n" + lighting + "\n    }", StringComparison.Ordinal);
        patched = text;
        return true;
    }

    private static bool Single(string text, string anchor)
    {
        int first = text.IndexOf(anchor, StringComparison.Ordinal);
        return first >= 0 && text.IndexOf(anchor, first + anchor.Length, StringComparison.Ordinal) < 0;
    }
}
