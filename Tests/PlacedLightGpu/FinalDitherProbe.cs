using System;
using System.IO;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;

// Numeric display-stage probe: distinguish apparent pixel holes from coverage.
internal static class FinalDitherProbe
{
    internal static void Run(string assets)
    {
        using var window = new GameWindow(GameWindowSettings.Default, new NativeWindowSettings {
            StartVisible = false, ClientSize = new Vector2i(32, 32), API = ContextAPI.OpenGL,
            APIVersion = new Version(4, 3), Profile = ContextProfile.Core });
        window.MakeCurrent(); GL.LoadBindings(new OpenTK.Windowing.GraphicsLibraryFramework.GLFWBindingsContext());
        string dither = File.ReadAllText(Path.Combine(assets, "game/shaderincludes/dither.fsh"));
        string grading = File.ReadAllText(Path.Combine(assets, "drtagx/shaders/DRTAgXKraken.fsh"));
        string final = File.ReadAllText(Path.Combine(assets, "game/shaders/final.fsh"));
        if (!final.Contains("outColor = ColorGrade(color);", StringComparison.Ordinal) ||
            final.Contains("drtDisplayNoise", StringComparison.Ordinal))
            throw new Exception("Native final pass still requests display noise");
        foreach (var format in new[] { PixelInternalFormat.Rgba16f, PixelInternalFormat.Rgba32f })
        {
            string vertex = "#version 330 core\nout vec2 texCoord;void main(){vec2 p=vec2((gl_VertexID<<1)&2,gl_VertexID&2);texCoord=p;gl_Position=vec4(p*2.-1.,0,1);}";
            string fragment = "#version 330 core\n" + """
                in vec2 texCoord;
                uniform float contrastLevel=0,gammaLevel=1,extraGamma=1,brightnessLevel=1,sepiaLevel=0,glitchEffectStrength=0,windWaveCounter=0;
                uniform vec3 sceneRgb;
                uniform int probeSeed;
                uniform float sceneAlpha;
                const vec3 LUM_VEC=vec3(.2126,.7152,.0722);
                float gnoise(vec3 p){return 0.;}
                out vec4 color;
                // Also exercise fallback callers of the native include: the returned
                // vec4 must be zero, including alpha, for every pixel and seed.
                """ + dither + grading + "\nvoid main(){color=ColorGrade(vec4(sceneRgb,sceneAlpha))+NoiseFromPixelPosition(ivec2(gl_FragCoord.xy),probeSeed,2662);}";
            int program = ProbeShader.Program((ShaderType.VertexShader, vertex), (ShaderType.FragmentShader, fragment));
            int vao = GL.GenVertexArray(), output = GL.GenTexture(), fbo = GL.GenFramebuffer();
            GL.BindVertexArray(vao); GL.BindTexture(TextureTarget.Texture2D, output);
            GL.TexImage2D(TextureTarget.Texture2D, 0, format, 32, 32, 0, PixelFormat.Rgba, PixelType.Float, IntPtr.Zero);
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, fbo); GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, output, 0);
            GL.Viewport(0, 0, 32, 32); GL.Disable(EnableCap.Blend); GL.Disable(EnableCap.DepthTest); GL.UseProgram(program);
            float[] pixels = new float[32 * 32 * 4], previous = new float[pixels.Length];
            foreach (float alpha in new[] { .25f, .5f, 1f })
            foreach (float rgb in new[] { 0f, .001f, .01f, .05f, .1f, .25f, 1f, 8f, 32f })
            {
                GL.Uniform3(GL.GetUniformLocation(program, "sceneRgb"), rgb, rgb * .5f, rgb * .25f);
                GL.Uniform1(GL.GetUniformLocation(program, "sceneAlpha"), alpha);
                GL.DrawArrays(PrimitiveType.Triangles, 0, 3); GL.ReadPixels(0, 0, 32, 32, PixelFormat.Rgba, PixelType.Float, pixels);
                float clean = pixels[0], min = float.PositiveInfinity, max = float.NegativeInfinity;
                int negative = 0, changing = 0;
                for (int seed = 1; seed <= 2; ++seed)
                {
                    GL.Uniform1(GL.GetUniformLocation(program, "probeSeed"), seed * 314159);
                    GL.DrawArrays(PrimitiveType.Triangles, 0, 3); GL.ReadPixels(0, 0, 32, 32, PixelFormat.Rgba, PixelType.Float, pixels);
                    for (int i = 0; i < pixels.Length; i += 4)
                    {
                        if (pixels[i + 3] != alpha) throw new Exception("Display alpha changed");
                        for (int channel = 0; channel < 3; ++channel) {
                            if (!float.IsFinite(pixels[i + channel]) || pixels[i + channel] < 0) throw new Exception("Display RGB negative/nonfinite");
                            if (pixels[i + channel] != pixels[channel]) throw new Exception("Spatial display noise remains");
                            if (seed == 2 && pixels[i + channel] != previous[i + channel]) throw new Exception("Animated display noise remains");
                        }
                        min = Math.Min(min, pixels[i]); max = Math.Max(max, pixels[i]);
                        if (pixels[i] < 0) ++negative;
                        if (seed == 2 && pixels[i] != previous[i]) ++changing;
                    }
                    Array.Copy(pixels, previous, pixels.Length);
                }
                if (negative != 0 || changing != 0 || min != clean || max != clean)
                    throw new Exception("Display output differs across pixels/seeds");
                Console.WriteLine($"PASS {format} scene={rgb:R}, displayR={clean:R}, spatialRange={max-min:R}, changingR={changing}/1024, alpha={alpha:R}");
            }
            if (GL.GetError() != ErrorCode.NoError) throw new Exception("Final probe GL error");
            GL.DeleteProgram(program); GL.DeleteVertexArray(vao); GL.DeleteTexture(output); GL.DeleteFramebuffer(fbo);
        }
        ProbeAo(assets);
    }

    private static void ProbeAo(string assets)
    {
        // Uniform planes must not acquire a screen-pixel pattern. Exercise the
        // actual AO fragment with axis-aligned and nearly parallel normals.
        string source = File.ReadAllText(Path.Combine(assets, "game/shaders/ssao.fsh"));
        string vertex = "#version 330 core\nout vec2 texcoord;void main(){vec2 p=vec2((gl_VertexID<<1)&2,gl_VertexID&2);texcoord=p;gl_Position=vec4(p*2.-1.,0,1);}";
        int vao=GL.GenVertexArray(), fbo=GL.GenFramebuffer();
        GL.BindVertexArray(vao); GL.BindFramebuffer(FramebufferTarget.Framebuffer,fbo);
        int Texture(int width, int height, float[] data)
        {
            int id=GL.GenTexture(); GL.BindTexture(TextureTarget.Texture2D,id);
            GL.TexImage2D(TextureTarget.Texture2D,0,PixelInternalFormat.Rgba16f,width,height,0,PixelFormat.Rgba,PixelType.Float,data);
            GL.TexParameter(TextureTarget.Texture2D,TextureParameterName.TextureMinFilter,(int)TextureMinFilter.Nearest);
            GL.TexParameter(TextureTarget.Texture2D,TextureParameterName.TextureMagFilter,(int)TextureMagFilter.Nearest);
            return id;
        }
        int position=Texture(1,1,[1,0,-10,0]), normal=Texture(1,1,[0,0,1,0]), reveal=Texture(1,1,[1,1,1,1]);
        int output=Texture(16,16,new float[16*16*4]);
        GL.FramebufferTexture2D(FramebufferTarget.Framebuffer,FramebufferAttachment.ColorAttachment0,TextureTarget.Texture2D,output,0);
        GL.Viewport(0,0,16,16);
        float[] samples=new float[64*3], pixels=new float[16*16*4];
        for(int i=0;i<64;++i) {
            samples[i*3]=.16f*(float)Math.Sin(i*2.4);
            samples[i*3+1]=.16f*(float)Math.Cos(i*2.4);
            samples[i*3+2]=.005f*(i+1);
        }
        int groups=0;
        foreach(int level in new[]{1,2}) {
            int program=ProbeShader.Program((ShaderType.VertexShader,vertex),(ShaderType.FragmentShader,
                source.Replace("#version 330 core",$"#version 330 core\n#define SSAOLEVEL {level}")));
            GL.UseProgram(program);
            GL.Uniform1(GL.GetUniformLocation(program,"gPosition"),0);
            GL.Uniform1(GL.GetUniformLocation(program,"gNormal"),1);
            GL.Uniform1(GL.GetUniformLocation(program,"revealage"),2);
            GL.Uniform3(GL.GetUniformLocation(program,"samples[0]"),level==2?24:20,samples);
            GL.UniformMatrix4(GL.GetUniformLocation(program,"projection"),1,false,new float[]{1,0,0,0,0,1,0,0,0,0,1,0,0,0,0,1});
            foreach(var direction in new[]{Vector3.UnitX,-Vector3.UnitX,Vector3.UnitY,-Vector3.UnitY,Vector3.UnitZ,-Vector3.UnitZ,
                Vector3.Normalize(new Vector3(.01f,0,1)),Vector3.Normalize(new Vector3(.1f,0,1))})
            foreach(float sign in new[]{0f,1f}) {
                GL.ActiveTexture(TextureUnit.Texture0); GL.BindTexture(TextureTarget.Texture2D,position);
                GL.ActiveTexture(TextureUnit.Texture1); GL.BindTexture(TextureTarget.Texture2D,normal);
                GL.TexSubImage2D(TextureTarget.Texture2D,0,0,0,1,1,PixelFormat.Rgba,PixelType.Float,new[]{direction.X,direction.Y,direction.Z,sign});
                GL.ActiveTexture(TextureUnit.Texture2); GL.BindTexture(TextureTarget.Texture2D,reveal);
                GL.DrawArrays(PrimitiveType.Triangles,0,3); GL.ReadPixels(0,0,16,16,PixelFormat.Rgba,PixelType.Float,pixels);
                for(int i=0;i<pixels.Length;i++) {
                    if(!float.IsFinite(pixels[i]) || pixels[i]<0 || pixels[i]>1 || pixels[i]!=pixels[i%4])
                        throw new Exception("SSAO is nonfinite or contains a screen-pixel pattern");
                    if(i%4==3 && pixels[i]!=1) throw new Exception("SSAO coverage changed");
                }
                ++groups;
            }
            GL.DeleteProgram(program);
        }
        if(GL.GetError()!=ErrorCode.NoError) throw new Exception("SSAO no-dither probe GL error");
        foreach(int id in new[]{position,normal,reveal,output}) GL.DeleteTexture(id);
        GL.DeleteFramebuffer(fbo); GL.DeleteVertexArray(vao);
        Console.WriteLine($"PASS {groups} SSAO groups: finite RGBA16F, axis/near-axis normals, solid/foliage metadata, zero pixel pattern, alpha one");
    }
}
