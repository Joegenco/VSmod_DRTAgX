using System;
using System.Text;
using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace DRTAgX;

/// <summary>Share atmospheric transport while retaining each LOD provider's actual shader/mesh ABI.</summary>
internal static class LodFogAssetPatch
{
    private const string Marker = "// DRTAgX shared LOD atmosphere";
    private static bool _warned;
    internal static bool Installed { get; private set; }

    internal static void Apply(ICoreClientAPI api)
    {
        Installed = false;
        // Both released Farseer and the installed ChunkLOD use this asset domain.
        IAsset? asset = api.Assets.TryGet(new AssetLocation("farseer", "shaders/region.fsh"));
        if (asset == null) return;
        string source = asset.ToText();
        if (!TryPatchFragment(source, out string patched))
        {
            if (!_warned) api.Logger.Warning("[DRT AgX] Unrecognized LOD region shader; native LOD lighting/fog/range retained.");
            _warned = true;
            return;
        }
        Installed = true;
        if (source == patched) return;
        asset.Data = Encoding.UTF8.GetBytes(patched);
        api.Logger.Notification("[DRT AgX] ChunkLOD/Farseer shared terrain lighting and atmospheric fog installed.");
    }

    internal static bool TryPatchFragment(string source, out string patched)
    {
        patched = source;
        bool existingFog = source.Contains(Marker, StringComparison.Ordinal);
        if (existingFog && source.Contains(LodLightingAssetPatch.Marker, StringComparison.Ordinal))
        {
            // Upgrade existing in-memory patches too, so a shader reload cannot
            // retain the old gap-fill/depth overlap inside the native view range.
            if (!TryAddNearBoundary(RemoveShadowDither(source), out string upgraded)) return false;
            patched = upgraded;
            return true;
        }
        string text = source.Replace("\r\n", "\n");
        const string include = "#include underwatereffects.fsh";
        const string main = "void main()\n{";
        if (!Single(text, include) || !Single(text, main) || !text.Contains("#include fogandlight.fsh", StringComparison.Ordinal)) return false;
        bool chunkLod = text.Contains("uniform float lodFogIntensity;", StringComparison.Ordinal);
        string weather = chunkLod ? """
                float flatFog = clamp(1.0 - 1.0 / exp((vWorldAbs.y - lodFlatFogStart) * lodFlatFogDensity), 0.0, 1.0);
                float lodFog  = clamp(max(flatFog, fogAmount), 0.0, 1.0) * clamp(lodFogIntensity, 0.0, 1.0);
                terraColor.rgb = mix(terraColor.rgb, rgbaFog.rgb, lodFog);
            """ : """
                terraColor = applyFog(terraColor, fogAmount);
                terraColor = applySpheresFog(terraColor, fogAmount, worldPos.xyz);
            """;
        string outputs = chunkLod ? """
                outColor = vec4(mix(terraColor.rgb, skyColor.rgb, edgeFade), terraColor.a);
                outGlow = mix(terraGlow, skyGlow, edgeFade);
            """ : """
                outColor = mix(terraColor, skyColor, fade);
                outGlow = mix(terraGlow, skyGlow, fade);
            """;
        const string oldClip = "    if (dist < 0.0 || dist > 1.0) discard;";
        string fallbackWeather = chunkLod ? weather : """
                terraColor.rgb = mix(terraColor.rgb, rgbaFog.rgb, clamp(fogAmount, 0.0, 1.0));
                terraColor = applySpheresFog(terraColor, fogAmount, worldPos.xyz);
            """;
        if (!Single(text, existingFog ? fallbackWeather : weather) || !Single(text, outputs) || (!chunkLod && !existingFog && !Single(text, oldClip))) return false;
        if (!LodLightingAssetPatch.TryPatch(text, chunkLod, out text)) return false;
        text = RemoveShadowDither(text);
        // Upgrade an already fog-patched asset once; never wrap its transport or
        // boundary a second time on a shader reload.
        if (existingFog) return TryAddNearBoundary(text, out patched);

        // Keep native branches statically live: the provider still sets their
        // uniform keys, and they remain the fallback before atmosphere publication.
        text = text.Replace(include, include + "\n#include drtagx_terrain_boundary.fsh", StringComparison.Ordinal);
        text = text.Replace(main, main + "\n    " + Marker + "\n    drtDiscardTerrainBoundary(worldPos.xyz);", StringComparison.Ordinal);
        // The shared applyFog helper intentionally passes completed surface RGB
        // through. Spell out Farseer's native fallback blend so fogAmount and
        // its fogMinIn/rgbaFogIn setters remain active after GLSL optimization.
        text = text.Replace(weather, "    if (drtFogColor.w < 0.5) {\n" + fallbackWeather + "\n    }", StringComparison.Ordinal);
        text = text.Replace(outputs, """
                if (drtFogColor.w > 0.5) {
                    // worldPos retains real distance even when LOD projection is compressed.
                    // Sky-overlay alpha is not terrain coverage; LOD is completed forward RGB.
                    float receiverDepth = drtNativeReceiverDepth(worldPos.xyz, gl_FragCoord.z);
                    outColor = drtApplySurfaceFog(vec4(terraColor.rgb, 1.0), worldPos.xyz, receiverDepth, true, 1.0);
                    outGlow = vec4(0.0, 0.0, 0.0, 1.0);
                } else {
            """ + "\n" + outputs + "\n    }", StringComparison.Ordinal);
        if (!chunkLod)
            // Farseer's old normalized-distance cutoff ends 512 blocks early.
            // Shared concealment now owns the far cutoff; its near handoff stays native.
            text = text.Replace(oldClip, "    if (dist < 0.0 || (drtFogColor.w < 0.5 && dist > 1.0)) discard;", StringComparison.Ordinal);
        return TryAddNearBoundary(text, out patched);
    }

    private static bool TryAddNearBoundary(string source, out string patched)
    {
        patched = source;
        const string call = "    drtDiscardLodNearBoundary(worldPos.xyz);";
        if (source.Contains(call, StringComparison.Ordinal)) return true;
        const string main = "void main()\n{";
        string text = source.Replace("\r\n", "\n");
        if (!Single(text, main)) return false;
        // This entry gate also precedes provider gap-fill, seam and depth passes.
        patched = text.Replace(main, main + "\n" + call, StringComparison.Ordinal);
        return true;
    }

    private static string RemoveShadowDither(string text) =>
        // The recognized ChunkLOD height-map march starts at a fixed interval
        // midpoint. Keep world-space material texture variation independent.
        text.Replace("float jitter = fract(52.9829189 * fract(dot(gl_FragCoord.xy, vec2(0.06711056, 0.00583715))));",
            "// Fixed shadow-march midpoint; no screen-pixel jitter.", StringComparison.Ordinal)
            .Replace("float t = 2.0 + (jitter * stepLen);", "float t = 2.0 + 0.5 * stepLen;", StringComparison.Ordinal);

    private static bool Single(string text, string anchor)
    {
        int first = text.IndexOf(anchor, StringComparison.Ordinal);
        return first >= 0 && text.IndexOf(anchor, first + anchor.Length, StringComparison.Ordinal) < 0;
    }
}
