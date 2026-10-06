using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using DRTAgX;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;
using Vintagestory.API.Client;
using Vintagestory.API.Common;

/// <summary>Run the installed SMAA shader programs/targets with the production compatibility hooks.</summary>
internal static class SmaaHdrProbe
{
    internal static void Run(string assemblyPath)
    {
        using var window = new GameWindow(GameWindowSettings.Default, new NativeWindowSettings {
            StartVisible = false, ClientSize = new Vector2i(48, 24), API = ContextAPI.OpenGL,
            APIVersion = new Version(4, 3), Profile = ContextProfile.Core });
        window.MakeCurrent();
        var context = new OpenTK.Windowing.GraphicsLibraryFramework.GLFWBindingsContext();
        GL.LoadBindings(context);
        OpenTK.Graphics.OpenGL.GL.LoadBindings(context); // The installed mod uses this OpenTK namespace.
        ProbeAssets.Api([]); // Resolve native API signature dependencies without starting the game.
        Assembly assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.GetFullPath(assemblyPath));
        Type rendererType = assembly.GetType("VSSMAA.SMAARenderer", true)!;
        Type system = assembly.GetType("VSSMAA.SMAAModSystem", true)!;
        const BindingFlags fields = BindingFlags.Instance | BindingFlags.NonPublic;
        const BindingFlags methods = BindingFlags.Instance | BindingFlags.NonPublic;
        // Compatibility checks read the separately installed SMAA archive.
        using var archive = ZipFile.OpenRead(Environment.GetEnvironmentVariable("SMAA_MOD_ZIP") ??
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VintagestoryData/Mods/vssmaa.zip"));
        using var sourceReader = new StreamReader(archive.GetEntry("assets/vssmaa/shaders/smaa.glsl")!.Open());
        string source = sourceReader.ReadToEnd();
        var asset = SurfaceApiProxy.Make<IAsset>((method, _) => method.Name == "ToText" ? source : throw new NotSupportedException(method.Name));
        var manager = SurfaceApiProxy.Make<IAssetManager>((method, _) => method.Name == "TryGet" ? asset : throw new NotSupportedException(method.Name));
        int warnings = 0;
        var logger = SurfaceApiProxy.Make<ILogger>((method, args) => {
            if (method.Name == "Warning" || method.Name == "Error") { warnings++; Console.WriteLine(args[0]); }
            return null;
        });
        var api = SurfaceApiProxy.Make<ICoreClientAPI>((method, _) => method.Name switch {
            "get_Assets" => manager, "get_Logger" => logger, _ => throw new NotSupportedException(method.Name)
        });
        object renderer = Activator.CreateInstance(rendererType, api)!;
        int Field(string name) => (int)rendererType.GetField(name, fields)!.GetValue(renderer)!;
        void Call(string name, params object[] args) => rendererType.GetMethod(name, methods)!.Invoke(renderer, args);
        void Check(bool condition, string label) { if (!condition) throw new Exception(label); }
        void Initialize() {
            Call("EnsureInit");
            Check((bool)rendererType.GetField("initialized", fields)!.GetValue(renderer)!, "actual SMAA initialization/shader compile");
        }
        int Format(string name) {
            GL.BindTexture(TextureTarget.Texture2D, Field(name));
            GL.GetTexLevelParameter(TextureTarget.Texture2D, 0, GetTextureParameter.TextureInternalFormat, out int format);
            return format;
        }
        int input = GL.GenTexture();
        void Upload(int width, int height, float[] pixels) {
            GL.ActiveTexture(TextureUnit.Texture0); GL.BindTexture(TextureTarget.Texture2D, input);
            GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba16f, width, height, 0, PixelFormat.Rgba, PixelType.Float, pixels);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
        }
        float[] Image(int width, int height, Vector3 rgb, bool diagonal = false) {
            var pixels = new float[width * height * 4];
            for (int y = 0; y < height; y++) for (int x = 0; x < width; x++) {
                Vector3 color = diagonal ? (x > y * 1.4f + 4 ? new Vector3(16) : new Vector3(2)) : rgb;
                int i = (y * width + x) * 4;
                pixels[i] = color.X; pixels[i + 1] = color.Y; pixels[i + 2] = color.Z; pixels[i + 3] = .375f;
            }
            return pixels;
        }
        void Bind(int unit, int texture) {
            GL.ActiveTexture(TextureUnit.Texture0 + unit); GL.BindTexture(TextureTarget.Texture2D, texture); GL.BindSampler(unit, 0);
        }
        float[] Draw(int width, int height, float sharpness) {
            // This is the provider's own edge/weight/neighborhood/sharpen sequence,
            // using its real programs, LUTs, stencil and framebuffer allocations.
            GL.Viewport(0, 0, width, height); GL.Disable(EnableCap.DepthTest); GL.Disable(EnableCap.Blend);
            GL.Disable(EnableCap.CullFace); GL.Disable(EnableCap.ScissorTest); GL.ColorMask(true, true, true, true);
            GL.BindVertexArray(Field("vao"));
            void Program(string name, string metrics) {
                GL.UseProgram(Field(name)); GL.Uniform4(Field(metrics), 1f / width, 1f / height, (float)width, (float)height);
            }
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, Field("edgesFbo"));
            GL.ClearColor(0, 0, 0, 0); GL.ClearStencil(0); GL.StencilMask(255);
            GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.StencilBufferBit);
            GL.Enable(EnableCap.StencilTest); GL.StencilFunc(StencilFunction.Always, 1, 255);
            GL.StencilOp(StencilOp.Keep, StencilOp.Keep, StencilOp.Replace);
            Program("progEdge", "locRtEdge"); Bind(0, input); GL.DrawArrays(PrimitiveType.Triangles, 0, 3);
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, Field("blendFbo")); GL.Clear(ClearBufferMask.ColorBufferBit);
            GL.StencilFunc(StencilFunction.Equal, 1, 255); GL.StencilOp(StencilOp.Keep, StencilOp.Keep, StencilOp.Keep);
            Program("progWeight", "locRtWeight"); Bind(0, Field("edgesTex")); Bind(1, Field("areaTex")); Bind(2, Field("searchTex"));
            GL.DrawArrays(PrimitiveType.Triangles, 0, 3); GL.Disable(EnableCap.StencilTest);
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, Field("outFbo"));
            Program("progBlend", "locRtBlend"); Bind(0, input); Bind(1, Field("blendTex"));
            GL.DrawArrays(PrimitiveType.Triangles, 0, 3);
            if (sharpness > 0) {
                GL.BindFramebuffer(FramebufferTarget.Framebuffer, Field("sharpFbo"));
                Program("progSharp", "locSharpRt"); GL.Uniform1(Field("locSharpAmt"), sharpness);
                Bind(0, Field("outTex")); GL.DrawArrays(PrimitiveType.Triangles, 0, 3);
            }
            var result = new float[width * height * 4];
            GL.ReadBuffer(ReadBufferMode.ColorAttachment0);
            GL.ReadPixels(0, 0, width, height, PixelFormat.Rgba, PixelType.Float, result);
            Check(result.All(float.IsFinite), "finite SMAA RGB/alpha");
            return result;
        }
        void CheckTargets() {
            Check(Format("outTex") == (int)PixelInternalFormat.Rgba16f && Format("sharpTex") == (int)PixelInternalFormat.Rgba16f, "both SMAA scene targets RGBA16F");
            Check(Format("edgesTex") == (int)PixelInternalFormat.Rg8 && Format("blendTex") == (int)PixelInternalFormat.Rgba8, "normalized edge/weight targets retained");
        }
        try {
            system.GetField("Quality", BindingFlags.Static | BindingFlags.NonPublic)!.SetValue(null, 1);
            Initialize(); Call("EnsureTargets", 32, 24);
            Upload(32, 24, Image(32, 24, new Vector3(4, 2, 8)));
            float[] clipped = Draw(32, 24, 0);
            Check(clipped[0] == 1 && clipped[1] == 1 && clipped[2] == 1, "reproduce original SMAA RGBA8 clipping");
            Console.WriteLine("REPRO original SMAA maps HDR (4,2,8) to (1,1,1)");
            ((IDisposable)renderer).Dispose();
            using (var compatibility = new SmaaHdrCompatibility(api)) {
                renderer = Activator.CreateInstance(rendererType, api)!; Initialize();
                foreach (int quality in new[] { 1, 2, 3, 4 }) {
                    system.GetField("Quality", BindingFlags.Static | BindingFlags.NonPublic)!.SetValue(null, quality);
                    Call("RebuildPrograms"); Call("EnsureTargets", 32, 24); CheckTargets();
                    foreach (Vector3 color in new[] { Vector3.Zero, new Vector3(.2f, .4f, .6f), new Vector3(4, 2, 8), new Vector3(32, 2, .25f) }) {
                        Upload(32, 24, Image(32, 24, color));
                        foreach (float sharpness in new[] { 0f, .3f, 1f }) {
                            float[] actual = Draw(32, 24, sharpness);
                            for (int i = 0; i < actual.Length; i++) {
                                float expected = i % 4 == 3 ? .375f : color[i % 4];
                                Check(Math.Abs(actual[i] - expected) < Math.Max(.002f, expected * .002f), "constant HDR/black/material/alpha survives SMAA and sharpening");
                            }
                        }
                    }
                    Upload(32, 24, Image(32, 24, Vector3.Zero, true));
                    foreach (float sharpness in new[] { 0f, .3f, 1f }) {
                        float[] actual = Draw(32, 24, sharpness);
                        for (int i = 0; i < actual.Length; i++) Check(i % 4 == 3 ? Math.Abs(actual[i] - .375f) < .002f : actual[i] >= 1.99f && actual[i] <= 16.01f,
                            "SMAA HDR diagonal retains neighborhood range and alpha");
                        if (sharpness == 0) Check(actual.Where((_, i) => i % 4 != 3).Any(v => v > 2.1f && v < 15.9f), "real SMAA smooths HDR diagonal edges");
                    }
                    Console.WriteLine($"PASS actual SMAA quality={quality}: HDR/material/black/alpha, sharpening 0/.3/1, HDR diagonal AA");
                }
                // A new size and same-size deletion/recreation must re-promote;
                // cached ordinary frames must not replace or clear their colors.
                foreach (var (width, height) in new[] { (48, 20), (32, 24) }) {
                    Call("EnsureTargets", width, height); CheckTargets();
                    Upload(width, height, Image(width, height, new Vector3(4, 2, 8)));
                    Check(Draw(width, height, .3f)[0] == 4, "resize preserves HDR SMAA");
                    Call("DeleteTargets"); Call("EnsureTargets", width, height); CheckTargets();
                }
                var ensure = rendererType.GetMethod("EnsureTargets", methods)!.CreateDelegate<Action<int, int>>(renderer);
                for (int i = 0; i < 20; i++) ensure(32, 24);
                long before = GC.GetAllocatedBytesForCurrentThread();
                for (int i = 0; i < 10000; i++) ensure(32, 24);
                Check(GC.GetAllocatedBytesForCurrentThread() == before, "cached SMAA compatibility allocates zero bytes");
                Console.WriteLine("PASS actual SMAA resize/same-size recreation and zero-allocation cached targets");

                // Test mutation with a bound PBO and unrelated active-unit/FBO
                // bindings; only the known target storage may change.
                int temporary = GL.GenTexture(), fbo = GL.GenFramebuffer(), pbo = GL.GenBuffer();
                GL.ActiveTexture(TextureUnit.Texture7); GL.BindTexture(TextureTarget.Texture2D, temporary);
                GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba8, 2, 2, 0, PixelFormat.Rgba, PixelType.UnsignedByte, IntPtr.Zero);
                GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer, fbo);
                GL.FramebufferTexture2D(FramebufferTarget.DrawFramebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, temporary, 0);
                GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer, Field("outFbo"));
                GL.BindFramebuffer(FramebufferTarget.ReadFramebuffer, Field("blendFbo")); GL.BindTexture(TextureTarget.Texture2D, input);
                GL.BindBuffer(BufferTarget.PixelUnpackBuffer, pbo); GL.BufferData(BufferTarget.PixelUnpackBuffer, 64, IntPtr.Zero, BufferUsageHint.StaticDraw);
                SmaaHdrCompatibility.PromoteColorTarget(temporary, fbo, 2, 2);
                Check(GL.GetInteger(GetPName.PixelUnpackBufferBinding) == pbo && GL.GetInteger(GetPName.ActiveTexture) == (int)TextureUnit.Texture7
                    && GL.GetInteger(GetPName.TextureBinding2D) == input && GL.GetInteger(GetPName.DrawFramebufferBinding) == Field("outFbo")
                    && GL.GetInteger(GetPName.ReadFramebufferBinding) == Field("blendFbo"), "SMAA HDR promotion restores all touched bindings");
                GL.BindBuffer(BufferTarget.PixelUnpackBuffer, 0); GL.DeleteBuffer(pbo); GL.DeleteFramebuffer(fbo); GL.DeleteTexture(temporary);
                Check(warnings == 0, "SMAA compatibility installed without warnings");
                ((IDisposable)renderer).Dispose();
            }
            renderer = Activator.CreateInstance(rendererType, api)!; Initialize(); Call("EnsureTargets", 32, 24);
            Check(Format("outTex") == (int)PixelInternalFormat.Rgba8, "SMAA compatibility unpatches on disposal");
            Check(GL.GetError() == ErrorCode.NoError, "SMAA HDR GL NoError");
            Console.WriteLine("PASS production mutation binding/PBO restoration, clean disposal/unpatch and GL NoError");
        }
        finally { ((IDisposable)renderer).Dispose(); GL.DeleteTexture(input); }
    }
}
