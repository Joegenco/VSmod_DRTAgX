using System;
using System.IO;
using System.Linq;
using DRTAgX;
using OpenTK.Graphics.OpenGL4;

/// <summary>Compare production lunar scattering and sprite main against the same shaders at unit gain.</summary>
internal static class LunarBalanceProbe
{
    internal static void Run(string directory)
    {
        string Expand(string name)=>string.Join('\n',File.ReadAllLines(Path.Combine(directory,name)).Select(line=>
            line.Trim().StartsWith("#include ")?Expand(line.Trim()[9..]):line));
        const string gain="const float DRT_MOON_GAIN = 1.0 / 3.5;";
        string skySource=Expand("drtagx_atmosphere_skyview.csh");
        Check(skySource.Contains(gain),"reference lunar gain substitution");
        int sky=ProbeShader.Program((ShaderType.ComputeShader,skySource)),reference=ProbeShader.Program((ShaderType.ComputeShader,skySource.Replace(gain,"const float DRT_MOON_GAIN = 1.0;")));
        var api=ProbeAssets.AtmosphereApi(directory);
        int trans=AtmosphereCompute.Load(api,"drtagx_atmosphere_transmittance"),multiple=AtmosphereCompute.Load(api,"drtagx_atmosphere_multiscatter");
        int tt=AtmosphereCompute.Texture(256,64),mt=AtmosphereCompute.Texture(32,32),target=AtmosphereCompute.Texture(192,108);
        AtmosphereCompute.Dispatch2D(trans,tt,256,64);GL.ActiveTexture(TextureUnit.Texture0);GL.BindTexture(TextureTarget.Texture2D,tt);
        GL.UseProgram(multiple);GL.Uniform1(GL.GetUniformLocation(multiple,"transmittanceTable"),0);AtmosphereCompute.Dispatch2D(multiple,mt,32,32);
        float Read(int program) {
            GL.ActiveTexture(TextureUnit.Texture0);GL.BindTexture(TextureTarget.Texture2D,tt);
            GL.ActiveTexture(TextureUnit.Texture1);GL.BindTexture(TextureTarget.Texture2D,mt);
            GL.UseProgram(program);GL.Uniform1(GL.GetUniformLocation(program,"transmittanceTable"),0);GL.Uniform1(GL.GetUniformLocation(program,"multiscatterTable"),1);
            GL.Uniform4(GL.GetUniformLocation(program,"sunDirection"),0f,-1f,0f,0f);GL.Uniform4(GL.GetUniformLocation(program,"moonDirection"),0f,1f,0f,.02f);
            GL.Uniform1(GL.GetUniformLocation(program,"cameraAltitude"),1f);AtmosphereCompute.Dispatch2D(program,target,192,108);
            GL.MemoryBarrier(MemoryBarrierFlags.TextureUpdateBarrierBit);GL.BindTexture(TextureTarget.Texture2D,target);
            var pixels=new float[192*108*4];GL.GetTexImage(TextureTarget.Texture2D,0,PixelFormat.Rgba,PixelType.Float,pixels);
            Check(pixels.All(float.IsFinite),"finite lunar halo");float energy=0;
            for(int i=0;i<pixels.Length;i+=4)energy+=pixels[i]*.2126f+pixels[i+1]*.7152f+pixels[i+2]*.0722f;
            return energy;
        }
        float energy=Read(sky),original=Read(reference);
        Check(energy>0 && Math.Abs(original/energy-3.5f)<.005f,"complete lunar LUT radiance, halo and multiple scattering reduced 3.5x");
        Console.WriteLine($"Moon atmosphere reference/current ratio={original/energy:F4}");
        foreach(int program in new[]{sky,reference,trans,multiple})GL.DeleteProgram(program);
        foreach(int texture in new[]{tt,mt,target})GL.DeleteTexture(texture);
        VerifySprite(directory);
        Check(GL.GetError()==ErrorCode.NoError,"lunar GL NoError");
        Console.WriteLine("PASS actual lunar LUT and post-phase sprite reductions at 3.5x, unchanged sun and sprite coverage");
    }

