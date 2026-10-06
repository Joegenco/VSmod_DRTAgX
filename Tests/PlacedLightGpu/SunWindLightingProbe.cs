using System;
using System.IO;
using DRTAgX;
using OpenTK.Graphics.OpenGL4;

// Numerical draw fixtures execute maintained cloud, alpha and forward lighting
// code. They do not substitute screenshots for the human appearance review.
internal static class SunWindLightingProbe
{
    internal static void Run(string deferredPath)
    {
        using var state = new ShadowGlState();
        string assets = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(deferredPath)!, "../.."));
        string game = Path.Combine(assets, "game/shaders");
        string lighting = Path.Combine(assets, "drtagx/shaders/lighting");
        string vertex = "#version 430 core\nvoid main(){vec2 p=vec2((gl_VertexID<<1)&2,gl_VertexID&2);gl_Position=vec4(p*2-1,0,1);}";
        int output = GL.GenTexture(), fbo = GL.GenFramebuffer(), vao = GL.GenVertexArray();
        int scene = GL.GenTexture(), liquid = GL.GenTexture(), atlas = GL.GenTexture();
        int previous = GL.GetInteger(GetPName.CurrentProgram);
        try
        {
            GL.ActiveTexture(TextureUnit.Texture14); GL.BindSampler(14, 0);
            GL.BindTexture(TextureTarget.Texture2D, output);
            GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba32f, 1, 1, 0, PixelFormat.Rgba, PixelType.Float, IntPtr.Zero);
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, fbo);
            GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, output, 0);
            GL.DrawBuffer(DrawBufferMode.ColorAttachment0); GL.ReadBuffer(ReadBufferMode.ColorAttachment0);
            GL.BindVertexArray(vao); GL.Viewport(0, 0, 1, 1);
            GL.Disable(EnableCap.DepthTest); GL.Disable(EnableCap.ScissorTest); GL.Disable(EnableCap.Blend); GL.ColorMask(true, true, true, true);
            float[] Draw() {
                GL.ClearColor(0,0,0,0); GL.Clear(ClearBufferMask.ColorBufferBit); GL.DrawArrays(PrimitiveType.Triangles,0,3);
                float[] pixel = new float[4]; GL.ReadPixels(0,0,1,1,PixelFormat.Rgba,PixelType.Float,pixel);
                if (GL.GetError()!=ErrorCode.NoError) throw new Exception("Sun/wind fixture GL error");
                return pixel;
            }

            string cloud = File.ReadAllText(Path.Combine(game, "cloudvolumetric.fsh"));
            int program = ProbeShader.Program((ShaderType.VertexShader,vertex), (ShaderType.FragmentShader,
                "#version 430 core\nuniform sampler2D depthTex, liquidDepth;uniform vec4 drtAtmosphereScreen;uniform vec2 pixel;out vec4 color;\n" +
                ProbeShader.Function(cloud,"float drtCloudLiquidDepth(") + "\nvoid main(){color=vec4(drtCloudLiquidDepth(pixel));}"));
            try {
                GL.UseProgram(program); GL.Uniform1(GL.GetUniformLocation(program,"depthTex"),0); GL.Uniform1(GL.GetUniformLocation(program,"liquidDepth"),1);
                foreach(var size in new[]{(2880,1598,720,399),(37,21,9,5),(1920,1080,480,270)}) {
                    GL.ActiveTexture(TextureUnit.Texture0); GL.BindSampler(0,0); GL.BindTexture(TextureTarget.Texture2D,scene);
                    GL.TexImage2D(TextureTarget.Texture2D,0,PixelInternalFormat.R32f,size.Item1,size.Item2,0,PixelFormat.Red,PixelType.Float,IntPtr.Zero);
                    GL.TexParameter(TextureTarget.Texture2D,TextureParameterName.TextureMinFilter,(int)TextureMinFilter.Nearest);
                    GL.TexParameter(TextureTarget.Texture2D,TextureParameterName.TextureMagFilter,(int)TextureMagFilter.Nearest);
                    GL.ActiveTexture(TextureUnit.Texture1); GL.BindSampler(1,0); GL.BindTexture(TextureTarget.Texture2D,liquid);
                    float[] data=new float[size.Item3*size.Item4];
                    for(int i=0;i<data.Length;i++) data[i]=(i+1f)/(data.Length+1f);
                    GL.TexImage2D(TextureTarget.Texture2D,0,PixelInternalFormat.R32f,size.Item3,size.Item4,0,PixelFormat.Red,PixelType.Float,data);
                    GL.TexParameter(TextureTarget.Texture2D,TextureParameterName.TextureMinFilter,(int)TextureMinFilter.Linear);
                    GL.TexParameter(TextureTarget.Texture2D,TextureParameterName.TextureMagFilter,(int)TextureMagFilter.Linear);
                    GL.Uniform4(GL.GetUniformLocation(program,"drtAtmosphereScreen"),3840f,2131f,0f,1f);
                    foreach(var fraction in new[]{(.001f,.001f),(.5f,.5f),(.75f,.6f),(.999f,.999f)}) {
                        // Real fragments address pixel centres, not exact texel boundaries.
                        float x=MathF.Floor(fraction.Item1*size.Item1)+.5f,y=MathF.Floor(fraction.Item2*size.Item2)+.5f;
                        GL.Uniform2(GL.GetUniformLocation(program,"pixel"),x,y);
                        int ix=Math.Clamp((int)(x/size.Item1*size.Item3),0,size.Item3-1);
                        int iy=Math.Clamp((int)(y/size.Item2*size.Item4),0,size.Item4-1);
                        Near(Draw()[0],data[iy*size.Item3+ix],"cloud depth follows the scene ray at scaled/odd sizes");
                    }
                }
                GL.Uniform4(GL.GetUniformLocation(program,"drtAtmosphereScreen"),3840f,2131f,0f,0f);
                Near(Draw()[0],1f,"absent liquid depth leaves the cloud ray unobstructed");
            } finally { GL.DeleteProgram(program); }

            string shadowVertex = """
                #version 330 core
                uniform int windMode;
                uniform float phase;
                out vec2 uv;
                out vec3 drtShadowLocalPos;
                flat out int drtShadowWindMode;
                void main(){vec2 p=vec2((gl_VertexID<<1)&2,gl_VertexID&2);gl_Position=vec4(p*2-1,0,1);
                    uv=p*8+vec2(phase,0.0625);drtShadowLocalPos=vec3(p,0);drtShadowWindMode=windMode;}
                """;
            program=ProbeShader.Program((ShaderType.VertexShader,shadowVertex),(ShaderType.FragmentShader,File.ReadAllText(Path.Combine(game,"chunkshadowmap.fsh"))));
            try {
                GL.UseProgram(program); GL.Uniform1(GL.GetUniformLocation(program,"tex2d"),2);
                GL.ActiveTexture(TextureUnit.Texture2); GL.BindSampler(2,0); GL.BindTexture(TextureTarget.Texture2D,atlas);
                float[] pixels=new float[8*8*4];
                for(int y=0;y<8;y++) for(int x=0;x<8;x++) {int at=(y*8+x)*4;pixels[at]=pixels[at+1]=pixels[at+2]=1;pixels[at+3]=x==0?1:0;}
                GL.TexImage2D(TextureTarget.Texture2D,0,PixelInternalFormat.Rgba32f,8,8,0,PixelFormat.Rgba,PixelType.Float,pixels);
                GL.GenerateMipmap(GenerateMipmapTarget.Texture2D);
                GL.TexParameter(TextureTarget.Texture2D,TextureParameterName.TextureMinFilter,(int)TextureMinFilter.LinearMipmapLinear);
                GL.TexParameter(TextureTarget.Texture2D,TextureParameterName.TextureMagFilter,(int)TextureMagFilter.Nearest);
                GL.TexParameter(TextureTarget.Texture2D,TextureParameterName.TextureWrapS,(int)TextureWrapMode.Repeat);
                GL.TexParameter(TextureTarget.Texture2D,TextureParameterName.TextureWrapT,(int)TextureWrapMode.Repeat);
                GL.Uniform1(GL.GetUniformLocation(program,"phase"),.375f);
                GL.Uniform1(GL.GetUniformLocation(program,"windMode"),0);
                Check(Draw()[3]>.02f,"alpha fixture reproduces grazing mip card fill under the native low cutoff");
                GL.Uniform1(GL.GetUniformLocation(program,"windMode"),1);
                Near(Draw()[3],0,"grazing grass retains authored transparent gaps");
                GL.Uniform1(GL.GetUniformLocation(program,"phase"),.0625f);
                Near(Draw()[3],1,"grazing grass retains opaque blades");
            } finally { GL.DeleteProgram(program); }

            string forward = File.ReadAllText(Path.Combine(lighting,"drtagx_forward_placedlights.ash"));
            string balance = File.ReadAllText(Path.Combine(lighting,"drtagx_light_balance.ash"));
            string fog = File.ReadAllText(Path.Combine(game,"fogandlight.fsh"));
            string sunSampling = File.ReadAllText(Path.Combine(lighting,"drtagx_sun_shadow_sampling.fsh"));
            program=ProbeShader.Program((ShaderType.VertexShader,vertex),(ShaderType.FragmentShader,
                "#version 430 core\n#define SHADOWQUALITY 2\nvec3 drtSkyLight=vec3(0),drtForwardSunGridOffset=vec3(0),drtForwardSunPlaneNormal=vec3(0);uniform vec4 drtSunGridFrame,drtAtmosphereCamera;uniform vec3 receiver, normal, voxel, lightPosition;uniform float shadowIntensity;uniform int mode;out vec4 color;\n"+
                balance+forward+sunSampling+ProbeShader.Function(fog,"void drtPrepareSunGrid(")+ProbeShader.Function(fog,"float getBrightnessFromNormal(")+
                "\nvoid main(){if(mode==2){drtPrepareSunGrid(receiver,normal);color=vec4(receiver-drtAtmosphereCamera.xyz+drtSunGridFrame.xyz+drtForwardSunGridOffset,0);}else color=mode==1?vec4(getBrightnessFromNormal(normal,1,.34)):vec4(drtForwardPlacedLocal(receiver,normal,voxel,0,0),1);}"));
            try {
                GL.UseProgram(program);
                float[] block=new float[32],calibration=new float[64];for(int i=0;i<32;i++)block[i]=i/31f;
                SurfaceLightBindings.Calibrate(block,calibration); GL.Uniform2(GL.GetUniformLocation(program,"drtPlacedCalibration[0]"),32,calibration);
                GL.Uniform4(GL.GetUniformLocation(program,"drtEntityPlacedSources[0]"),0f,0f,0f,22f);
                GL.Uniform4(GL.GetUniformLocation(program,"drtEntityPlacedColors[0]"),1f,1f,1f,20f);
                GL.Uniform4(GL.GetUniformLocation(program,"drtEntityPlacedFades"),1f,0f,0f,0f);
                GL.Uniform3(GL.GetUniformLocation(program,"normal"),0f,0f,1f);GL.Uniform3(GL.GetUniformLocation(program,"voxel"),.4f,.4f,.4f);
                GL.Uniform1(GL.GetUniformLocation(program,"drtEntityPlacedCount"),1);
                float previousEnergy=float.MaxValue;
                foreach(float distance in new[]{1f,5.5f,10f,15f}) {
                    GL.Uniform3(GL.GetUniformLocation(program,"receiver"),0f,0f,-distance);
                    float direct=calibration[40]*(1-distance/22)*(1/(1+.25f*distance*distance)+.33f);
                    float energy=Draw()[0]; Near(energy,.2f+.5f*direct,"forward placed light matches terrain calibration/falloff");
                    Check(energy<previousEnergy,"forward placed light decreases with distance");previousEnergy=energy;
                }
                GL.Uniform3(GL.GetUniformLocation(program,"receiver"),0f,0f,-23f);
                Near(Draw()[0],.4f,"out-of-range forward material retains native voxel light");
                GL.Uniform1(GL.GetUniformLocation(program,"mode"),1);GL.Uniform3(GL.GetUniformLocation(program,"lightPosition"),0f,0f,1f);
                GL.Uniform1(GL.GetUniformLocation(program,"shadowIntensity"),1f);
                foreach(float scale in new[]{.1f,1f,10f}) { GL.Uniform3(GL.GetUniformLocation(program,"normal"),0f,0f,scale);Near(Draw()[0],1f,"scaled model normals retain full sun-facing brightness"); }
                GL.Uniform1(GL.GetUniformLocation(program,"mode"),2);GL.Uniform3(GL.GetUniformLocation(program,"normal"),0f,1f,0f);
                GL.Uniform3(GL.GetUniformLocation(program,"receiver"),.2f,2.4f,.3f);
                GL.Uniform4(GL.GetUniformLocation(program,"drtAtmosphereCamera"),0f,1.7f,0f,0f);
                GL.Uniform4(GL.GetUniformLocation(program,"drtSunGridFrame"),.1383667f,.69921875f,.2911987f,1f);
                float[] grid=Draw();Near(grid[0],.328125f,"forward sun grid shares deferred world cell centres");Near(grid[2],.578125f,"forward sun grid shares deferred world phase");
            } finally { GL.DeleteProgram(program); }
        }
        finally {
            GL.UseProgram(previous);GL.DeleteFramebuffer(fbo);GL.DeleteVertexArray(vao);
            foreach(int texture in new[]{output,scene,liquid,atlas}) GL.DeleteTexture(texture);
        }
    }
    private static void Near(float actual,float expected,string name)=>Check(float.IsFinite(actual)&&Math.Abs(actual-expected)<2e-5,name+$" ({actual} vs {expected})");
    private static void Check(bool valid,string name){if(!valid)throw new Exception(name);Console.WriteLine("PASS "+name);}
}
