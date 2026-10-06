using System;
using System.IO;
using DRTAgX;
using OpenTK.Graphics.OpenGL4;

internal static class ForwardLightingProbe
{
    internal static void Run(string deferredPath)
    {
        using var state = new ShadowGlState();
        string assets = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(deferredPath)!, "../.."));
        string fog = File.ReadAllText(Path.Combine(assets, "game/shaders/fogandlight.vsh"));
        string lighting = Path.Combine(assets, "drtagx/shaders/lighting");
        string common = File.ReadAllText(Path.Combine(lighting, "drtagx_light_balance.ash"));
        string accumulation = File.ReadAllText(Path.Combine(lighting, "drtagx_dynamic_accumulation.ash"));
        // Turn the maintained interpolated inputs into controlled fragment locals.
        string surface = File.ReadAllText(Path.Combine(lighting, "drtagx_surface_lighting.fsh"))
            .Replace("#include drtagx_fog_transport.ash", "") // Isolated legacy-path parity fixture; ambient UBO has its own production probe.
            .Replace("#include drtagx_light_balance.ash", "").Replace("in vec3 drt", "vec3 drt");
        string deferred = ProbeShader.DeferredSource(deferredPath);
        // Keep both maintained compositors in the same program with distinct
        // globals. Their inputs share the actual forward G-buffer components.
        string ParityNames(string text)
        {
            foreach (string name in new[] { "drtSunLight", "drtLocalLight", "drtSkyLight",
                "drtSurfaceSunAccess", "drtAppliedSunScale", "drtSunEffectColor",
                "drtComposeRadiance", "drtSunOnlyMultiplier", "drtShadeSurface", "drtVisibleLocalLight" })
                text = text.Replace(name, "parity" + name[3..]);
            return text;
        }
        string parity = "\n#undef drtSurfaceSunAccess\n#define DRT_DEFERRED_LIGHTING\n" +
            ParityNames(surface) + ProbeShader.Function(deferred, "float drtPlacedWeight(") + """
            vec4 drtPlacedLights(vec3 r, vec3 n, bool direct, bool grass, out float e, out vec3 s) {
                e = 0.0; s = vec3(0.0); return vec4(0.0);
            }
            vec4 drtPlacedFoliageLight(vec3 r, vec3 n, out float facing) { facing=1.0; return vec4(0.0); }
            float drtMovingVisibility(vec3 r, vec3 e, int i, vec3 n) {
                return 1.0; // Moving shadows disabled or an unobstructed map.
            }
            """ + ProbeShader.Function(deferred, "vec3 drtAccumulateMovingPair(") +
            ProbeShader.Function(deferred, "vec3 drtCurrentFrameLights(")
                .Replace("drtPointCount", "pointLightQuantity").Replace("drtPointPos", "pointLights")
                .Replace("drtPointColor", "pointLightColors") +
            ParityNames(ProbeShader.Function(deferred, "void drtDeferredRelight("))
                .Replace("drtSkyColor", "sky");
        string vertex = """
            #version 430 core
            void main() {
                vec2 p = vec2((gl_VertexID << 1) & 2, gl_VertexID & 2);
                gl_Position = vec4(p * 2.0 - 1.0, 0.0, 1.0);
            }
            """;
        int fbo = GL.GenFramebuffer(), texture = GL.GenTexture(), vao = GL.GenVertexArray();
        int previousProgram = GL.GetInteger(GetPName.CurrentProgram);
        try
        {
            GL.ActiveTexture(TextureUnit.Texture14); GL.BindTexture(TextureTarget.Texture2D, texture);
            GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba32f, 1, 1, 0,
                PixelFormat.Rgba, PixelType.Float, IntPtr.Zero);
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, fbo);
            GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0,
                TextureTarget.Texture2D, texture, 0);
            GL.DrawBuffer(DrawBufferMode.ColorAttachment0); GL.ReadBuffer(ReadBufferMode.ColorAttachment0);
            GL.BindVertexArray(vao); GL.Viewport(0, 0, 1, 1);
            GL.Disable(EnableCap.DepthTest); GL.Disable(EnableCap.ScissorTest); GL.Disable(EnableCap.Blend);
            GL.ColorMask(true, true, true, true);
            float[] baseline = null;
            foreach (int dynamicCount in new[] { 0, 16 })
            {
                string fragment = "#version 430 core\n#define SHADOWQUALITY 1\n#define DYNLIGHTS " + dynamicCount + "\n" + """
                    #define DRT_DYNAMIC_CAPACITY 16
                    const int GlowLevelBitMask = 255;
                    float glowLevel;
                    float blockBrightness;
                    vec3 blockLight;
                    float sm_voxSunLight;
                    float drtVertexFogSunlight;
                    uniform float glitchStrengthFL;
                    uniform float nightVisionStrength;
                    uniform vec3 sky;
                    uniform vec4 voxel;
                    uniform int flags;
                    uniform float sunVisibility;
                    uniform float reflectiveGain;
                    uniform vec3 normal;
                    uniform vec3 pointLights[16];
                    uniform vec3 pointLightColors[16];
                    uniform vec4 drtPointRadiance[16];
                    uniform int pointLightQuantity;
                    uniform int parityMode;
                    uniform int drtStaticCount;
                    uniform int drtStaticTileWidth;
                    uniform float drtStaticBlend;
                    uniform vec3 raw;
                    out vec4 color;
                    """ + common + accumulation + surface +
                    ProbeShader.Function(fog, "vec4 getPointLightRgbv(") +
                    ProbeShader.Function(fog, "vec4 applyLightWithoutPointLight(") +
                    ProbeShader.Function(fog, "vec4 applyLightWithNormal(") + parity + """
                    void main() {
                        applyLightWithNormal(sky, voxel, flags, vec4(0.0, 0.0, 0.0, 1.0), normal);
                        if (parityMode == 0) {
                            vec3 diffuse = raw * drtShadeSurface(blockLight, sunVisibility, vec3(1.0));
                            color = vec4(drtSunOnlyMultiplier(diffuse, vec3(reflectiveGain)), glowLevel);
                        } else {
                            // Same producer expression as chunkopaque/chunktopsoil,
                            // and the continuous Glow-B contract before deferred relight.
                            vec3 albedo = raw * (drtVoxelLight + drtEmissionLight);
                            float bb = blockBrightness;
                            float access = drtUnpackDeferredSun(drtPackDeferredSun(sm_voxSunLight));
                            drtDeferredRelight(albedo, bb, raw, vec3(0.0), normal, normal,
                                access, access, glowLevel, 0.0);
                            vec3 diffuse = parityShadeSurface(albedo, sunVisibility, vec3(1.0));
                            color = vec4(paritySunOnlyMultiplier(diffuse, vec3(reflectiveGain)), glowLevel);
                        }
                    }
                    """;
                int program = ProbeShader.Program((ShaderType.VertexShader, vertex), (ShaderType.FragmentShader, fragment));
                try
                {
                    GL.UseProgram(program);
                    GL.Uniform2(GL.GetUniformLocation(program, "drtSunlightBounds"), 0.03f, 0.81f);
                    GL.Uniform3(GL.GetUniformLocation(program, "sky"), 1f, 1f, 1f);
                    GL.Uniform4(GL.GetUniformLocation(program, "voxel"), 1f, 0.6f, 0.2f, 0.81f);
                    GL.Uniform1(GL.GetUniformLocation(program, "flags"), 64);
                    GL.Uniform1(GL.GetUniformLocation(program, "reflectiveGain"), 1f);
                    GL.Uniform3(GL.GetUniformLocation(program, "raw"), 1f, 1f, 1f);
                    float[] Draw(float brightness)
                    {
                        GL.Uniform1(GL.GetUniformLocation(program, "sunVisibility"), brightness);
                        GL.DrawArrays(PrimitiveType.Triangles, 0, 3);
                        float[] pixel = new float[4];
                        GL.ReadPixels(0, 0, 1, 1, PixelFormat.Rgba, PixelType.Float, pixel);
                        if (GL.GetError() != ErrorCode.NoError) throw new Exception("Forward lighting GL error");
                        return pixel;
                    }
                    void Equal(float[] actual, float[] expected, string name)
                    {
                        for (int i = 0; i < 4; ++i)
                            if (!float.IsFinite(actual[i]) || Math.Abs(actual[i] - expected[i]) > 2e-5)
                                throw new Exception($"{name}: {actual[i]} != {expected[i]}");
                        Console.WriteLine("PASS " + name);
                    }
                    float[] full = Draw(1);
                    Equal(full, [4f, 3.6f, 3.2f, 0.1f], $"DYNLIGHTS={dynamicCount}: authored forward sunlight/local/glow scales");
                    Equal(Draw(0), [2f, 1.6f, 1.2f, 0.1f], $"DYNLIGHTS={dynamicCount}: shadows affect only sunlight");
                    if (baseline != null) Equal(full, baseline, "disabled and enabled-empty dynamic paths have identical radiance");
                    baseline = full;
                    GL.Uniform1(GL.GetUniformLocation(program, "reflectiveGain"), 3f);
                    Equal(Draw(0), [2f, 1.6f, 1.2f, 0.1f], "reflective sunlight gain cannot amplify occluded local/emissive light");
                    Equal(Draw(1), [8f, 7.6f, 7.2f, 0.1f], "reflective gain changes only the composed sunlight component");
                    GL.Uniform1(GL.GetUniformLocation(program, "reflectiveGain"), 1f);
                    if (dynamicCount > 0)
                    {
                        float[] colors = new float[48]; Array.Fill(colors, 10f);
                        GL.Uniform3(GL.GetUniformLocation(program, "pointLightColors[0]"), 16, colors);
                        float[] prepared = new float[64];
                        for (int i=0; i<16; ++i) { prepared[i*4]=prepared[i*4+1]=prepared[i*4+2]=1; prepared[i*4+3]=MathF.Sqrt(300); }
                        GL.Uniform4(GL.GetUniformLocation(program, "drtPointRadiance[0]"), 16, prepared);
                        GL.Uniform1(GL.GetUniformLocation(program, "pointLightQuantity"), 16);
                        float budget = 0; for (int i = 1; i <= 8; ++i) budget += (1.5f * 1.25f)/i;
                        Equal(Draw(1), [4f+budget, 3.6f+budget, 3.2f+budget, 0.1f], "actual forward ABI uses shared H8 dynamic budget");
                    }
                    // These comparisons do not assume fixed art-direction gains.
                    // Isolate each energy source before fog, exposure and display.
                    GL.Uniform2(GL.GetUniformLocation(program, "drtSunlightBounds"), 0f, 1f);
                    GL.Uniform3(GL.GetUniformLocation(program, "raw"), 0.6f, 0.4f, 0.2f);
                    GL.Uniform3(GL.GetUniformLocation(program, "sky"), 0.35f, 0.5f, 0.65f);
                    GL.Uniform3(GL.GetUniformLocation(program, "normal"), 0f, 0f, 1f);
                    foreach (string component in new[] { "block", "sun", "emission", "dynamic", "mixed" })
                    {
                        bool mixed = component == "mixed";
                        GL.Uniform4(GL.GetUniformLocation(program, "voxel"),
                            component == "block" || mixed ? 0.8f : 0f,
                            component == "block" || mixed ? 0.4f : 0f,
                            component == "block" || mixed ? 0.2f : 0f,
                            component == "sun" || mixed ? 17f/31f : 0f);
                        GL.Uniform1(GL.GetUniformLocation(program, "flags"), component == "emission" || mixed ? 64 : 0);
                        GL.Uniform1(GL.GetUniformLocation(program, "pointLightQuantity"),
                            dynamicCount > 0 && (component == "dynamic" || mixed) ? 16 : 0);
                        foreach (float visibility in new[] { 0f, 1f })
                        {
                            GL.Uniform1(GL.GetUniformLocation(program, "parityMode"), 0);
                            float[] forward = Draw(visibility);
                            GL.Uniform1(GL.GetUniformLocation(program, "parityMode"), 1);
                            foreach (var gate in new[] { (0, 0f, 0), (0, 1f, 1), (1, 0f, 1), (1, 1f, 0) })
                            {
                                GL.Uniform1(GL.GetUniformLocation(program, "drtStaticCount"), gate.Item1);
                                GL.Uniform1(GL.GetUniformLocation(program, "drtStaticBlend"), gate.Item2);
                                GL.Uniform1(GL.GetUniformLocation(program, "drtStaticTileWidth"), gate.Item3);
                                Equal(Draw(visibility), forward,
                                    $"forward/deferred {component} parity: DYNLIGHTS={dynamicCount}, sun visibility={visibility}, cache={gate}");
                            }
                        }
                    }
                }
                finally { GL.UseProgram(previousProgram); GL.DeleteProgram(program); }
            }
        }
        finally { GL.DeleteFramebuffer(fbo); GL.DeleteTexture(texture); GL.DeleteVertexArray(vao); }
    }
}
