using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DRTAgX;
using OpenTK.Graphics.OpenGL4;

/// <summary>Run maintained celestial vertex/fragment, SSAO and final composition through real HDR attachments.</summary>
internal static class CelestialOcclusionProbe
{
    private const int Size = 16;
    private const string Fullscreen = "#version 430 core\nout vec2 texcoord;void main(){vec2 p=vec2(gl_VertexID==1?3:-1,gl_VertexID==2?3:-1);texcoord=p*.5+.5;gl_Position=vec4(p,0,1);}";

    internal static void Run(string directory)
    {
        string game = Path.GetFullPath(Path.Combine(directory, "../../../game/shaders"));
        var seen = new HashSet<string>();
        string Expand(string name)
        {
            if (!seen.Add(name)) return "";
            return string.Join('\n', File.ReadAllLines(Path.Combine(directory, name)).Select(line =>
                line.Trim().StartsWith("#include ") ? Expand(line.Trim()[9..]) : line));
        }
        string WithoutIncludes(string source) => string.Join('\n', source.Split('\n').Where(line => !line.Trim().StartsWith("#include ")));
        string nativeVertex = WithoutIncludes(File.ReadAllText(Path.Combine(game, "celestialobject.vsh")))
            .Replace("#version 330 core", "#version 430 core\n#define SSAOLEVEL 2\nout float glowLevel;\nfloat getSpheresFogAmount(vec3 p){return 0;}");
        string shared = Expand("drtagx_atmospheric_sky.fsh") + "\n" + """
            const float zNear=.1,zFar=10000;
            vec4 applyFog(vec4 c,float f){return c;}
            vec4 applySpheresFog(vec4 c,float f,vec3 p){return c;}
            """ + "\n" + Expand("drtagx_water_transport.fsh") + "\n" + Expand("drtagx_celestial_balance.ash");
        string nativeFragment = WithoutIncludes(File.ReadAllText(Path.Combine(game, "celestialobject.fsh")))
            .Replace("#version 330 core", "#version 430 core\n#define SSAOLEVEL 2\n" + shared + "\n");
        int celestial = ProbeShader.Program((ShaderType.VertexShader, nativeVertex), (ShaderType.FragmentShader, nativeFragment));
        int ssao = ProbeShader.Program((ShaderType.VertexShader, Fullscreen), (ShaderType.FragmentShader,
            File.ReadAllText(Path.Combine(game, "ssao.fsh")).Replace("#version 330 core", "#version 330 core\n#define SSAOLEVEL 2")));
        // Preserve final's actual AO/glow math. Identity grading and inert additive
        // effects isolate HDR radiance before the user-configured display transform.
        string finalSource = "#version 430 core\n#define SSAOLEVEL 2\n#define BLOOM 0\n#define GODRAYS 0\n#define FXAA 0\n" + """
            in vec2 texcoord;
            #define texCoord texcoord
            const vec2 invFrameSize=vec2(1.0/16.0);
            const int horizontalResolution=16,dithseed=1,drtBloomReady=0;
            const float windWaveCounter=0,frostVignetting=0,damageVignetting=0,damageVignettingSide=0,ambientBloomLevel=0;
            const vec4 drtAtmosphereScreen=vec4(16,16,1,0),drtAtmosphereSun=vec4(0,1,0,1);
            uniform sampler2D primaryScene,glowParts,ssaoScene;
            layout(location=0) out vec4 outColor;
            vec4 NoiseFromPixelPosition(ivec2 p,int s,int r){return vec4(0);}
            float gnoise(vec3 p){return 0;}
            vec3 vf_compositeVolumetric(vec3 c,vec2 uv){return c;}
            vec3 lf_apply(vec3 c){return c;}
            float drtCelestialVisibility(vec3 p){return 1;}
            vec4 ColorGrade(vec4 c,vec4 d){return c;}
            """ + "\n" + ProbeShader.Function(File.ReadAllText(Path.Combine(game, "final.fsh")), "void main(");
        int final = ProbeShader.Program((ShaderType.VertexShader, Fullscreen), (ShaderType.FragmentShader, finalSource));
        int[] images = Enumerable.Range(0, 6).Select(_ => Target()).ToArray();
        int lut = Texture(7, .3f, .4f, .5f, 1), tex = Texture(8, 1, 1, 1, 1), white = Texture(6, 1, 1, 1, 1);
        int scene = GL.GenFramebuffer(), post = GL.GenFramebuffer(), vao = GL.GenVertexArray(), vertices = GL.GenBuffer();
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, scene);
        for (int i = 0; i < 4; i++) GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0 + i, TextureTarget.Texture2D, images[i], 0);
        GL.DrawBuffers(4, new[] { DrawBuffersEnum.ColorAttachment0, DrawBuffersEnum.ColorAttachment1, DrawBuffersEnum.ColorAttachment2, DrawBuffersEnum.ColorAttachment3 });
        Check(GL.CheckFramebufferStatus(FramebufferTarget.Framebuffer) == FramebufferErrorCode.FramebufferComplete, "celestial HDR MRT complete");
        GL.BindVertexArray(vao); GL.BindBuffer(BufferTarget.ArrayBuffer, vertices);
        float[] triangle = { -1,-1,0, 3,-1,0, -1,3,0 };
        GL.BufferData(BufferTarget.ArrayBuffer, triangle.Length * 4, triangle, BufferUsageHint.StaticDraw);
        GL.EnableVertexAttribArray(0); GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 12, 0);
        GL.VertexAttrib2(1, .5f, .5f); GL.VertexAttrib4(2, 1f, 1f, 1f, 1f); GL.VertexAttribI1(3, 0);
        GL.Viewport(0, 0, Size, Size); GL.Disable(EnableCap.DepthTest); GL.Disable(EnableCap.Blend);
        GL.Disable(EnableCap.ScissorTest); GL.Disable(EnableCap.CullFace); GL.ColorMask(true, true, true, true);
        float[] identity = new float[16]; for (int i = 0; i < 4; i++) identity[i * 5] = 1;
        float[] model = (float[])identity.Clone(); model[14] = -100;
        float[] projection = (float[])identity.Clone(); projection[10] = 0;
        GL.UseProgram(celestial);
        GL.UniformMatrix4(GL.GetUniformLocation(celestial, "modelMatrix"), 1, false, model);
        GL.UniformMatrix4(GL.GetUniformLocation(celestial, "viewMatrix"), 1, false, identity);
        GL.UniformMatrix4(GL.GetUniformLocation(celestial, "projectionMatrix"), 1, false, projection);
        GL.Uniform1(GL.GetUniformLocation(celestial, "extraGlow"), 128);
        foreach (var (name, unit) in new[] { ("tex", 8), ("drtSkyViewPrevious", 7), ("drtSkyViewCurrent", 7), ("drtFogVolume", 5), ("liquidDepth", 6) })
            GL.Uniform1(GL.GetUniformLocation(celestial, name), unit);
        GL.Uniform2(GL.GetUniformLocation(celestial, "frameSize"), (float)Size, (float)Size);
        GL.Uniform3(GL.GetUniformLocation(celestial, "moonPosition"), 0f, 1f, 0f);
        GL.Uniform3(GL.GetUniformLocation(celestial, "sunPosition"), 0f, 1f, 0f);
        GL.Uniform1(GL.GetUniformLocation(celestial, "dayLight"), 1f);
        GL.UniformBlockBinding(celestial, GL.GetUniformBlockIndex(celestial, "DrtAtmosphere"), 10);
        GL.UseProgram(ssao);
        foreach (var (name, unit) in new[] { ("gPosition", 3), ("gNormal", 2), ("revealage", 6), ("texNoise", 6) }) GL.Uniform1(GL.GetUniformLocation(ssao, name), unit);
        GL.Uniform2(GL.GetUniformLocation(ssao, "screenSize"), (float)Size, (float)Size);
        GL.UniformMatrix4(GL.GetUniformLocation(ssao, "projection"), 1, false, identity);
        GL.UseProgram(final);
        foreach (var (name, unit) in new[] { ("primaryScene", 0), ("glowParts", 1), ("ssaoScene", 4) }) GL.Uniform1(GL.GetUniformLocation(final, name), unit);
        var frame = new float[AtmosphereRenderer.FrameFloatCount];
        frame[0] = frame[1] = frame[2] = .2f; frame[3] = frame[24] = frame[27] = 1; frame[9] = 200;
        int buffer = GL.GenBuffer(); GL.BindBuffer(BufferTarget.UniformBuffer, buffer);
        GL.BufferData(BufferTarget.UniformBuffer, frame.Length * 4, frame, BufferUsageHint.DynamicDraw); GL.BindBufferBase(BufferRangeTarget.UniformBuffer, 10, buffer);
        int draws = 0;
        float[] Read(int attachment)
        {
            GL.ReadBuffer(ReadBufferMode.ColorAttachment0 + attachment);
            var pixels = new float[Size * Size * 4]; GL.ReadPixels(0, 0, Size, Size, PixelFormat.Rgba, PixelType.Float, pixels); return pixels;
        }
        try
        {
            foreach (float elevation in new[] { 90f, 0f, -30f })
            foreach (float density in new[] { 0f, .0001f, 1f })
            foreach (bool moon in new[] { false, true })
            foreach (float emission in new[] { 0f, 1f })
            {
                frame[13] = MathF.Sin(elevation * MathF.PI / 180); frame[4] = density;
                GL.BindBuffer(BufferTarget.UniformBuffer, buffer); GL.BufferSubData(BufferTarget.UniformBuffer, IntPtr.Zero, frame.Length * 4, frame);
                GL.ActiveTexture(TextureUnit.Texture8); GL.BindTexture(TextureTarget.Texture2D, tex);
                GL.TexSubImage2D(TextureTarget.Texture2D, 0, 0, 0, 1, 1, PixelFormat.Rgba, PixelType.Float, new[] { emission, emission, emission, 1f });
                GL.BindFramebuffer(FramebufferTarget.Framebuffer, scene); GL.UseProgram(celestial);
                GL.Uniform1(GL.GetUniformLocation(celestial, "weirdMathToMakeMoonLookNicer"), moon ? 1 : 0);
                GL.DrawArrays(PrimitiveType.Triangles, 0, 3); draws++;
                var primary = Read(0); var normal = Read(2); var position = Read(3);
                Check(normal.All(x => x == 0) && position.All(x => x == 0), "sun/moon use the native sky's non-geometry sentinel");
                Check(primary.All(float.IsFinite), "finite celestial radiance");
                for (int i = 0; i < 4; i++) { GL.ActiveTexture(TextureUnit.Texture0 + i); GL.BindTexture(TextureTarget.Texture2D, images[i]); }
                GL.BindFramebuffer(FramebufferTarget.Framebuffer, post);
                GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, images[4], 0);
                GL.DrawBuffer(DrawBufferMode.ColorAttachment0); GL.UseProgram(ssao); GL.DrawArrays(PrimitiveType.Triangles, 0, 3); draws++;
                Check(Read(0).All(x => Math.Abs(x - 1) < .0001f), "actual SSAO leaves fogged celestial pixels unoccluded");
                GL.ActiveTexture(TextureUnit.Texture4); GL.BindTexture(TextureTarget.Texture2D, images[4]);
                GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, images[5], 0);
                GL.UseProgram(final); GL.DrawArrays(PrimitiveType.Triangles, 0, 3); draws++;
                var completed = Read(0);
                for (int i = 0; i < completed.Length; i++) Near(completed[i], primary[i], "actual final composition preserves celestial HDR/fog background");
                if (density == 1)
                {
                    // Retained exposure: 6 at zenith, 2.350835 at the solar
                    // horizon, 0.5 at night (the horizon ramp is not yet 3).
                    float gain = elevation == 90 ? 6 : elevation == -30 ? .5f : 2.3508353f;
                    for (int i = 0; i < completed.Length; i++) Near(completed[i], i % 4 == 3 ? 1 : .2f * gain, "fully fogged sun/moon match weather sky rather than black");
                }
            }
            Console.WriteLine($"PASS actual celestial vertex/fragment -> SSAO level 2 -> final HDR: {draws} draws, {draws * Size * Size} pixels; sun/moon, zero/bright texture, clear/hazy/opaque fog, day/sunset/night");
        }
        finally
        {
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0); GL.BindVertexArray(0); GL.UseProgram(0);
            GL.BindBufferBase(BufferRangeTarget.UniformBuffer, 10, 0);
            foreach (int p in new[] { celestial, ssao, final }) GL.DeleteProgram(p);
            foreach (int t in images.Concat(new[] { lut, tex, white })) GL.DeleteTexture(t);
            GL.DeleteFramebuffer(scene); GL.DeleteFramebuffer(post); GL.DeleteVertexArray(vao); GL.DeleteBuffer(vertices); GL.DeleteBuffer(buffer);
        }
        Check(GL.GetError() == ErrorCode.NoError, "celestial occlusion GL NoError");
    }
    private static int Target()
    {
        int t = GL.GenTexture(); GL.BindTexture(TextureTarget.Texture2D, t);
        GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba32f, Size, Size, 0, PixelFormat.Rgba, PixelType.Float, IntPtr.Zero);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest); return t;
    }
    private static int Texture(int unit, params float[] pixel)
    {
        GL.ActiveTexture(TextureUnit.Texture0 + unit); int t = GL.GenTexture(); GL.BindTexture(TextureTarget.Texture2D, t);
        GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba32f, 1, 1, 0, PixelFormat.Rgba, PixelType.Float, pixel);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest); return t;
    }
    private static void Near(float actual, float expected, string message) => Check(float.IsFinite(actual) && Math.Abs(actual - expected) < .0001f * Math.Max(1, Math.Abs(expected)), $"{message}: {actual} vs {expected}");
    private static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
}