    private static void VerifySprite(string directory)
    {
        string celestial=File.ReadAllText(Path.GetFullPath(Path.Combine(directory,"../../../game/shaders/celestialobject.fsh")));
        string common=File.ReadAllText(Path.Combine(directory,"drtagx_fog_transport.ash"));
        string gain=File.ReadAllText(Path.Combine(directory,"drtagx_celestial_balance.ash"));
        // Keep the actual production main/phase math; neutral environment stubs isolate the sprite's gain.
        string fragment="#version 330 core\n#define SSAOLEVEL 0\n"+common+gain+"""
            uniform sampler2D tex; uniform int weirdMathToMakeMoonLookNicer;
            uniform float moonSunAngle; uniform float dayLight;
            const float alphaTest=.001; const float extraGodray=0;
            const float fogMinIn=0; const float horizonFog=0; const float fogDensityIn=0;
            const float glowLevel=1; const vec3 sunPosition=vec3(1,0,0); const vec3 moonPosition=vec3(0,1,0);
            const float cameraUnderwater=0;
            const vec2 uv=vec2(.5); const vec4 color=vec4(1); const vec3 vertexPosition=vec3(0,1,0);
            layout(location=0) out vec4 outColor; layout(location=1) out vec4 outGlow;
            vec4 applyFog(vec4 c,float f){return c;}
            DrtFogTransport drtSkyMediumTransport(vec3 d){return drtClearTransport();}
            vec3 drtClearSkyRadiance(vec3 d){return vec3(.2);}
            vec3 drtSkyBackground(vec3 d){return vec3(.2);}
            float drtFilteredLiquidDepth(){return 1.0;}
            void getSkyColorAt(vec3 p,vec3 sun,float h,float daylight,float fog,out vec4 c,out vec4 g){c=vec4(.2,.2,.2,1);g=vec4(0);}
            """+ProbeShader.Function(celestial,"void main (");
        const string vertex="#version 330 core\nvoid main(){gl_Position=vec4(gl_VertexID==1?3:-1,gl_VertexID==2?3:-1,0,1);}";
        int shader=ProbeShader.Program((ShaderType.VertexShader,vertex),(ShaderType.FragmentShader,fragment));
        int reference=ProbeShader.Program((ShaderType.VertexShader,vertex),(ShaderType.FragmentShader,fragment.Replace("const float DRT_MOON_GAIN = 1.0 / 3.5;","const float DRT_MOON_GAIN = 1.0;")));
        // Same production main with just the requested daylight gain disabled:
        // isolate the linear two-stop change from phase shading and sky background.
        int daytimeReference=ProbeShader.Program((ShaderType.VertexShader,vertex),(ShaderType.FragmentShader,
            fragment.Replace("return mix(1.0, 0.25, smoothstep(0.0, sin(radians(10.0)), sunElevation));", "return 1.0;")));
        int texture=GL.GenTexture();GL.ActiveTexture(TextureUnit.Texture0);GL.BindTexture(TextureTarget.Texture2D,texture);
        GL.TexImage2D(TextureTarget.Texture2D,0,PixelInternalFormat.Rgba32f,1,1,0,PixelFormat.Rgba,PixelType.Float,new[]{2f,2f,2f,1f});
        GL.TexParameter(TextureTarget.Texture2D,TextureParameterName.TextureMinFilter,(int)TextureMinFilter.Nearest);
        GL.TexParameter(TextureTarget.Texture2D,TextureParameterName.TextureMagFilter,(int)TextureMagFilter.Nearest);
        int target=AtmosphereCompute.Texture(1,1),glowTarget=AtmosphereCompute.Texture(1,1),fbo=GL.GenFramebuffer(),vao=GL.GenVertexArray();
        GL.BindTexture(TextureTarget.Texture2D,texture);GL.BindFramebuffer(FramebufferTarget.Framebuffer,fbo);
        GL.FramebufferTexture2D(FramebufferTarget.Framebuffer,FramebufferAttachment.ColorAttachment0,TextureTarget.Texture2D,target,0);
        GL.FramebufferTexture2D(FramebufferTarget.Framebuffer,FramebufferAttachment.ColorAttachment1,TextureTarget.Texture2D,glowTarget,0);
        GL.DrawBuffers(2,new[]{DrawBuffersEnum.ColorAttachment0,DrawBuffersEnum.ColorAttachment1});GL.ReadBuffer(ReadBufferMode.ColorAttachment0);
        GL.BindVertexArray(vao);GL.Viewport(0,0,1,1);GL.Disable(EnableCap.Blend);GL.Disable(EnableCap.DepthTest);
        int buffer=GL.GenBuffer();GL.BindBuffer(BufferTarget.UniformBuffer,buffer);
        float[] frame=new float[AtmosphereRenderer.FrameFloatCount],glow=new float[4];
        GL.BufferData(BufferTarget.UniformBuffer,frame.Length*4,frame,BufferUsageHint.DynamicDraw);
        GL.BindBufferBase(BufferRangeTarget.UniformBuffer,10,buffer);
        float[] Draw(int program,int moon,float daylight) {
            GL.UseProgram(program);GL.UniformBlockBinding(program,GL.GetUniformBlockIndex(program,"DrtAtmosphere"),10);
            GL.Uniform1(GL.GetUniformLocation(program,"tex"),0);GL.Uniform1(GL.GetUniformLocation(program,"weirdMathToMakeMoonLookNicer"),moon);
            GL.Uniform1(GL.GetUniformLocation(program,"dayLight"),1f);GL.Uniform1(GL.GetUniformLocation(program,"moonSunAngle"),.7f);
            // Set solar elevation independently of the deliberately delayed native
            // daylight clock, so dimming follows the atmosphere's same-frame sun.
            frame[13]=daylight;
            GL.BindBuffer(BufferTarget.UniformBuffer,buffer);GL.BufferSubData(BufferTarget.UniformBuffer,IntPtr.Zero,frame.Length*4,frame);
            GL.DrawArrays(PrimitiveType.Triangles,0,3);var pixel=new float[4];
            GL.ReadBuffer(ReadBufferMode.ColorAttachment0);GL.ReadPixels(0,0,1,1,PixelFormat.Rgba,PixelType.Float,pixel);
            GL.ReadBuffer(ReadBufferMode.ColorAttachment1);GL.ReadPixels(0,0,1,1,PixelFormat.Rgba,PixelType.Float,glow);
            GL.ReadBuffer(ReadBufferMode.ColorAttachment0);return pixel;
        }
        foreach(float daylight in new[]{0f,1f}) {
            var actual=Draw(shader,1,daylight);var original=Draw(reference,1,daylight);
            Check(Math.Abs((original[0]-.2f)/(actual[0]-.2f)-3.5f)<.02f,"moon gain applies after nonlinear phase in day and night");
            Check(Math.Abs(actual[3]-original[3])<.0001f,"moon coverage unchanged");
            var sun=Draw(shader,0,daylight);var referenceSun=Draw(reference,0,daylight);
            Check(Math.Abs(sun[0]-referenceSun[0])<.0001f,"sun sprite radiance unchanged");
        }
        float midpoint=.5f*MathF.Sin(10*MathF.PI/180);
        foreach(var (solarHeight,expectedGain) in new[]{(-1f,1f),(0f,1f),(midpoint,.625f),(1f,.25f)}) {
            var actual=Draw(shader,1,solarHeight);float actualGlow=glow[0];
            var original=Draw(daytimeReference,1,solarHeight);float originalGlow=glow[0];
            Check(Math.Abs((actual[0]-.2f)/(original[0]-.2f)-expectedGain)<.002f,"linear daytime lunar sprite gain");
            Check(Math.Abs(actualGlow/originalGlow-expectedGain)<.002f,"matching lunar bloom gain");
            Check(actual[3]==original[3],"daytime lunar alpha/background coverage unchanged");
            var sun=Draw(shader,0,solarHeight);var referenceSun=Draw(daytimeReference,0,solarHeight);
            Check(sun[0]==referenceSun[0],"daytime lunar gain leaves sun unchanged");
        }
        Console.WriteLine("PASS daytime moon sprite/bloom: two stops, smooth midpoint, unchanged night/sun/coverage");
        GL.BindFramebuffer(FramebufferTarget.Framebuffer,0);GL.BindVertexArray(0);GL.UseProgram(0);GL.BindBufferBase(BufferRangeTarget.UniformBuffer,10,0);
        GL.DeleteProgram(shader);GL.DeleteProgram(reference);GL.DeleteProgram(daytimeReference);GL.DeleteBuffer(buffer);GL.DeleteFramebuffer(fbo);GL.DeleteVertexArray(vao);GL.DeleteTexture(texture);GL.DeleteTexture(target);GL.DeleteTexture(glowTarget);
    }
    private static void Check(bool ok,string name){if(!ok)throw new Exception(name);}
}
