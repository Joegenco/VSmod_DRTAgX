using System;
using System.IO;
using System.Linq;
using DRTAgX;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;
using Vintagestory.API.MathTools;

internal static class PlacedPerformanceProbe
{
    // Isolated GPU benchmark of tile compute plus the complete placed-light
    // traversal/filter. Distinct D24 maps and current-frame depth are included.
    // It does not represent native terrain/HDR/UI or measure gameplay FPS.
    internal static void Run(string path, string beforeRoot)
    {
        using var window = new GameWindow(GameWindowSettings.Default, new NativeWindowSettings {
            StartVisible = false, ClientSize = new Vector2i(16, 16), API = ContextAPI.OpenGL,
            APIVersion = new Version(4, 3), Profile = ContextProfile.Core
        });
        window.MakeCurrent(); GL.LoadBindings(new OpenTK.Windowing.GraphicsLibraryFramework.GLFWBindingsContext());
        string current = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, "../../drtagx/shaders"));
        string before = Path.GetFullPath(Path.Combine(beforeRoot, "VintageStory_DRTAgX_2.0.0/DRTAgX/assets/drtagx/shaders"));
        Console.WriteLine("GPU: " + GL.GetString(StringName.Renderer));
        const string vertex = "#version 430 core\nvoid main(){vec2 p=vec2((gl_VertexID<<1)&2,gl_VertexID&2);gl_Position=vec4(p*2-1,0,1);}";
        const string prefix = """
            #version 430 core
            layout(std430,binding=4) readonly buffer Sources { vec4 drtStaticSourceData[]; };
            layout(std430,binding=5) readonly buffer Tiles { uint drtStaticTileData[]; };
            uniform sampler2DArrayShadow drtStaticMaps;
            uniform sampler2D receiverDepth;
            uniform mat4 inverseProjection, invModelViewMatrix;
            uniform vec2 frameSize;
            uniform vec2 drtPlacedCalibration[32];
            uniform int drtStaticCount, drtStaticTileWidth;
            uniform int drtShadowGridEnabled=1;
            uniform int drtStaticAllTerrainPasses=0; // Preserve this fixture's legacy lighting selection.
            uniform float drtStaticBlend, sunBrightness;
            out vec4 color;
            """;
        int Lighting(bool old)
        {
            string folder = Path.Combine(old ? before : current, "deferred");
            string suffix = """
                void main(){
                    vec2 uv = gl_FragCoord.xy / frameSize;
                    float d = texelFetch(receiverDepth, ivec2(gl_FragCoord.xy), 0).r;
                    if(d == 1.0){color=vec4(0);return;}
                    vec4 p = inverseProjection * vec4(uv*2-1,d*2-1,1);
                    vec3 receiver = p.xyz / p.w;
                    float emitter; vec3 selfLight;
                """;
            suffix += old ? "vec4 placed=drtPlacedLights(receiver,vec3(0,0,1),emitter,selfLight);float weight=drtPlacedWeight(receiver,sunBrightness,0);" :
                "float weight=drtPlacedWeight(receiver,sunBrightness,0);vec4 placed=drtPlacedLights(receiver,vec3(0,0,1),weight>0,false,emitter,selfLight);";
            suffix += "color=vec4(placed.rgb*weight+selfLight,placed.a);}";
            return ProbeShader.Program((ShaderType.VertexShader, vertex), (ShaderType.FragmentShader, prefix +
                File.ReadAllText(Path.Combine(folder, "drtagx_deferred_cube.fsh")) +
                File.ReadAllText(Path.Combine(folder, "drtagx_deferred_staticshadows.fsh")) +
                File.ReadAllText(Path.Combine(folder, "drtagx_deferred_placedlights.fsh")) + suffix));
        }
        int oldProgram = Lighting(true), newProgram = Lighting(false);
        int coarse = ProbeShader.Program((ShaderType.ComputeShader, File.ReadAllText(Path.Combine(current, "staticlighttiles.csh"))));
        int fine = ProbeShader.Program((ShaderType.ComputeShader, File.ReadAllText(Path.Combine(current, "staticlighttiles.csh"))
            .Replace("#version 430 core", "#version 430 core\n#define DRT_DEPTH_CULL")));
        int fbo = GL.GenFramebuffer(), output = GL.GenTexture(), terrain = GL.GenTexture(), atlas = GL.GenTexture(), vao = GL.GenVertexArray();
        int sourceBuffer = GL.GenBuffer(), tileBuffer = GL.GenBuffer(), rectangleBuffer = GL.GenBuffer(), query = GL.GenQuery();
        GL.BindVertexArray(vao); GL.Disable(EnableCap.DepthTest); GL.Disable(EnableCap.Blend); GL.Disable(EnableCap.ScissorTest);
        GL.ActiveTexture(TextureUnit.Texture14); GL.BindTexture(TextureTarget.Texture2DArray, atlas);
        GL.TexStorage3D(TextureTarget3d.Texture2DArray, 1, SizedInternalFormat.DepthComponent24, 192, 192, 128 * 6);
        GL.TexParameter(TextureTarget.Texture2DArray, TextureParameterName.TextureCompareMode, (int)TextureCompareMode.CompareRefToTexture);
        GL.TexParameter(TextureTarget.Texture2DArray, TextureParameterName.TextureCompareFunc, (int)DepthFunction.Lequal);
        GL.TexParameter(TextureTarget.Texture2DArray, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
        GL.TexParameter(TextureTarget.Texture2DArray, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
        GL.TexParameter(TextureTarget.Texture2DArray, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        GL.TexParameter(TextureTarget.Texture2DArray, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
        float[] face = new float[192 * 192];
        for (int layer = 0; layer < 128 * 6; ++layer)
        {
            // Spatially distinct clear/blocked areas force actual filtering,
            // while keeping the accepted four-tap receiving-plane path intact.
            for (int p = 0; p < face.Length; ++p)
                face[p] = ((p % 192 + layer * 7) % 192 < 75) ? 0.955f + (layer % 11) * 0.002f : 1f;
            GL.TexSubImage3D(TextureTarget.Texture2DArray, 0, 0, 0, layer, 192, 192, 1, PixelFormat.DepthComponent, PixelType.Float, face);
        }
        try
        {
            foreach (var resolution in new[] { (1920, 1080), (3840, 2160) })
            foreach (var scene in new[] { ("sparse", 1), ("overlap", 8), ("overlap", 16), ("overlap", 32), ("separated", 8),
                ("separated", 16), ("separated", 64), ("separated", 128), ("mixed", 128), ("horizon", 128), ("day", 32) })
            {
                int width = resolution.Item1, height = resolution.Item2, count = scene.Item2;
                int tileWidth = (width + 15) / 16, tiles = tileWidth * ((height + 15) / 16);
                float[] projection = Mat4f.Create(), inverse = new float[16];
                Mat4f.Perspective(projection, 1.4f, (float)width / height, 0.1f, 256f); Mat4f.Invert(inverse, projection);
                float[] depths = new float[width * height];
                for (int p = 0; p < depths.Length; ++p)
                {
                    float z = scene.Item1 == "separated" ? 40f : scene.Item1 == "mixed" && p % width > width / 2 ? 100f : 8f;
                    depths[p] = scene.Item1 == "horizon" && p / width > height / 2 ? 1f :
                        (-projection[10] + projection[14] / z) * 0.5f + 0.5f;
                }
                GL.ActiveTexture(TextureUnit.Texture13); GL.BindTexture(TextureTarget.Texture2D, terrain);
                GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.R32f, width, height, 0, PixelFormat.Red, PixelType.Float, depths);
                GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
                GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
                GL.ActiveTexture(TextureUnit.Texture0); GL.BindTexture(TextureTarget.Texture2D, output);
                GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba32f, width, height, 0, PixelFormat.Rgba, PixelType.Float, IntPtr.Zero);
                GL.BindFramebuffer(FramebufferTarget.Framebuffer, fbo);
                GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, output, 0);
                GL.DrawBuffer(DrawBufferMode.ColorAttachment0);
                if (GL.CheckFramebufferStatus(FramebufferTarget.Framebuffer) != FramebufferErrorCode.FramebufferComplete) throw new Exception("Benchmark FBO");
                GL.Viewport(0, 0, width, height);
                float[] oldData = new float[count * 12], newData = new float[count * 16];
                int[] rectangles = new int[count * 4];
                for (int i = 0; i < count; ++i)
                {
                    float x = (i % 8 - 3.5f) * 0.8f, y = (i / 8 - 1.5f) * 0.6f;
                    float z = scene.Item1 == "separated" ? (i < 2 ? -32f : 0f) : -5f;
                    int a = i * 12, b = i * 16;
                    oldData[a] = newData[b] = newData[b + 12] = x;
                    oldData[a + 1] = newData[b + 1] = newData[b + 13] = y;
                    oldData[a + 2] = newData[b + 2] = newData[b + 14] = z;
                    oldData[a + 3] = newData[b + 3] = 22;
                    oldData[a + 4] = 1; oldData[a + 5] = i % 64; oldData[a + 6] = 8;
                    oldData[a + 7] = newData[b + 7] = i;
                    oldData[a + 8] = newData[b + 8] = 1;
                    oldData[a + 9] = newData[b + 9] = -1;
                    oldData[a + 10] = newData[b + 10] = -1;
                    newData[b + 15] = 20;
                    StaticLightGpuRecord.Rgb(i % 64, 8, out newData[b + 4], out newData[b + 5], out newData[b + 6]);
                    rectangles[i * 4 + 2] = tileWidth - 1; rectangles[i * 4 + 3] = (height + 15) / 16 - 1;
                }
                GL.BindBuffer(BufferTarget.ShaderStorageBuffer, rectangleBuffer);
                GL.BufferData(BufferTarget.ShaderStorageBuffer, rectangles.Length * 4, rectangles, BufferUsageHint.StaticDraw);
                GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 6, rectangleBuffer);
                GL.BindBuffer(BufferTarget.ShaderStorageBuffer, tileBuffer);
                GL.BufferData(BufferTarget.ShaderStorageBuffer, tiles * 5 * 4, IntPtr.Zero, BufferUsageHint.DynamicDraw);
                GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 5, tileBuffer);
                foreach (int program in new[] { oldProgram, newProgram })
                {
                    GL.UseProgram(program);
                    GL.Uniform1(GL.GetUniformLocation(program, "drtStaticCount"), count);
                    GL.Uniform1(GL.GetUniformLocation(program, "drtStaticTileWidth"), tileWidth);
                    GL.Uniform1(GL.GetUniformLocation(program, "drtStaticBlend"), 1f);
                    GL.Uniform1(GL.GetUniformLocation(program, "drtStaticMaps"), 14);
                    GL.Uniform1(GL.GetUniformLocation(program, "receiverDepth"), 13);
                    GL.Uniform1(GL.GetUniformLocation(program, "sunBrightness"), scene.Item1 == "day" ? 1f : 0f);
                    GL.Uniform2(GL.GetUniformLocation(program, "frameSize"), (float)width, (float)height);
                    GL.Uniform2(GL.GetUniformLocation(program, "drtPlacedCalibration"), 32, Enumerable.Repeat(8f, 64).ToArray());
                    GL.UniformMatrix4(GL.GetUniformLocation(program, "inverseProjection"), 1, false, inverse);
                    GL.UniformMatrix4(GL.GetUniformLocation(program, "invModelViewMatrix"), 1, false, Mat4f.Create());
                }
                foreach (int program in new[] { coarse, fine })
                {
                    GL.UseProgram(program);
                    GL.Uniform1(GL.GetUniformLocation(program, "sourceCount"), count);
                    GL.Uniform1(GL.GetUniformLocation(program, "tileWidth"), tileWidth);
                    GL.Uniform1(GL.GetUniformLocation(program, "tileCount"), tiles);
                    GL.Uniform1(GL.GetUniformLocation(program, "terrainDepth"), 13);
                    GL.UniformMatrix4(GL.GetUniformLocation(program, "inverseProjection"), 1, false, inverse);
                }
                void Draw(int variant)
                {
                    GL.UseProgram(variant == 2 ? fine : coarse);
                    GL.MemoryBarrier(MemoryBarrierFlags.ShaderStorageBarrierBit);
                    GL.DispatchCompute(variant == 2 ? tiles : (tiles + 63) / 64, 1, 1);
                    GL.MemoryBarrier(MemoryBarrierFlags.ShaderStorageBarrierBit);
                    GL.UseProgram(variant == 0 ? oldProgram : newProgram);
                    GL.DrawArrays(PrimitiveType.Triangles, 0, 3);
                }
                void Upload(int variant)
                {
                    float[] data = variant == 0 ? oldData : newData;
                    GL.BindBuffer(BufferTarget.ShaderStorageBuffer, sourceBuffer);
                    GL.BufferData(BufferTarget.ShaderStorageBuffer, data.Length * 4, data, BufferUsageHint.StreamDraw);
                    GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 4, sourceBuffer);
                }
                double Measure(int variant)
                {
                    Upload(variant); GL.BeginQuery(QueryTarget.TimeElapsed, query); Draw(variant); GL.EndQuery(QueryTarget.TimeElapsed);
                    GL.GetQueryObject(query, GetQueryObjectParam.QueryResult, out long ns); return ns / 1e6;
                }
                for (int warm = 0; warm < 4; ++warm) for (int v = 0; v < 3; ++v) Measure(v);
                double[][] times = { new double[17], new double[17], new double[17] };
                for (int r = 0; r < 17; ++r) for (int i = 0; i < 3; ++i) { int v = (r + i) % 3; times[v][r] = Measure(v); }
                foreach (double[] samples in times) Array.Sort(samples);
                // Output parity includes the actual depth mask and map filtering.
                // Use a small readback sample window; all-receiver cull proof is
                // separately covered by DepthTileProbe's brute-force reference.
                float[][] pixels = { new float[64 * 64 * 4], new float[64 * 64 * 4], new float[64 * 64 * 4] };
                for (int v = 0; v < 3; ++v) { Upload(v); Draw(v); GL.ReadPixels(width / 2 - 32, height / 2 - 32, 64, 64, PixelFormat.Rgba, PixelType.Float, pixels[v]); }
                float error = 0;
                for (int i = 0; i < pixels[0].Length; ++i) for (int v = 1; v < 3; ++v) error = Math.Max(error, Math.Abs(pixels[v][i] - pixels[0][i]));
                if (error > 0.0001f) throw new Exception("Benchmark lighting parity: " + error);
                Console.WriteLine($"{width}x{height} {scene.Item1}/{count}: baseline {times[0][8]:F3}/{times[0][16]:F3}, prepared/coarse {times[1][8]:F3}/{times[1][16]:F3}, prepared/depth {times[2][8]:F3}/{times[2][16]:F3} ms median/p95; parity max {error:G3}");
                if (GL.GetError() != ErrorCode.NoError) throw new Exception("Benchmark GL error");
            }
        }
        finally
        {
            GL.DeleteProgram(oldProgram); GL.DeleteProgram(newProgram); GL.DeleteProgram(coarse); GL.DeleteProgram(fine);
            GL.DeleteQuery(query); GL.DeleteBuffer(sourceBuffer); GL.DeleteBuffer(tileBuffer); GL.DeleteBuffer(rectangleBuffer);
            GL.DeleteFramebuffer(fbo); GL.DeleteTexture(output); GL.DeleteTexture(terrain); GL.DeleteTexture(atlas); GL.DeleteVertexArray(vao);
        }
    }
}
