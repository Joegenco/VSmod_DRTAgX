using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using DRTAgX;
using OpenTK.Graphics.OpenGL4;

// Render the real Sheyder composite into native-like MRTs, then execute the
// production HDR downsample. Reflection RGB must not become emission metadata.
internal static class SsrGlowProbe
{
    internal static void Run(string deferredPath)
    {
        using var state = new HdrPassState();
        string root = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(deferredPath)!, "../.."));
        // Third-party shader sources come from a local installation, not the repository.
        using var zip = ZipFile.OpenRead(Environment.GetEnvironmentVariable("SHEYDER_MOD_ZIP") ??
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "VintagestoryData/Mods/SheyderMod 1.1.3.zip"));
        string Native(string name)
        {
            using var reader = new StreamReader(zip.GetEntry("assets/sheydermod/shaders/" + name)!.Open());
            return reader.ReadToEnd();
        }
        string Expand(string source)
        {
            var seen = new HashSet<string>();
            string Include(string name)
            {
                if (!seen.Add(name)) return "";
                foreach (string directory in new[] { Path.Combine(root, "game/shaders"), Path.Combine(root, "game/shaderincludes"),
                    Path.Combine(Environment.GetEnvironmentVariable("VINTAGE_STORY")!, "assets/game/shaderincludes") })
                {
                    string path = Path.Combine(directory, name);
                    if (File.Exists(path)) return Lines(File.ReadAllText(path));
                }
                return Lines(Native(name));
            }
            string Lines(string text)
            {
                var lines = text.Split('\n');
                for (int i = 0; i < lines.Length; ++i)
                    if (lines[i].Trim().StartsWith("#include ")) lines[i] = Include(lines[i].Trim()[9..]);
                return string.Join('\n', lines);
            }
            return Lines(source);
        }
        const string vertex = "#version 330 core\nout vec2 texcoord,texCoord;void main(){vec2 p=vec2((gl_VertexID<<1)&2,gl_VertexID&2);texcoord=texCoord=p;gl_Position=vec4(p*2.-1.,0,1);}";
        int original = ProbeShader.Program((ShaderType.VertexShader, vertex), (ShaderType.FragmentShader, Expand(Native("ssrcomposite.fsh"))));
        int patched = ProbeShader.Program((ShaderType.VertexShader, vertex), (ShaderType.FragmentShader,
            Expand(File.ReadAllText(Path.Combine(root, "sheydermod/shaders/ssrcomposite.fsh")))));
        int hdr = ProbeShader.Program((ShaderType.VertexShader, vertex), (ShaderType.FragmentShader,
            File.ReadAllText(Path.Combine(root, "drtagx/shaders/hdr_downsample.fsh"))));
        int srcRgb = GL.GetInteger(GetPName.BlendSrcRgb), dstRgb = GL.GetInteger(GetPName.BlendDstRgb);
        int srcAlpha = GL.GetInteger(GetPName.BlendSrcAlpha), dstAlpha = GL.GetInteger(GetPName.BlendDstAlpha);
        int eqRgb = GL.GetInteger(GetPName.BlendEquationRgb), eqAlpha = GL.GetInteger(GetPName.BlendEquationAlpha);
        int sceneFbo = GL.GenFramebuffer(), hdrFbo = GL.GenFramebuffer(), vao = GL.GenVertexArray();
        int[] targets = new int[4];
        float[][] background = { new[] { 1f, .5f, .25f, 1 }, new[] { 0f, 0, 0, 1 }, new[] { .2f, .3f, .4f, .7f }, new[] { 5f, 6, 7, .9f } };
        int Scalar(int unit, float[] rgba)
        {
            int texture = GL.GenTexture(); GL.ActiveTexture(TextureUnit.Texture0 + unit); GL.BindTexture(TextureTarget.Texture2D, texture);
            GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba32f, 1, 1, 0, PixelFormat.Rgba, PixelType.Float, rgba);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
            return texture;
        }
        int reflection = Scalar(0, new[] { 4f, 2, 1, 1 }), liquid = Scalar(1, new[] { .4f, 0, 0, 1 });
        int opaque = Scalar(2, new[] { .8f, 0, 0, 1 }), result = Scalar(3, new[] { 0f, 0, 0, 0 });
        float[] Read(int target)
        {
            GL.ReadBuffer(ReadBufferMode.ColorAttachment0 + target);
            var pixel = new float[4]; GL.ReadPixels(0, 0, 1, 1, PixelFormat.Rgba, PixelType.Float, pixel); return pixel;
        }
        static void Equal(float[] a, float[] b, string name, int count = 4)
        {
            for (int c = 0; c < count; ++c)
                if (!float.IsFinite(a[c]) || Math.Abs(a[c] - b[c]) > 2e-5f) throw new Exception($"{name}: channel {c}, {a[c]} vs {b[c]}");
        }
        float[] Draw(int program, bool debug = false)
        {
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, sceneFbo);
            for (int i = 0; i < 4; i++) GL.ClearBuffer(ClearBuffer.Color, i, background[i]);
            GL.UseProgram(program); GL.Uniform1(GL.GetUniformLocation(program, "ssrTex"), 0);
            GL.Uniform1(GL.GetUniformLocation(program, "liquidDepth"), 1); GL.Uniform1(GL.GetUniformLocation(program, "sceneDepth"), 2);
            float[] identity = { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1 };
            GL.UniformMatrix4(GL.GetUniformLocation(program, "ssr_invProj"), 1, false, identity);
            GL.UniformMatrix4(GL.GetUniformLocation(program, "ssr_invModelView"), 1, false, identity);
            GL.Uniform1(GL.GetUniformLocation(program, "ssr_strength"), .5f); GL.Uniform1(GL.GetUniformLocation(program, "ssr_debug"), debug ? 1f : 0f);
            foreach (var (unit, texture) in new[] { (0, reflection), (1, liquid), (2, opaque) })
            { GL.ActiveTexture(TextureUnit.Texture0 + unit); GL.BindTexture(TextureTarget.Texture2D, texture); }
            if (debug) GL.Disable(EnableCap.Blend); else GL.Enable(EnableCap.Blend);
            GL.BlendEquation(BlendEquationMode.FuncAdd); GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
            GL.DrawArrays(PrimitiveType.Triangles, 0, 3); return Read(0);
        }
        float[] Bloom()
        {
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, hdrFbo); GL.Disable(EnableCap.Blend); GL.UseProgram(hdr);
            GL.ActiveTexture(TextureUnit.Texture0); GL.BindTexture(TextureTarget.Texture2D, targets[0]);
            GL.ActiveTexture(TextureUnit.Texture1); GL.BindTexture(TextureTarget.Texture2D, targets[1]);
            GL.Uniform1(GL.GetUniformLocation(hdr, "source"), 0); GL.Uniform1(GL.GetUniformLocation(hdr, "glowParts"), 1);
            GL.Uniform1(GL.GetUniformLocation(hdr, "emissiveFirstLevel"), 1); GL.Uniform2(GL.GetUniformLocation(hdr, "texelSize"), 1f, 1f);
            GL.DrawArrays(PrimitiveType.Triangles, 0, 3); return Read(0);
        }
        try
        {
            GL.BindVertexArray(vao); GL.Viewport(0, 0, 1, 1);
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, sceneFbo);
            for (int i = 0; i < 4; ++i)
            {
                targets[i] = Scalar(3, background[i]);
                GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0 + i, TextureTarget.Texture2D, targets[i], 0);
            }
            GL.DrawBuffers(4, new[] { DrawBuffersEnum.ColorAttachment0, DrawBuffersEnum.ColorAttachment1, DrawBuffersEnum.ColorAttachment2, DrawBuffersEnum.ColorAttachment3 });
            if (GL.CheckFramebufferStatus(FramebufferTarget.Framebuffer) != FramebufferErrorCode.FramebufferComplete) throw new Exception("SSR probe MRT framebuffer");
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, hdrFbo);
            GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, result, 0);
            GL.DrawBuffer(DrawBufferMode.ColorAttachment0);
            if (GL.CheckFramebufferStatus(FramebufferTarget.Framebuffer) != FramebufferErrorCode.FramebufferComplete) throw new Exception("SSR probe HDR framebuffer");
            float[] oldScene = Draw(original), oldGlow = Read(1);
            var newScene = Draw(patched);
            Equal(newScene, oldScene, "SSR color/alpha changed");
            for (int i = 1; i < 4; i++) Equal(Read(i), background[i], "SSR changed auxiliary attachment " + i);
            Console.WriteLine($"SSR original undefined glow.G={oldGlow[1]:R}; corrected glow.G={Read(1)[1]:R}");
            Equal(Bloom(), newScene, "SSR reflection was given an emissive multiplier", 3);
            Console.WriteLine("PASS actual SSR MRT: unchanged HDR reflection/alpha, exact glow/normal/position preservation; water has no emissive boost");
            foreach (var (red, green) in new[] { (.5f, 0f), (0f, .2f), (.5f, .2f), (0f, -.7f) })
            {
                background[1][0] = red; background[1][1] = green;
                var scene = Draw(patched); Equal(Read(1), background[1], "Existing emission metadata changed");
                var bloom = Bloom(); float[] expected = new float[4];
                for (int c = 0; c < 3; c++)
                {
                    float r = scene[c] * (1 + red * 3);
                    // Shipping extraction is linear G after the R gain, with .85 blue.
                    // Historical .44/32/.90 tuning is not the 2.0.1 source baseline.
                    expected[c] = r * (1 + Math.Max(green, 0) * (c == 0 ? 1.2f : c == 1 ? 1 : .85f));
                }
                Equal(bloom, expected, "Authored emissive gains changed", 3);
                Console.WriteLine($"PASS SSR/HDR retains authored R={red}, G={green} gains; signed G cannot produce NaN");
            }
            GL.ActiveTexture(TextureUnit.Texture1); GL.BindTexture(TextureTarget.Texture2D, liquid);
            foreach (float depth in new[] { 1f, .9f })
            {
                GL.TexSubImage2D(TextureTarget.Texture2D, 0, 0, 0, 1, 1, PixelFormat.Rgba, PixelType.Float, new[] { depth, 0f, 0, 1 });
                Equal(Draw(patched), background[0], "Dry/occluded pixel scene changed");
                for (int i = 1; i < 4; i++) Equal(Read(i), background[i], "Dry/occluded metadata changed");
            }
            Console.WriteLine("PASS SSR dry/occluded pixels preserve all native attachments");
            Draw(patched, true); Equal(Read(1), new float[4], "SSR debug created glow");
            Console.WriteLine("PASS SSR debug view has zero emissive metadata");
            if (GL.GetError() != ErrorCode.NoError) throw new Exception("SSR/HDR probe GL error");
        }
        finally
        {
            GL.BlendFuncSeparate((BlendingFactorSrc)srcRgb, (BlendingFactorDest)dstRgb, (BlendingFactorSrc)srcAlpha, (BlendingFactorDest)dstAlpha);
            GL.BlendEquationSeparate((BlendEquationMode)eqRgb, (BlendEquationMode)eqAlpha);
            foreach (int texture in targets) GL.DeleteTexture(texture);
            foreach (int texture in new[] { reflection, liquid, opaque, result }) GL.DeleteTexture(texture);
            foreach (int program in new[] { original, patched, hdr }) GL.DeleteProgram(program);
            GL.DeleteVertexArray(vao); GL.DeleteFramebuffer(sceneFbo); GL.DeleteFramebuffer(hdrFbo);
        }
    }
}
