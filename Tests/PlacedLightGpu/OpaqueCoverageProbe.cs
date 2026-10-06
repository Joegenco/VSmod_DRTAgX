#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;

// Execute the entire captured terrain fragment, including late mod patches.
// Controlled vertex inputs isolate material coverage from geometry and lighting.
internal static class OpaqueCoverageProbe
{
    internal static void Run(string captureDirectory)
    {
        using var window = new GameWindow(GameWindowSettings.Default, new NativeWindowSettings {
            StartVisible = false, ClientSize = new Vector2i(16, 16), API = ContextAPI.OpenGL,
            APIVersion = new Version(4, 3), Profile = ContextProfile.Core });
        window.MakeCurrent(); GL.LoadBindings(new OpenTK.Windowing.GraphicsLibraryFramework.GLFWBindingsContext());
        string original = File.ReadAllText(Path.Combine(captureDirectory, "live-terrain.fsh"));
        original = "#version 330 core\n" + Regex.Replace(original, @"(?m)^\s*#version[^\r\n]*", "");
        original = Regex.Replace(original, @"(?m)^\s*#define SHEYDER_DEFERRED[^\r\n]*", "");
        original = original.Replace("#version 330 core\n", "#version 330 core\n#define SHEYDER_DEFERRED 0\n");
        string common = original[original.IndexOf("// Shared vertex/fragment ABI.", StringComparison.Ordinal)..];
        common = common[..(common.IndexOf("#endif // DRT_FOG_TRANSPORT", StringComparison.Ordinal) + "#endif // DRT_FOG_TRANSPORT".Length)];
        var inputs = Regex.Matches(original, @"(?m)^\s*(flat\s+)?in\s+(\w+)\s+(\w+)\s*;")
            .Cast<Match>().GroupBy(m => m.Groups[3].Value).Select(g => g.First()).ToArray();
        int vao = GL.GenVertexArray(), fbo = GL.GenFramebuffer(), ubo = GL.GenBuffer();
        GL.BindVertexArray(vao); GL.BindFramebuffer(FramebufferTarget.Framebuffer, fbo);
        int output = Texture(PixelInternalFormat.Rgba16f, 16, 16, null);
        GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, output, 0);
        GL.DrawBuffer(DrawBufferMode.ColorAttachment0); GL.ReadBuffer(ReadBufferMode.ColorAttachment0);
        GL.Disable(EnableCap.Blend); GL.Disable(EnableCap.DepthTest); GL.Viewport(0, 0, 16, 16);
        GL.BindBufferBase(BufferRangeTarget.UniformBuffer, 10, ubo);
        float[] frame = new float[120], pixels = new float[16 * 16 * 4];
        frame[0] = .2f; frame[1] = .3f; frame[2] = .4f; frame[3] = 1;
        frame[8] = 512; frame[9] = 200; frame[13] = 1; frame[27] = 1;
        frame[32] = frame[33] = 16;
        foreach (int offset in new[] { 36, 52, 76 }) for (int i = 0; i < 4; ++i) frame[offset + i * 5] = 1;
        frame[116] = .001f; frame[117] = frame[119] = 1;
        int groups = 0;
        foreach (float floor in new[] { 0f, .00001f })
        {
            string transport = common.Replace("const float DRT_MIN_FOG_OPACITY = 0.00001;", $"const float DRT_MIN_FOG_OPACITY = {floor.ToString("R", System.Globalization.CultureInfo.InvariantCulture)};");
            var vertex = new StringBuilder("#version 430 core\n" + transport + "\nuniform int probeFlags;\nuniform float probeSun,probeLight;\n");
            foreach (Match input in inputs)
                vertex.AppendLine($"{(input.Groups[1].Success ? "flat " : "") }out {input.Groups[2].Value} {input.Groups[3].Value};");
            vertex.AppendLine("void main(){vec2 p=vec2((gl_VertexID<<1)&2,gl_VertexID&2);gl_Position=vec4(p*2.-1.,0,1);");
            foreach (Match input in inputs)
            {
                string name = input.Groups[3].Value, type = input.Groups[2].Value;
                string value = name switch {
                    "rgba" => "vec4(1)", "rgbaFog" => "vec4(.2,.3,.4,1)", "uv" => "p",
                    "normal" => "vec3(0,0,1)", "worldPos" or "camPos" => "vec4(p*2.-1.,-10.,1)",
                    "vertexPosition" => "vec3(p*2.-1.,-10.)", "renderFlags" => "probeFlags",
                    "voxSunLight" or "sm_voxSunLight" => "probeSun", "nb" => "1.0",
                    "drtSunLight" or "drtSkyLight" => "vec3(probeLight*probeSun)", "blockLight" => "vec3(probeLight)",
                    "drtLocalLight" or "drtVoxelLight" => "vec3(probeLight)",
                    _ => $"{type}(0)"
                };
                // Compute the production metadata in the vertex stage as in terrain.
                if (name == "fogAmount") value = "drtFogOpacity(vec3(p*2.-1.,-10.),true,probeSun)";
                vertex.AppendLine($"{name}={value};");
            }
            vertex.AppendLine("}");
            string fragment = original.Replace("const float DRT_MIN_FOG_OPACITY = 0.00001;", $"const float DRT_MIN_FOG_OPACITY = {floor.ToString("R", System.Globalization.CultureInfo.InvariantCulture)};");
            int program = ProbeShader.Program((ShaderType.VertexShader, vertex.ToString()), (ShaderType.FragmentShader, fragment));
            GL.UseProgram(program); GL.UniformBlockBinding(program, GL.GetUniformBlockIndex(program, "DrtAtmosphere"), 10);
            int atlas = Texture(PixelInternalFormat.Rgba32f, 1, 1, new[] { .5f, .3f, .2f, 1f });
            int depth = GL.GenTexture(); GL.BindTexture(TextureTarget.Texture2D, depth);
            GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.DepthComponent32f, 1, 1, 0, PixelFormat.DepthComponent, PixelType.Float, new[] { 1f });
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureCompareMode, (int)TextureCompareMode.CompareRefToTexture);
            GL.GetProgram(program, GetProgramParameterName.ActiveUniforms, out int uniforms);
            int unit = 0;
            for (int i = 0; i < uniforms; ++i)
            {
                string name = GL.GetActiveUniform(program, i, out _, out ActiveUniformType type);
                if (!type.ToString().Contains("Sampler", StringComparison.Ordinal)) continue;
                GL.Uniform1(GL.GetUniformLocation(program, name), unit);
                GL.ActiveTexture(TextureUnit.Texture0 + unit++);
                GL.BindTexture(TextureTarget.Texture2D, type == ActiveUniformType.Sampler2DShadow ? depth : atlas);
            }
            GL.Uniform2(GL.GetUniformLocation(program, "frameSize"), 16f, 16f);
            GL.Uniform1(GL.GetUniformLocation(program, "shadowIntensity"), 0f);
            GL.Uniform3(GL.GetUniformLocation(program, "lightPosition"), 0f, 0f, 1f);
            foreach (int noCull in new[] { 0, 1 }) foreach (int deferred in new[] { 0, 1 })
            foreach (float flat in new[] { 0f, .032f, .2f }) foreach (float sun in new[] { 0f, .001f, 1f })
            foreach (float light in new[] { 0f, .00001f, .1f })
            {
                frame[6] = flat; frame[7] = -4;
                GL.BindBuffer(BufferTarget.UniformBuffer, ubo); GL.BufferData(BufferTarget.UniformBuffer, frame.Length * 4, frame, BufferUsageHint.DynamicDraw);
                GL.Uniform1(GL.GetUniformLocation(program, "haxyFade"), noCull);
                GL.Uniform1(GL.GetUniformLocation(program, "deferredMode"), deferred);
                GL.Uniform1(GL.GetUniformLocation(program, "alphaTest"), noCull == 1 ? .42f : .001f);
                GL.Uniform1(GL.GetUniformLocation(program, "probeSun"), sun);
                GL.Uniform1(GL.GetUniformLocation(program, "probeLight"), light);
                GL.ClearColor(0, 0, 0, 0); GL.Clear(ClearBufferMask.ColorBufferBit);
                GL.DrawArrays(PrimitiveType.Triangles, 0, 3); GL.ReadPixels(0, 0, 16, 16, PixelFormat.Rgba, PixelType.Float, pixels);
                for (int i = 0; i < pixels.Length; ++i)
                {
                    if (!float.IsFinite(pixels[i])) throw new Exception($"Nonfinite channel {i % 4}: floor={floor}, noCull={noCull}, deferred={deferred}, flat={flat}, sun={sun}");
                    if (i % 4 == 3 && pixels[i] != 1) throw new Exception($"Opaque alpha={pixels[i]} at pixel={i / 4}: floor={floor}, noCull={noCull}, deferred={deferred}, flat={flat}, sun={sun}");
                }
                if (GL.GetError() != ErrorCode.NoError) throw new Exception("Coverage probe GL error");
                ++groups;
            }
            GL.DeleteProgram(program); GL.DeleteTexture(atlas); GL.DeleteTexture(depth);
        }
        GL.DeleteTexture(output); GL.DeleteFramebuffer(fbo); GL.DeleteBuffer(ubo); GL.DeleteVertexArray(vao);
        Console.WriteLine($"PASS {groups} captured terrain fragment groups: {groups * 256} pixels have finite RGB and alpha one, including zero/tiny fog floor, ordinary/no-cull, forward/deferred, flat fog and sunlight zero");
    }

    private static int Texture(PixelInternalFormat format, int width, int height, float[]? values)
    {
        int texture = GL.GenTexture(); GL.BindTexture(TextureTarget.Texture2D, texture);
        if (values == null) GL.TexImage2D(TextureTarget.Texture2D, 0, format, width, height, 0, PixelFormat.Rgba, PixelType.Float, IntPtr.Zero);
        else GL.TexImage2D(TextureTarget.Texture2D, 0, format, width, height, 0, PixelFormat.Rgba, PixelType.Float, values);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
        return texture;
    }
}
