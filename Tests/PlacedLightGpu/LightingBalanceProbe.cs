using System;
using System.IO;
using DRTAgX;
using OpenTK.Graphics.OpenGL4;
using Vintagestory.API.MathTools;

internal static class LightingBalanceProbe
{
    // Synthetic floating-point pixels exercise maintained GLSL before fog,
    // exposure or OIT blending. No screenshots or frame-time measurements.
    internal static void Run(string deferredPath)
    {
        using var state = new ShadowGlState();
        string folder = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(deferredPath)!, "../../drtagx/shaders/lighting"));
        string source = ProbeShader.DeferredSource(deferredPath);
        string common = File.ReadAllText(Path.Combine(folder, "drtagx_light_balance.ash"));
        string accumulation = File.ReadAllText(Path.Combine(folder, "drtagx_dynamic_accumulation.ash"));
        string fog = File.ReadAllText(Path.GetFullPath(Path.Combine(folder, "../../../game/shaders/fogandlight.fsh")));
        string opaqueVertex = File.ReadAllText(Path.GetFullPath(Path.Combine(folder, "../../../game/shaders/chunkopaque.vsh")));
        // Execute the maintained opaque vertex shade too: its interpolated nb
        // supplies forward terrain, while deferred evaluates normals per pixel.
        int vertexShadeStart = opaqueVertex.IndexOf("//  14.3.24:", StringComparison.Ordinal);
        if (vertexShadeStart < 0) throw new Exception("Missing opaque vertex normal shading section");
        string vertexShade = opaqueVertex[vertexShadeStart..opaqueVertex.LastIndexOf('}')];
        string surface = File.ReadAllText(Path.Combine(folder, "drtagx_surface_lighting.fsh"))
            .Replace("#include drtagx_fog_transport.ash", "") // Ambient UBO behavior is covered by AmbientSkyProbe.
            .Replace("#include drtagx_light_balance.ash", "");
        string fragment = """
            #version 430 core
            #define DRT_DYNAMIC_CAPACITY 16
            #define DRT_DEFERRED_LIGHTING
            #define SHADOWQUALITY 1
            uniform vec3 lightPosition;
            uniform float shadowIntensity;
            in float probeVertexFace;
            uniform int mode;
            uniform float alpha;
            uniform vec3 sky;
            uniform vec3 delta;
            uniform vec3 authored;
            uniform vec3 normal;
            uniform float visibility;
            uniform vec3 contributions[16];
            uniform int count;
            uniform vec3 sun;
            uniform vec3 local;
            uniform vec3 boost;
            uniform float block;
            uniform float tags;
            uniform float packedValue;
            uniform vec4 hsv;
            uniform vec4 placed;
            uniform float emitter;
            uniform vec3 selfLight;
            uniform vec3 dynamic;
            uniform vec3 raw;
            uniform float glow;
            uniform vec3 drtSkyColor;
            uniform float drtStaticBlend;
            uniform int drtStaticCount;
            uniform int drtStaticTileWidth;
            out vec4 color;
            """ + common + accumulation + surface +
            ProbeShader.Function(fog, "float getBrightnessFromNormal(") +
            ProbeShader.Function(source, "vec3 drtSourceRgb(") +
            ProbeShader.Function(source, "float drtPlacedWeight(") + """
            vec4 drtPlacedLights(vec3 r, vec3 n, bool direct, bool grass, out float e, out vec3 s) {
                e = emitter; s = selfLight; return placed;
            }
            vec4 drtPlacedFoliageLight(vec3 r, vec3 n, out float facing) { facing=1.0; return vec4(0.0); }
            vec3 drtCurrentFrameLights(vec3 r, vec3 n) { return dynamic; }
            """ + ProbeShader.Function(source, "void drtDeferredRelight(") + """
            void main() {
                float access = drtSunAccess(alpha);
                if (mode == 0) color = vec4(drtSunRadiance(access, sky), access);
                else if (mode == 1) color = vec4(drtDynamicContribution(delta, authored, normal, visibility), 1.0);
                else if (mode == 2) color = vec4(drtAccumulateDynamic(contributions, count), 1.0);
                else if (mode == 3) color = vec4(drtComposeRadiance(sun, local, visibility, boost), 1.0);
                else if (mode == 4) color = vec4(drtPackSunBlock(alpha, block) + tags, 0.0, 0.0, 1.0);
                else if (mode == 5) color = vec4(drtUnpackSunBlock(packedValue), 0.0, 1.0);
                else if (mode == 6) color = vec4(drtSourceRgb(hsv), 1.0);
                else if (mode == 8) color = vec4(drtAmbientSky(sky), 1.0);
                else if (mode == 9 || mode == 10) {
                    float face = mode == 10 ? probeVertexFace : getBrightnessFromNormal(normal, 1.0,
                        0.34 + (1.0 - shadowIntensity) / 8.0);
                    float effective = mix(1.0, drtCombineSunDarkening(visibility, face), sqrt(clamp(alpha, 0.0, 1.0)));
                    color = vec4(drtComposeRadiance(sun, local, effective, boost), face);
                }
                else {
                    vec3 albedo = local; float bb = block;
                    drtDeferredRelight(albedo, bb, raw, delta, normal, normal, alpha, alpha, glow, 0.0);
                    color = vec4(drtShadeSurface(albedo, visibility, boost), 1.0);
                }
            }
            """;
        string vertex = """
            #version 430 core
            #define SHADOWQUALITY 1
            uniform vec3 normal;
            uniform vec3 lightPosition;
            uniform float shadowIntensity;
            out float probeVertexFace;
            """ + common + """
            void main() {
                float nb;
            """ + vertexShade + """
                probeVertexFace = nb;
                vec2 p = vec2((gl_VertexID << 1) & 2, gl_VertexID & 2);
                gl_Position = vec4(p * 2.0 - 1.0, 0.0, 1.0);
            }
            """;
        int program = ProbeShader.Program((ShaderType.VertexShader, vertex), (ShaderType.FragmentShader, fragment));
        int texture = GL.GenTexture(), fbo = GL.GenFramebuffer(), vao = GL.GenVertexArray();
        try
        {
            GL.ActiveTexture(TextureUnit.Texture14);
            GL.BindTexture(TextureTarget.Texture2D, texture);
            GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba32f, 1, 1, 0,
                PixelFormat.Rgba, PixelType.Float, IntPtr.Zero);
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, fbo);
            GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0,
                TextureTarget.Texture2D, texture, 0);
            GL.DrawBuffer(DrawBufferMode.ColorAttachment0);
            GL.ReadBuffer(ReadBufferMode.ColorAttachment0);
            if (GL.CheckFramebufferStatus(FramebufferTarget.Framebuffer) != FramebufferErrorCode.FramebufferComplete)
                throw new Exception("Lighting probe framebuffer incomplete");
            GL.BindVertexArray(vao);
            GL.UseProgram(program);
            GL.Viewport(0, 0, 1, 1);
            GL.Disable(EnableCap.DepthTest); GL.Disable(EnableCap.Blend); GL.Disable(EnableCap.ScissorTest);
            GL.ColorMask(true, true, true, true);
            int Loc(string name) => GL.GetUniformLocation(program, name);
            void F(string name, float value) => GL.Uniform1(Loc(name), value);
            void V(string name, float x, float y, float z) => GL.Uniform3(Loc(name), x, y, z);
            float[] Draw(int mode)
            {
                GL.Uniform1(Loc("mode"), mode);
                GL.DrawArrays(PrimitiveType.Triangles, 0, 3);
                float[] result = new float[4];
                GL.ReadPixels(0, 0, 1, 1, PixelFormat.Rgba, PixelType.Float, result);
                if (GL.GetError() != ErrorCode.NoError) throw new Exception("Lighting pixel GL error");
                return result;
            }
            void Equal(float[] result, float[] expected, string name, float tolerance = 2e-5f)
            {
                for (int i = 0; i < expected.Length; ++i)
                    if (!float.IsFinite(result[i]) || MathF.Abs(result[i] - expected[i]) > tolerance)
                        throw new Exception($"{name}: channel {i}, {result[i]} != {expected[i]}");
                Console.WriteLine("PASS " + name);
            }
            float[] DrawSunCase() {
                float[] pixel = Draw(9);
                Equal(Draw(10), pixel, "opaque vertex/fragment sun face shading parity");
                return pixel;
            }
            GL.Uniform2(Loc("drtSunlightBounds"), 0.03f, 0.81f);
            V("sky", 1, 1, 1); F("alpha", 0.81f);
            Equal(Draw(0), [1, 1, 1, 1], "full live-table sun retains current 1x base gain");
            F("alpha", 0.03f);
            Equal(Draw(0), [0, 0, 0, 0], "sun-table zero is fully dark");
            F("alpha", 0.42f);
            Equal(Draw(0), [0.5f, 0.5f, 0.5f, 0.5f], "table-derived sunlight midpoint");
            F("alpha", 0.81f); V("sky", 0.01f, 0.02f, 0.03f);
            Equal(Draw(0), [0.01f, 0.02f, 0.03f, 1], "sky access preserves night/weather radiance");

            // A grazing face must not expose a brighter strip where a biased
            // sun map loses contact. Fully facing surfaces retain cast shadows.
            V("lightPosition", 0, 1, 0); F("shadowIntensity", 1);
            V("normal", 1, 0, 0); V("sun", 2, 2, 2);
            V("local", 0.7f, 0.3f, 0.5f); V("boost", 1, 1, 1);
            F("alpha", 1); F("visibility", 1);
            Equal(DrawSunCase(), [1.4f, 1f, 1.2f, 0.35f], "grazing sun face fills CSM contact gap");
            F("visibility", 0.35f);
            Equal(DrawSunCase(), [1.4f, 1f, 1.2f, 0.35f], "grazing sun face matches full CSM darkness");
            V("normal", 0, 1, 0);
            Equal(DrawSunCase(), [1.4f, 1f, 1.2f, 1f], "sun-facing surface retains full cast shadow");
            F("visibility", 1);
            Equal(DrawSunCase(), [2.7f, 2.3f, 2.5f, 1f], "sun-facing unoccluded surface retains full sunlight");
            V("normal", 1, 0, 0); F("alpha", 0); V("sun", 0, 0, 0);
            Equal(DrawSunCase(), [0.7f, 0.3f, 0.5f, 0.35f], "grazing safeguard leaves cave local/emissive light unchanged");
            F("alpha", 0.25f); V("sun", 0.5f, 0.5f, 0.5f);
            Equal(DrawSunCase(), [1.0375f, 0.6375f, 0.8375f, 0.35f], "partial sky access gates only the sun component");
            V("normal", 0, 1, 0); V("lightPosition", 1, 0, 0);
            F("alpha", 1); V("sun", 2, 2, 2);
            Equal(DrawSunCase(), [1.4f, 1f, 1.2f, 0.35f], "up-facing sky boost cannot expose a grazing sun contact gap");
            F("shadowIntensity", 0); V("normal", 1, 0, 0); V("lightPosition", 0, 1, 0);
            Equal(DrawSunCase(), [1.7f, 1.3f, 1.5f, 0.5f], "zero shadow strength preserves native face shading");

            foreach (float nativeSky in new[] { -1f, 0f, 0.0001f }) {
                V("sky", nativeSky, nativeSky, nativeSky);
                Equal(Draw(8), [0.05f, 0.1f, 0.15f], $"authored ambient minimum at native sky {nativeSky}");
            }
            V("sky", 0.2f, 0.4f, 0.8f);
            Equal(Draw(8), [0.4f, 0.8f, 1.6f],
                "linear ambient fallback above floor without per-channel power");

            V("authored", 10, 10, 10); V("delta", 0, 0, 0); V("normal", 0, 0, 0); F("visibility", 1);
            // Shipping moving/held lighting includes the authored 25% gain.
            const float dynamicGain = 1.5f * 1.25f;
            Equal(Draw(1), [dynamicGain, dynamicGain, dynamicGain], "single dynamic source uses authored scale at zero distance");
            V("authored", 0, 0, 10); V("delta", 0, 0, 5); V("normal", 0, 0, 1);
            float expectedBlue = dynamicGain * 0.5f * MathF.Sqrt(0.5f) / 2.25f;
            Equal(Draw(1), [0, 0, expectedBlue], "pure blue source retains native reach and falloff");
            F("visibility", 0);
            Equal(Draw(1), [0, 0, 0], "dynamic occlusion removes direct light");

            var inputs = new float[48];
            float[] Accumulate(int count)
            {
                GL.Uniform3(Loc("contributions[0]"), 16, inputs);
                GL.Uniform1(Loc("count"), count);
                return Draw(2);
            }
            Array.Fill(inputs, 0.5f);
            Equal(Accumulate(0), [0, 0, 0], "empty dynamic set");
            Equal(Accumulate(1), [0.5f, 0.5f, 0.5f], "one dynamic rank");
            float h8 = 0; for (int i = 1; i <= 8; ++i) h8 += 0.5f / i;
            foreach (int n in new[] { 8, 9, 16 }) Equal(Accumulate(n), [h8, h8, h8], $"{n} identical sources share H8 budget");
            Array.Clear(inputs);
            for (int i = 0; i < 10; ++i) for (int c = 0; c < 3; ++c) inputs[i*3+c] = MathF.Pow(0.7f, i);
            float harmonic = 0; for (int i = 0; i < 8; ++i) harmonic += MathF.Pow(0.7f, i) / (i+1);
            Equal(Accumulate(10), [harmonic, harmonic, harmonic], "separated strengths receive exact harmonic weights");
            float[] ordered = Accumulate(10);
            for (int i = 0; i < 5; ++i) for (int c = 0; c < 3; ++c)
                (inputs[i*3+c], inputs[(9-i)*3+c]) = (inputs[(9-i)*3+c], inputs[i*3+c]);
            Equal(Accumulate(10), ordered, "dynamic permutation invariance");

            // Retain smooth in-budget ties. The strict eighth/ninth cutoff must
            // switch to the stronger color rather than borrowing the ninth.
            void TieSources(float offset, bool boundary)
            {
                Array.Clear(inputs);
                int a = 0;
                if (boundary) {
                    for (int i = 0; i < 7; ++i) for (int c = 0; c < 3; ++c) inputs[i*3+c] = 1.0f - i*0.1f;
                    a = 7;
                }
                inputs[a*3] = (0.2f + offset) / 0.2126f;
                inputs[(a+1)*3+2] = 0.2f / 0.0722f;
            }
            foreach (bool boundary in new[] { false, true }) {
                TieSources(-0.000001f, boundary); float[] before = Accumulate(boundary ? 9 : 2);
                TieSources(0.000001f, boundary); float[] after = Accumulate(boundary ? 9 : 2);
                if (boundary) {
                    if (!(after[0] > before[0] && before[2] > after[2]))
                        throw new Exception("Eighth/ninth cutoff must exclude the weaker color");
                } else Equal(before, after, "first/second colored crossing is continuous", 5e-5f);
                TieSources(0, boundary); float[] tie = Accumulate(boundary ? 9 : 2);
                int a = boundary ? 7 : 0;
                for (int c = 0; c < 3; ++c) (inputs[a*3+c], inputs[(a+1)*3+c]) = (inputs[(a+1)*3+c], inputs[a*3+c]);
                Equal(Accumulate(boundary ? 9 : 2), tie, "exact colored tie is permutation invariant");
            }

            V("sun", 2, 2, 2); V("local", 0.7f, 0.3f, 0.5f); V("boost", 1, 1, 1); F("visibility", 0);
            Equal(Draw(3), [0.7f, 0.3f, 0.5f], "sun occlusion leaves local light and emission unchanged");
            V("sun", 0, 0, 0); F("visibility", 1);
            Equal(Draw(3), [0.7f, 0.3f, 0.5f], "zero sunlight ignores sun-facing brightness");
            V("local", 0, 0, 0);
            Equal(Draw(3), [0, 0, 0], "no texture-independent RGB floor");

            // Round through the real half-float attachment with both retained tags.
            GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba16f, 1, 1, 0,
                PixelFormat.Rgba, PixelType.Float, IntPtr.Zero);
            foreach (float access in new[] { 0f, 0.5f, 1f }) foreach (float tag in new[] { 0f, 64f, 128f, 192f }) {
                F("alpha", access); F("block", 1); F("tags", tag);
                float packed = Draw(4)[0] - tag;
                F("packedValue", packed);
                Equal(Draw(5), [MathF.Floor(access*31+0.5f)/31, 0.875f], "half-float pack retains sunlight and tags", 0.0003f);
            }
            GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba32f, 1, 1, 0,
                PixelFormat.Rgba, PixelType.Float, IntPtr.Zero);
            foreach (int hue in new[] { 0, 10, 21, 42, 63 }) foreach (int sat in new[] { 0, 4, 8 }) {
                StaticLightGpuRecord.Rgb(hue, sat, 1f, out float r, out float g, out float b);
                GL.Uniform4(Loc("hsv"), r, g, b, 0f);
                int packed = ColorUtil.HsvToRgba(hue*4, Math.Min(255, sat*32), 255);
                Vec3f native = new(); ColorUtil.ToRGBVec3f(packed, ref native);
                Equal(Draw(6), [native.Z, native.Y, native.X], "placed chroma matches public native RGB adapter");
            }

            // Exercise actual deferred replacement with controlled visibility/source records.
            V("raw", 1, 1, 1); V("local", 0.5f, 0.5f, 0.5f); V("drtSkyColor", 1, 1, 1);
            V("delta", 0, 0, -5.5f); V("boost", 1, 1, 1); F("visibility", 1); F("alpha", 1);
            F("drtStaticBlend", 1); F("glow", 0);
            Equal(Draw(7), [2.5f, 2.5f, 2.5f], "empty cache preserves full G-buffer local plus 2x sun");
            F("visibility", 0);
            Equal(Draw(7), [0.5f, 0.5f, 0.5f], "deferred sun occlusion preserves local light");
            F("alpha", 0); GL.Uniform1(Loc("drtStaticCount"), 1); GL.Uniform1(Loc("drtStaticTileWidth"), 1);
            GL.Uniform4(Loc("placed"), 0.8f, 0.4f, 1.4f, 1f);
            Equal(Draw(7), [0.4f, 0.25f, 0.7f], "placed maximum retains independently scaled voxel/direct channels");
            GL.Uniform4(Loc("placed"), 0f, 0f, 0f, 1f);
            Equal(Draw(7), [0.25f, 0.25f, 0.25f], "placed occlusion retains authored 0.5 voxel gain");

            // Outdoor sky access stays full at night. Test the complete relight
            // path so its sun envelope cannot erase otherwise valid placed light.
            V("drtSkyColor", 0.01f, 0.02f, 0.03f);
            GL.Uniform4(Loc("placed"), 0.8f, 0.7f, 0.6f, 1f);
            foreach (float access in new[] { 0f, 0.5f, 1f }) {
                F("alpha", access);
                Equal(Draw(7), [0.4f, 0.35f, 0.3f], $"night placed radiance survives sky access {access}");
            }
            GL.Uniform4(Loc("placed"), 0f, 0f, 0f, 1f);
            Equal(Draw(7), [0.25f, 0.25f, 0.25f], "outdoor night placed occlusion preserves voxel gain");
            // A night minimum remains albedo/sky-access dependent, and sun
            // visibility never changes the valid placed/local shadow result.
            V("drtSkyColor", 0, 0, 0);
            GL.Uniform1(Loc("drtStaticCount"), 0);
            V("local", 0, 0, 0); F("visibility", 1);
            Equal(Draw(7), [0.05f, 0.1f, 0.15f], "outdoor authored minimum sky radiance is multiplied by current sun gain");
            F("alpha", 0);
            Equal(Draw(7), [0, 0, 0], "ambient minimum cannot illuminate sealed caves");
            GL.Uniform1(Loc("drtStaticCount"), 1);
            V("drtSkyColor", 0.01f, 0.02f, 0.03f); F("visibility", 0);
            foreach (float directVisibility in new[] { 0f, 0.25f, 0.5f, 1f }) {
                GL.Uniform4(Loc("placed"), 0.5f * directVisibility, 0.4f * directVisibility, 0.3f * directVisibility, 1f);
                F("alpha", 0); float[] cave = Draw(7);
                F("alpha", 1);
                Equal(Draw(7), cave, $"outdoor/cave night shadows agree at direct visibility {directVisibility}");
            }
            V("local", 0.5f, 0.5f, 0.5f);
            V("drtSkyColor", 1, 1, 1);
            GL.Uniform4(Loc("placed"), 0.5f, 0.4f, 0.3f, 1f);
            Equal(Draw(7), [0.25f, 0.25f, 0.25f], "full daylight retains placed suppression");
            F("alpha", 0);
            GL.Uniform4(Loc("placed"), 0f, 0f, 0f, 1f);
            F("glow", 0.5f); V("local", 10.5f, 10.5f, 10.5f);
            Equal(Draw(7), [7.75f, 7.75f, 7.75f], "placed mode retains authored voxel energy and full emission");
            F("glow", 0); V("local", 0.5f, 0.5f, 0.5f); F("drtStaticBlend", 0);
            Equal(Draw(7), [0.5f, 0.5f, 0.5f], "unready cache keeps native local fallback");
        }
        finally
        {
            GL.UseProgram(0);
            GL.DeleteProgram(program); GL.DeleteFramebuffer(fbo); GL.DeleteTexture(texture); GL.DeleteVertexArray(vao);
        }
    }
}
