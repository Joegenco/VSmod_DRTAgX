using System;
using OpenTK.Graphics.OpenGL4;

// Domain folders intentionally share the mod root namespace.
// ReSharper disable CheckNamespace
namespace DRTAgX;

/// <summary>Atomic source adaptation and temporary GL validation for the optional rain provider.</summary>
internal static class RainCurtainsShaderPatch
{
    internal const string Marker = "// DRTAgX RainCurtains HDR atmosphere v1";

    internal static bool TryPatchSources(string vertex, string fragment, string helper,
        out string patchedVertex, out string patchedFragment)
    {
        patchedVertex = vertex;
        patchedFragment = fragment;
        bool vertexAdapted = vertex.Contains(Marker, StringComparison.Ordinal),
            fragmentAdapted = fragment.Contains(Marker, StringComparison.Ordinal);
        if (vertexAdapted || fragmentAdapted) return vertexAdapted && fragmentAdapted;
        string text = fragment.Replace("\r\n", "\n", StringComparison.Ordinal);
        string vertexText = vertex.Replace("\r\n", "\n", StringComparison.Ordinal);
        const string version = "#version 330 core";
        const string varying = "flat out vec3 cloudTint;";
        const string ray = "shownRel = pos - camRelPos;";
        const string cloudFog = "tint = mix(tint, rgbaFogIn.rgb, fogAmount * TERRAIN_FOG * (1.0 - merge));";
        const string simpleFog = "tint = rgbaFogIn.rgb * mix(darken, 1.0, cloudFogAmount);";
        const string rainbow = "return clamp(tint * (1.0 - dark * k) + light * k, 0.0, 1.0);";
        const string dither =
            "tint = clamp(tint + (dither - 0.5) * DITHER_STRENGTH / 255.0 / max(alpha, 0.02), 0.0, 1.0);";
        foreach (string anchor in new[] { version, cloudFog, simpleFog, rainbow, dither })
            if (!Single(text, anchor))
                return false;
        if (!Single(vertexText, varying) || !Single(vertexText, ray) || !Single(vertexText, version)) return false;
        if (!text.Contains("vec3 rainColour = cloudTint * mix(shade, 1.0, cloudFade);", StringComparison.Ordinal) ||
            !text.Contains("outGlow = vec4(0.0, 0.0, 0.0, alpha);", StringComparison.Ordinal)) return false;
        // The atmosphere's chroma is smooth over the provider's fourteen curtain
        // rows. Resolve its two LUT reads per vertex rather than every veil pixel.
        vertexText = vertexText.Replace(varying,
                varying + "\n" + Marker + "\nuniform vec4 rgbaFogIn;\nout vec3 drtRainFog;\n" + helper,
                StringComparison.Ordinal)
            .Replace(ray, ray + "\n    drtRainFog = drtRainCurtainFog(rgbaFogIn.rgb, shownRel);",
                StringComparison.Ordinal);
        text = text.Replace(version, version + "\n" + Marker + "\nin vec3 drtRainFog;\n" + helper,
                StringComparison.Ordinal)
            // cloudTint already contains DRTAgX cloudmap's drtCloudLight plus
            // additive lightning. Adapt only the separately supplied native fog.
            .Replace(cloudFog, "tint = mix(tint, drtRainFog, fogAmount * TERRAIN_FOG * (1.0 - merge));",
                StringComparison.Ordinal)
            .Replace(simpleFog, "tint = drtRainFog * mix(darken, 1.0, cloudFogAmount);", StringComparison.Ordinal)
            .Replace(rainbow,
                "vec3 result = tint * (1.0 - dark * k) + light * k;\n    return drtFogColor.w > 0.5 ? max(result, vec3(0.0)) : clamp(result, 0.0, 1.0);",
                StringComparison.Ordinal)
            // The shared scene is floating-point HDR. Keep the provider's exact
            // SDR quantization treatment only before atmosphere publication.
            .Replace(dither,
                "tint = drtFogColor.w > 0.5 ? max(tint, vec3(0.0)) : clamp(tint + (dither - 0.5) * DITHER_STRENGTH / 255.0 / max(alpha, 0.02), 0.0, 1.0);",
                StringComparison.Ordinal);
        patchedVertex = vertexText;
        patchedFragment = text;
        return true;
    }

    internal static bool Validate(string vertex, string fragment, out string error)
    {
        int program = 0, vs = 0, fs = 0;
        error = string.Empty;
        try
        {
            // Shader compilation/linking changes no framebuffer, buffer, texture,
            // viewport or active-program binding; delete every temporary GL object.
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
