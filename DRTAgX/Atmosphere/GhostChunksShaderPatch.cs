using System;
using OpenTK.Graphics.OpenGL4;

// Domain folders intentionally share the mod's root namespace.
// ReSharper disable CheckNamespace
namespace DRTAgX;

/// <summary>Adapt the verified GhostChunks 1.6.9 fragment ABI without replacing its renderer.</summary>
internal static class GhostChunksShaderPatch
{
    internal const string Marker = "// DRTAgX GhostChunks shared lighting v1";

    internal static bool TryPatch(string source, string helper, out string patched)
    {
        patched = source;
        if (source.Contains(Marker, StringComparison.Ordinal)) return true;
        string text = source.Replace("\r\n", "\n", StringComparison.Ordinal);
        const string version = "#version 330 core";
        const string lighting = "vec3 col = base * shade * ambientColor * noise;";
        const string sun = "if (sunShading > 0.0) {";
        const string shadow = "col *= 1.0 - extShadowStrength * (1.0 - sunVis);";
        const string fog = "float fog  = fogLevel(length(vRel), vRel.y);";
        const string edge = "fog = max(fog, (1.0 - edge) * edgeFog);";
        const string scattering = "if (atmosphere > 0.0 && beyond > 0.0)";
        const string finish = "col = mix(col, fogColor.rgb, fog);";
        const string water = "vec3  wcol = mix(col, fogColor.rgb, fres * 0.3);";
        const string specular = "wcol += sunTint * spec * 1.5 * min(1.0, dot(ambientColor, vec3(0.3333)) * 1.5);";
        foreach (string anchor in new[]
                     { version, lighting, sun, shadow, fog, edge, scattering, finish, water, specular })
            if (!Single(text, anchor))
                return false;
        // Refuse unknown output or coordinate contracts, retaining provider bytes.
        if (!text.Contains("outGlow      = vec4(0.0, 0.0, 0.0, 1.0);", StringComparison.Ordinal) ||
            !text.Contains("outGPosition = vec4(eyePos.xyz, fog * 2.0);", StringComparison.Ordinal) ||
            !text.Contains("vec4 eyePos  = view * vec4(vRel, 1.0);", StringComparison.Ordinal)) return false;
        text = text.Replace(version, version + "\n" + Marker + "\n" + helper, StringComparison.Ordinal)
            .Replace(fog, fog + "\n" + """
                                       bool drtShared = drtFogColor.w > 0.5;
                                       DrtFogTransport drtGhostFog = drtClearTransport();
                                       if (drtShared) {
                                           // This provider uses camera-relative coordinates and draws before
                                           // native terrain/liquids. No current-frame water exit exists yet.
                                           if (hd >= drtBoundaryEnd()) discard;
                                           drtGhostFog = drtAtmosphereView.z > 0.7
                                               ? drtWaterTransport(length(vRel), drtFogColor.rgb, true)
                                               : drtSurfaceAirTransport(vRel + drtAtmosphereCamera.xyz, true);
                                           fog = max(DRT_MIN_FOG_OPACITY, 1.0 - dot(drtGhostFog.transmittance, vec3(1.0 / 3.0)));
                                       }
                                       """, StringComparison.Ordinal)
            .Replace(edge, "if (!drtShared) " + edge, StringComparison.Ordinal)
            .Replace(lighting, """
                               // Coarse LOD meshes carry sampled materials, not voxel light or
                               // emission metadata. Match exposed terrain sky/ambient radiance.
                               float drtFace = sunShading > 0.0
                                   ? drtLodFaceShade(n, sunDir, drtLodSunShadowDarkness()) : shade;
                               vec3 col = drtShared ? drtLodMaterialAlbedo(base) * drtLodSkyLight() * noise
                                   : base * shade * ambientColor * noise;
                               """, StringComparison.Ordinal)
            .Replace(shadow, """
                             // Shared terrain combines face and cast darkness by maximum.
                             col *= drtShared ? drtCombineSunDarkening(1.0 - extShadowStrength * (1.0 - sunVis), drtFace)
                                 : 1.0 - extShadowStrength * (1.0 - sunVis);
                             """, StringComparison.Ordinal)
            .Replace(sun, "if (!drtShared && sunShading > 0.0) {", StringComparison.Ordinal)
            .Replace(water,
                "vec3 drtWaterSky = drtShared ? drtSkyBackground(reflect(-V, wn)) : fogColor.rgb;\n        vec3 wcol = mix(col, drtWaterSky, fres * 0.3);",
                StringComparison.Ordinal)
            .Replace(specular,
                "wcol += drtShared ? drtLodSkyLight() * spec * 1.5 : sunTint * spec * 1.5 * min(1.0, dot(ambientColor, vec3(0.3333)) * 1.5);",
                StringComparison.Ordinal)
            .Replace(scattering, "if (!drtShared && atmosphere > 0.0 && beyond > 0.0)", StringComparison.Ordinal)
            .Replace(finish,
                "col = drtShared ? drtApplyTransport(vec4(col, 1.0), drtGhostFog).rgb : mix(col, fogColor.rgb, fog);",
                StringComparison.Ordinal);
        patched = text;
        return true;
    }

    internal static bool Validate(string vertex, string fragment, out string error)
    {
        int program = 0, vs = 0, fs = 0;
        error = string.Empty;
        try
        {
            // Compilation does not change current GL bindings; every temporary
            // object is deleted before the provider sees any modified source.
            program = GL.CreateProgram();
            vs = GL.CreateShader(ShaderType.VertexShader);
            fs = GL.CreateShader(ShaderType.FragmentShader);
            GL.ShaderSource(vs, vertex);
            GL.ShaderSource(fs, fragment);
            foreach (int shader in new[] { vs, fs })
            {
                GL.CompileShader(shader);
                GL.GetShader(shader, ShaderParameter.CompileStatus, out int ok);
                if (ok == 0)
                {
                    error = GL.GetShaderInfoLog(shader);
                    return false;
                }

                GL.AttachShader(program, shader);
            }

            GL.LinkProgram(program);
            GL.GetProgram(program, GetProgramParameterName.LinkStatus, out int linked);
            if (linked == 0) error = GL.GetProgramInfoLog(program);
            return linked != 0;
        }
        finally
        {
            if (program != 0) GL.DeleteProgram(program);
            if (vs != 0) GL.DeleteShader(vs);
            if (fs != 0) GL.DeleteShader(fs);
        }
    }

    private static bool Single(string text, string anchor)
    {
        int first = text.IndexOf(anchor, StringComparison.Ordinal);
        return first >= 0 && text.IndexOf(anchor, first + anchor.Length, StringComparison.Ordinal) < 0;
    }
}