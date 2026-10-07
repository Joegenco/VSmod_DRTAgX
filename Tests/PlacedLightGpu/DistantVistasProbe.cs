using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using DRTAgX;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;

/// <summary>Installed provider ABI, real GL linking and native/LOD depth competition.</summary>
internal static class DistantVistasProbe
{
    internal static void Run(string references, Func<string, int, string> expand, float[] frame, int ubo, int fbo,
        Action<int> bind, Action restore, Func<int, float, float[]> read, Action<Vector3> sky)
    {
        void Check(bool condition, string message) { if (!condition) throw new Exception("Distant Vistas: " + message); }
        void Scalar(int program, string name, float value) => GL.Uniform1(GL.GetUniformLocation(program, name), value);
        void Setup(int program)
        {
            GL.UseProgram(program);
            foreach (string name in new[] { "dayLight", "lightLevelBias", "farViewDistance", "viewDistance", "zFar", "zNear" })
                Scalar(program, name, name switch { "farViewDistance" => 4096, "viewDistance" or "zFar" => 512, "zNear" => .1f, _ => 1f });
            GL.Uniform3(GL.GetUniformLocation(program, "rgbaAmbientIn"), 1f, 1f, 1f);
            GL.Uniform3(GL.GetUniformLocation(program, "sunPosition"), 0f, 1f, 0f);
            GL.Uniform3(GL.GetUniformLocation(program, "sunColor"), 1f, 1f, 1f);
            Scalar(program, "lightLevelBias", .5f);
            GL.Uniform3(GL.GetUniformLocation(program, "dvLandTint"), .4f, .5f, .3f);
            GL.Uniform4(GL.GetUniformLocation(program, "clipRect"), -100000f, -100000f, 100000f, 100000f);
            Scalar(program, "dvCoverOn", 1f); // Zero coverage dimensions leave the mask empty.
            // Model native auto-assignment before DRTAgX's real sampler allocator.
            GL.GetProgram(program, GetProgramParameterName.ActiveUniforms, out int count);
            int unit = 0;
            for (int i = 0; i < count; ++i)
            {
                string name = GL.GetActiveUniform(program, i, out _, out ActiveUniformType type);
                if (!type.ToString().Contains("Sampler", StringComparison.Ordinal) || name.StartsWith("drtSkyView") || name is "drtFogVolume" or "liquidDepth") continue;
                GL.Uniform1(GL.GetUniformLocation(program, name), name == "lodBlockTextures" ? 15 : unit++);
            }
        }

        foreach (bool overlay in new[] { false, true })
        {
            string name = overlay ? "farseer-region" : "lodterrain";
            string original = File.ReadAllText(Path.Combine(references, name + ".fsh"));
            Check(DistantVistasAssetPatch.TryPatch(original, overlay, out string patched), name + " recognized");
            Check(DistantVistasAssetPatch.TryPatch(patched, overlay, out string repeated) && repeated == patched, "idempotent reload");
            string unknown = original.Replace("terraColor.rgb *=", "terraColor.rgb +=", StringComparison.Ordinal);
            if (overlay) unknown = original.Replace("uniform float dvCoverOn;", "uniform float changedCoverage;", StringComparison.Ordinal);
            Check(!DistantVistasAssetPatch.TryPatch(unknown, overlay, out string fallback) && fallback == unknown, "unknown ABI stays unchanged");
            foreach (int ssao in new[] { 0, 1 })
            {
                string vertex = expand(File.ReadAllText(Path.Combine(references, name + ".vsh")), ssao);
                string fragment = expand(patched, ssao);
                int linked = ProbeShader.Program((ShaderType.VertexShader, vertex), (ShaderType.FragmentShader, fragment));
                foreach (Match declaration in Regex.Matches(expand(original, ssao) + vertex, @"uniform\s+\w+\s+(\w+)"))
                    Check(Regex.IsMatch(fragment + vertex, @"uniform\s+\w+\s+" + declaration.Groups[1].Value + @"(?:\s|[=;\[])"), "provider setter retained: " + declaration.Groups[1].Value);
                GL.DeleteProgram(linked);
                // Exercise the actual shadow varyings/samplers as well as the
                // no-shadow numerical fixture used below.
                int shadowLinked = ProbeShader.Program(
                    (ShaderType.VertexShader, vertex.Replace("#define SHADOWQUALITY 0", "#define SHADOWQUALITY 2", StringComparison.Ordinal)),
                    (ShaderType.FragmentShader, fragment.Replace("#define SHADOWQUALITY 0", "#define SHADOWQUALITY 2", StringComparison.Ordinal)));
                GL.DeleteProgram(shadowLinked);
                // Retain exactly the provider's fragment input ABI while giving its
                // material/derivative code a small, deterministic surface to shade.
                string declarations = string.Join("\n", Regex.Matches(vertex, @"(?m)^\s*(?:flat\s+)?out\s+(\w+)\s+(\w+)\s*;").Select(m => m.Value));
                string assignments = string.Join("\n", Regex.Matches(declarations, @"out\s+(\w+)\s+(\w+)").Select(m => m.Groups[2].Value + "=" + m.Groups[1].Value + "(0);"));
                string synthetic = "#version 330 core\nuniform vec3 probePos; uniform float probeClipDepth; uniform float probeBand;\n" + declarations + "\nvoid main(){\n" + assignments + """

                    vec2 p=vec2((gl_VertexID<<1)&2,gl_VertexID&2);
                    gl_Position=vec4(p*2.-1.,probeClipDepth,1);
                    worldPos=vec4(probePos+vec3((p-.5)*2.,0),1);
                    yLevel=200.; dist=.5; rgbaFog=vec4(.2,.3,.4,1);
                """ + (overlay ? "fogAmount=.75;" : "vertexColor=vec4(.4,.5,.3,probeBand); tint=vec3(1); climateUv=vec2(-1); leafWeight=-1; flowerCover=-1; snowTemp=1;") + "\n}";
                string previous = patched.Replace("    drtDiscardLodNearBoundary(worldPos.xyz);\n", "", StringComparison.Ordinal);
                LodOverlapProbe.Run(synthetic, expand(previous, ssao), fragment, Setup, bind, restore, frame, ubo, fbo, name + " SSAO=" + ssao);
                int program = ProbeShader.Program((ShaderType.VertexShader, synthetic), (ShaderType.FragmentShader, fragment));
                // The allocator's cache follows real program linking/reloading; the
                // caller's restore resets live ownership before each deleted ID.
                Setup(program);
                bind(program); restore(); // Invalidate any recycled ID from the overlap fixture.
                foreach (float daylight in new[] { 0f, 1f }) foreach (float density in new[] { 0f, .05f, .2f })
                {
                    frame[3] = 1; frame[4] = density; frame[8] = 512;
                    frame[13] = daylight > .5f ? 1 : -1; frame[15] = daylight;
                    sky(daylight > .5f ? new Vector3(1.8f, 1.7f, 1.5f) : new Vector3(.05f, .1f, .15f));
                    GL.BindBuffer(BufferTarget.UniformBuffer, ubo); GL.BufferData(BufferTarget.UniformBuffer, frame.Length * 4, frame, BufferUsageHint.DynamicDraw);
                    var pixel = read(program, 600);
                    Check(pixel.Take(3).All(float.IsFinite) && pixel.Take(3).Any(c => c > 0) && pixel[3] == 1, "day/night/dense solid terrain remains visible");
                    Check(pixel[4] == 0 && pixel[5] == 0 && pixel[6] == 0 && pixel[7] == 1, "forward glow has no deferred marker");
                }
                if (!overlay)
                {
                    // DV's packed alpha selects water (band one) or thin material
                    // (band two). Shared fog must retain their authored coverage.
                    foreach (float band in new[] { 64f / 255f, 128f / 255f })
                    {
                        GL.UseProgram(program); Scalar(program, "probeBand", band);
                        var pixel = read(program, 600);
                        float expectedAlpha = band < .4f ? 1f : .5f;
                        Check(Math.Abs(pixel[3] - expectedAlpha) < 1e-5f && pixel.Take(3).All(float.IsFinite), $"water/thin alpha retained: {band}, actual={pixel[3]}, expected={expectedAlpha}");
                    }
                }
                frame[4] = 0; frame[13] = frame[15] = 1;
                sky(new Vector3(8f, 6f, 4f));
                GL.BindBuffer(BufferTarget.UniformBuffer, ubo); GL.BufferData(BufferTarget.UniformBuffer, frame.Length * 4, frame, BufferUsageHint.DynamicDraw);
                GL.UseProgram(program); Scalar(program, "probeBand", 0);
                Check(read(program, 600).Take(3).Any(c => c > 1), "shared radiance remains unclamped HDR");
                GL.UseProgram(0); restore(); GL.DeleteProgram(program);
                Console.WriteLine($"PASS Distant Vistas 1.1.3 {name} SSAO={ssao}: actual ABI/link, reload/fallback, depth/MRT exclusion, day/night/dense HDR and material alpha");
            }
        }
        var system = new FakeSystem();
        var getter = LodFogRange.CreateDistantGetter(system);
        Check(getter() == 0, "idle provider has no extended endpoint");
        system.renderer = new FakeRenderer { EffectiveFarDistance = 18000 };
        Check(getter() == 18000, "live renderer endpoint");
        system.renderer = new FakeRenderer { EffectiveFarDistance = 2400 };
        Check(getter() == 2400, "replacement renderer/cap is read live");
        Console.WriteLine("PASS Distant Vistas idle/live/replacement fog range");
    }

    private sealed class FakeSystem { public FakeRenderer renderer; }
    private sealed class FakeRenderer { public float EffectiveFarDistance { get; set; } }
}
