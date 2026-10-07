using System;
using System.Text;
using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace DRTAgX;

/// <summary>Optional 1.1.3 LOD integration; preserve the provider's mesh and material ABI.</summary>
internal static class DistantVistasAssetPatch
{
    private const string Marker = "// DRTAgX Distant Vistas atmosphere v1";
    private static bool _warned;
    internal static bool Installed { get; private set; }

    internal static void Apply(ICoreClientAPI api)
    {
        Installed = false;
        if (!api.ModLoader.IsModEnabled("distantvistas")) return;
        Patch("distantvistas", "lodterrain.fsh", false);
        Patch("distantvistas", "farseer-region.fsh", true);
        // DV copies this overlay into Farseer's domain before shader reload.
        if (api.ModLoader.IsModEnabled("farseer")) Patch("farseer", "region.fsh", true);

        void Patch(string domain, string filename, bool overlay)
        {
            IAsset? asset = api.Assets.TryGet(new AssetLocation(domain, "shaders/" + filename));
            if (asset == null) return;
            string source = asset.ToText();
            if (!TryPatch(source, overlay, out string patched))
            {
                if (!_warned) api.Logger.Warning("[DRT AgX] Unrecognized Distant Vistas shader; provider fallback retained.");
                _warned = true;
                return;
            }
            if (!overlay) Installed = true;
            if (source == patched) return;
            asset.Data = Encoding.UTF8.GetBytes(patched);
            api.Logger.Notification($"[DRT AgX] Distant Vistas shared lighting/fog installed: {domain}:{filename}.");
        }
    }

    internal static bool TryPatch(string source, bool overlay, out string patched)
    {
        patched = source;
        if (source.Contains(Marker, StringComparison.Ordinal)) return true;
        string text = source.Replace("\r\n", "\n");
        const string include = "#include underwatereffects.fsh";
        const string main = "void main()\n{";
        const string ambient = "    terraColor.rgb *= shade * rgbaAmbientIn;\n    terraColor.rgb = clamp(terraColor.rgb, 0.0, 1.0);";
        const string fog = "    terraColor = applyFog(terraColor, fogLevel);\n    terraColor.rgb = applyUnderwaterEffects(terraColor.rgb, murkiness);";
        const string overlayFog = "    terraColor = applyFog(terraColor, fogAmount);";
        const string overlayColor = "    outColor = mix(terraColor, skyColor, fade);\n    outGlow = mix(vec4(0.0), skyGlow, fade);";
        if (!Single(text, include) || !Single(text, main) ||
            !Single(text, overlay ? overlayFog : ambient) || !Single(text, overlay ? overlayColor : fog) ||
            !text.Contains(overlay ? "uniform float dvCoverOn;" : "uniform LOD_TEXTURE_ARRAY lodBlockTextures;", StringComparison.Ordinal)) return false;

        text = text.Replace(include, include + "\n#include drtagx_lod_lighting.fsh\n#include drtagx_terrain_boundary.fsh", StringComparison.Ordinal);
        // Rejected proxies write neither depth nor any MRT attachment, including gap fills.
        text = text.Replace(main, main + "\n    " + Marker + "\n    drtDiscardLodNearBoundary(worldPos.xyz);\n    drtDiscardTerrainBoundary(worldPos.xyz);", StringComparison.Ordinal);
        if (overlay)
        {
            text = text.Replace(overlayFog, """
                    if (drtFogColor.w > 0.5) {
                        // Retain DV's unwalked height palette and provider coverage mask.
                        float rel = yLevel - float(seaLevel);
                        vec3 land = mix(dvLandTint, vec3(0.42, 0.40, 0.37), smoothstep(50.0, 150.0, rel));
                        if (rel < 0.75) land = vec3(0.16, 0.27, 0.36);
                        if (dvCoverOn < 0.5) land = mix(vec3(1.0), colorTint.rgb, min(colorTint.a, 0.12));
                        terraColor = vec4(drtLodMaterialAlbedo(land) * drtLodSkyLight()
                            * drtLodFaceShade(normal, sunPosition, 0.0), 1.0);
                    } else {
                        terraColor.rgb = mix(terraColor.rgb, rgbaFog.rgb, clamp(fogAmount, 0.0, 1.0));
                    }
                """, StringComparison.Ordinal);
            text = text.Replace("if (dist > 1.06) discard;", "if (drtFogColor.w < 0.5 && dist > 1.06) discard;", StringComparison.Ordinal)
                .Replace("if (fade > 0.92) discard;", "if (drtFogColor.w < 0.5 && fade > 0.92) discard;", StringComparison.Ordinal);
            text = text.Replace(overlayColor, SharedOutput("1.0") + " else {\n" + overlayColor + "\n    }", StringComparison.Ordinal);
        }
        else
        {
            text = text.Replace(ambient, """
                    if (drtFogColor.w > 0.5) {
                        // Native DV maps return half brightness at full shadow. Convert
                        // visibility once to DRTAgX's live darkness; preserve cast/facing max.
                        float castShade = 1.0 - drtLodSunShadowDarkness() * clamp(2.0 * (1.0 - shadowBrightness), 0.0, 1.0);
                        float faceShade = drtLodFaceShade(normal, sunPosition, 0.0);
                        terraColor.rgb = drtLodMaterialAlbedo(albedo) * drtLodSkyLight()
                            * drtCombineSunDarkening(castShade, faceShade);
                    } else {
                """ + "\n" + ambient + "\n    }", StringComparison.Ordinal);
            // The shared include's applyFog is a completed-surface pass-through.
            // Keep an explicit native fallback while the atmosphere snapshot is unavailable.
            text = text.Replace(fog, "    if (drtFogColor.w < 0.5) {\n        terraColor.rgb = mix(terraColor.rgb, rgbaFog.rgb, fogLevel);\n        terraColor.rgb = applyUnderwaterEffects(terraColor.rgb, murkiness);\n    }", StringComparison.Ordinal);
            const string fade = "    if (fade > 0.0) {";
            if (!Single(text, fade)) return false;
            text = text.Replace(fade, SharedOutput("outAlpha") + " else if (fade > 0.0) {", StringComparison.Ordinal);
        }
        patched = text;
        return true;
    }

    private static string SharedOutput(string alpha) => """
            if (drtFogColor.w > 0.5) {
                // Forward RGB receives one exposed atmosphere; preserve water/thin alpha.
                float receiverDepth = drtNativeReceiverDepth(worldPos.xyz, gl_FragCoord.z);
        """ + $"\n        outColor = drtApplySurfaceFog(vec4(terraColor.rgb, {alpha}), worldPos.xyz, receiverDepth, true, 1.0);\n        outGlow = vec4(0.0, 0.0, 0.0, 1.0);\n    }}";

    private static bool Single(string text, string anchor)
    {
        int first = text.IndexOf(anchor, StringComparison.Ordinal);
        return first >= 0 && text.IndexOf(anchor, first + anchor.Length, StringComparison.Ordinal) < 0;
    }
}
