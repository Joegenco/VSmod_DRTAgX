using System;
using System.IO;
using System.IO.Compression;
using OpenTK.Graphics.OpenGL4;

/// <summary>Independent captured/optimized GLSL comparison; numerical values only, no frame images.</summary>
internal static class LensFlareProbe
{
    internal static void Run(string assets)
    {
        // Compare with the original installed provider, without vendoring its assets.
        using var archive = ZipFile.OpenRead(Environment.GetEnvironmentVariable("SHEYDER_MOD_ZIP") ??
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "VintagestoryData/Mods/SheyderMod 1.1.3.zip"));
        int Program(string directory) {
            string Read(string name) {
                if (directory != null) return File.ReadAllText(Path.Combine(directory, name));
                using var reader = new StreamReader((archive.GetEntry("assets/sheydermod/shaders/" + name)
                    ?? throw new FileNotFoundException(name)).Open());
                return reader.ReadToEnd();
            }
            string vertex = "#version 430 core\nout vec2 texCoord,invFrameSize;uniform vec3 probeSun;\n" + Read("lensflare.vsh") +
                "\nvoid main(){vec2 p=vec2((gl_VertexID<<1)&2,gl_VertexID&2);texCoord=p;gl_Position=vec4(p*2.-1.,0,1);invFrameSize=vec2(1./64.,1./32.);lf_forward(probeSun,vec3(0,0,1),vec3(0,0,1));}";
            string fragment = "#version 430 core\nin vec2 texCoord,invFrameSize;out vec4 result;\n" + Read("lensflare.fsh") +
                "\nvoid main(){result=vec4(lf_apply(vec3(0)),1);}";
            return ProbeShader.Program((ShaderType.VertexShader,vertex),(ShaderType.FragmentShader,fragment));
        }
        using var saved = new DRTAgX.HdrPassState();
        int reference=Program(null), candidate=Program(Path.Combine(assets,"sheydermod/shaders"));
        int input=GL.GenTexture(), output=GL.GenTexture(), fbo=GL.GenFramebuffer(), vao=GL.GenVertexArray();
        float[] before=new float[64*32*4], after=new float[before.Length];
        float maxError=0;
        try {
            GL.BindVertexArray(vao); GL.ActiveTexture(TextureUnit.Texture0);
            GL.BindTexture(TextureTarget.Texture2D,input);
            GL.TexImage2D(TextureTarget.Texture2D,0,PixelInternalFormat.Rgba32f,1,1,0,PixelFormat.Rgba,PixelType.Float,new float[]{2,2,2,1});
            GL.TexParameter(TextureTarget.Texture2D,TextureParameterName.TextureMinFilter,(int)TextureMinFilter.Nearest);
            GL.TexParameter(TextureTarget.Texture2D,TextureParameterName.TextureMagFilter,(int)TextureMagFilter.Nearest);
            GL.BindTexture(TextureTarget.Texture2D,output);
            GL.TexImage2D(TextureTarget.Texture2D,0,PixelInternalFormat.Rgba32f,64,32,0,PixelFormat.Rgba,PixelType.Float,IntPtr.Zero);
            GL.BindFramebuffer(FramebufferTarget.Framebuffer,fbo);
            GL.FramebufferTexture2D(FramebufferTarget.Framebuffer,FramebufferAttachment.ColorAttachment0,TextureTarget.Texture2D,output,0);
            GL.DrawBuffer(DrawBufferMode.ColorAttachment0); GL.Viewport(0,0,64,32);
            GL.BindTexture(TextureTarget.Texture2D,input);
            foreach(float x in new[]{-1.5f,-1.2f,-.8f,0f,.6f,1.3f,2f})
            foreach(float y in new[]{-.53125f,.03125f,.46875f})
            foreach(float occlusion in new[]{0f,.1f,1f}) {
                foreach(int program in new[]{reference,candidate}) {
                    GL.UseProgram(program);
                    GL.Uniform3(GL.GetUniformLocation(program,"probeSun"),x,y,1f);
                    GL.Uniform1(GL.GetUniformLocation(program,"primaryScene"),0);
                    GL.Uniform1(GL.GetUniformLocation(program,"lf_Intensity"),8f);
                    GL.Uniform1(GL.GetUniformLocation(program,"lf_Brightness"),3f);
                    GL.Uniform1(GL.GetUniformLocation(program,"lf_SunOcclusion"),occlusion);
                    GL.DrawArrays(PrimitiveType.Triangles,0,3);
                    GL.ReadPixels(0,0,64,32,PixelFormat.Rgba,PixelType.Float,program==reference?before:after);
                }
                for(int i=0;i<before.Length;++i) {
                    float error=Math.Abs(before[i]-after[i]); maxError=Math.Max(maxError,error);
                    if(!float.IsFinite(after[i]) || error > 2e-5f*Math.Max(1f,Math.Abs(before[i])))
                        throw new Exception($"Flare parity x={x}, y={y}, occlusion={occlusion}, component={i}, error={error}");
                }
            }
            if(GL.GetError()!=ErrorCode.NoError) throw new Exception("Flare fixture GL error");
            Console.WriteLine($"PASS lens flare: 63 captured/optimized HDR, narrow-streak, offscreen and occlusion cases; max absolute error {maxError:G6}");
        }
        finally { GL.UseProgram(0); GL.DeleteProgram(reference); GL.DeleteProgram(candidate); GL.DeleteTexture(input); GL.DeleteTexture(output); GL.DeleteFramebuffer(fbo); GL.DeleteVertexArray(vao); }
    }
}
