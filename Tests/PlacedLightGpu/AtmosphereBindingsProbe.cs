using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using DRTAgX;
using OpenTK.Graphics.OpenGL4;
using Vintagestory.API.Client;

/// <summary>Exercise the real four-sampler binder with native-like stale liquid state.</summary>
internal static class AtmosphereBindingsProbe
{
    internal static void Run(string directory, int buffer)
    {
        ProbeAssets.Api([]); // Resolve installed optional API signature dependencies for DispatchProxy.
        string common = File.ReadAllText(Path.Combine(directory, "drtagx_fog_transport.ash"));
        int program = ProbeShader.Program((ShaderType.VertexShader, """
            #version 430 core
            void main(){gl_Position=vec4(gl_VertexID==1?3:-1,gl_VertexID==2?3:-1,0,1);}
            """), (ShaderType.FragmentShader, "#version 430 core\n" + common + """
            uniform sampler2D liquidDepth, drtSkyViewPrevious, drtSkyViewCurrent;
            uniform sampler3D drtFogVolume;
            uniform sampler2D modTextures[5];
            uniform float modWeight;
            out vec4 color;
            void main(){ color=vec4(texture(liquidDepth,vec2(.5)).r+
                texture(drtSkyViewPrevious,vec2(.5)).r+texture(drtSkyViewCurrent,vec2(.5)).r+
                texture(drtFogVolume,vec3(.5)).r+drtFogColor.w*.001+
                modWeight*(texture(modTextures[0],vec2(.5)).r+texture(modTextures[1],vec2(.5)).r+
                    texture(modTextures[2],vec2(.5)).r+texture(modTextures[3],vec2(.5)).r+texture(modTextures[4],vec2(.5)).r)); }
            """));
        int Scalar(float value)
        {
            int texture=GL.GenTexture(); GL.BindTexture(TextureTarget.Texture2D,texture);
            GL.TexImage2D(TextureTarget.Texture2D,0,PixelInternalFormat.R32f,1,1,0,PixelFormat.Red,PixelType.Float,new[]{value});
            GL.TexParameter(TextureTarget.Texture2D,TextureParameterName.TextureMinFilter,(int)TextureMinFilter.Nearest);
            GL.TexParameter(TextureTarget.Texture2D,TextureParameterName.TextureMagFilter,(int)TextureMagFilter.Nearest);
            return texture;
        }
        int previous=Scalar(.1f), current=Scalar(.2f), liquid=Scalar(.75f), resizedLiquid=Scalar(.625f), wrong=Scalar(.02f);
        int volume=AtmosphereCompute.Texture(1,1,1);
        GL.TexSubImage3D(TextureTarget.Texture3D,0,0,0,0,1,1,1,PixelFormat.Red,PixelType.Float,new[]{.4f});
        var buffers=new List<FrameBufferRef>();
        for(int i=0;i<13;i++) buffers.Add(null);
        buffers[5]=new FrameBufferRef { DepthTextureId=liquid, Width=1, Height=1 };
        var render=SurfaceApiProxy.Make<IRenderAPI>((method,_)=>method.Name=="get_FrameBuffers"?buffers:throw new NotSupportedException(method.Name));
        var api=SurfaceApiProxy.Make<ICoreClientAPI>((method,_)=>method.Name=="get_Render"?render:throw new NotSupportedException(method.Name));
        var sky=new AtmosphereSkyResources(api);
        typeof(AtmosphereSkyResources).GetProperty("Previous",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(sky,previous);
        typeof(AtmosphereSkyResources).GetProperty("Current",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(sky,current);
        bool oldDither=GL.IsEnabled(EnableCap.Dither);
        GL.Enable(EnableCap.Dither);
        var bindings=new AtmosphereProgramBindings(api,sky,buffer) { Volume=volume };
        if(GL.IsEnabled(EnableCap.Dither)) throw new Exception("Dithering still enabled after owner creation");
        var bind=typeof(AtmosphereProgramBindings).GetMethod("Bind",BindingFlags.Instance|BindingFlags.NonPublic);
        int sentinel=GL.GenSampler();
        for(int unit=10;unit<=20;unit++) {
            GL.ActiveTexture(TextureUnit.Texture0+unit);
            GL.BindTexture(TextureTarget.Texture2D,wrong); GL.BindTexture(TextureTarget.Texture3D,volume);
            GL.BindSampler(unit,sentinel);
        }
        int output=AtmosphereCompute.Texture(1,1), fbo=GL.GenFramebuffer(), vao=GL.GenVertexArray();
        GL.BindFramebuffer(FramebufferTarget.Framebuffer,fbo);
        GL.FramebufferTexture2D(FramebufferTarget.Framebuffer,FramebufferAttachment.ColorAttachment0,TextureTarget.Texture2D,output,0);
        if(GL.CheckFramebufferStatus(FramebufferTarget.Framebuffer)!=FramebufferErrorCode.FramebufferComplete) throw new Exception("Binding probe FBO");
        // Texture allocation above changed the active high unit: reassert all
        // sentinels before testing restoration, independent of GL defaults.
        for(int unit=10;unit<=20;unit++) { GL.ActiveTexture(TextureUnit.Texture0+unit); GL.BindTexture(TextureTarget.Texture2D,wrong); }
        GL.BindVertexArray(vao); GL.Viewport(0,0,1,1);
        using var borrowed = new BorrowedBufferRanges(BufferRangeTarget.UniformBuffer, AtmosphereProgramBindings.BufferBinding);
        foreach(bool congested in new[]{false,true}) foreach(bool resized in new[]{false,true}) {
            buffers[5].DepthTextureId=resized?resizedLiquid:liquid;
            bindings.Reload();
            GL.UseProgram(program); GL.Uniform1(GL.GetUniformLocation(program,"liquidDepth"),4);
            // Reproduce ChunkLOD exhausting 10..13, plus a nonconsecutive
            // sampler-array element on 16 that must also remain untouched.
            for(int i=0;i<5;i++) GL.Uniform1(GL.GetUniformLocation(program,$"modTextures[{i}]"),congested?(i<4?10+i:16):0);
            GL.Uniform1(GL.GetUniformLocation(program,"modWeight"),congested?.001f:0f);
            GL.ActiveTexture(TextureUnit.Texture4); GL.BindTexture(TextureTarget.Texture2D,wrong);
            // A foreign pass may reenable quantization; native Use must reassert
            // the lifetime policy independently of cached sampler assignments.
            GL.Enable(EnableCap.Dither);
            bind.Invoke(bindings,new object[]{program});
            if(GL.IsEnabled(EnableCap.Dither)) throw new Exception("Native Use left dithering enabled");
            GL.GetUniform(program,GL.GetUniformLocation(program,"liquidDepth"),out int assigned);
            if(assigned!=(congested?20:10)) throw new Exception("Liquid sampler collides with native/mod/atmosphere units");
            GL.DrawArrays(PrimitiveType.Triangles,0,3);
            float[] pixel=new float[4]; GL.ReadPixels(0,0,1,1,PixelFormat.Rgba,PixelType.Float,pixel);
            if(Math.Abs(pixel[0]-((resized?1.326f:1.451f)+(congested?.0001f:0f)))>.003f) throw new Exception("Native liquid depth or LOD textures were not sampled");
            bindings.Restore();
            borrowed.AssertRestored("Atmosphere Use/Stop");
            if(GL.IsEnabled(EnableCap.Dither)) throw new Exception("Native Stop restored dithering during active lifetime");
            for(int unit=10;unit<=20;unit++) {
                GL.ActiveTexture(TextureUnit.Texture0+unit);
                if(GL.GetInteger(GetPName.TextureBinding2D)!=wrong || GL.GetInteger(GetPName.TextureBinding3D)!=volume || GL.GetInteger(GetPName.SamplerBinding)!=sentinel)
                    throw new Exception("Atmosphere binder leaked texture/sampler state");
            }
        }
        // Programs without DrtAtmosphere still receive the no-dither policy.
        int plain=ProbeShader.Program((ShaderType.VertexShader,"#version 330 core\nvoid main(){gl_Position=vec4(0);}"),
            (ShaderType.FragmentShader,"#version 330 core\nout vec4 color;void main(){color=vec4(1);}"));
        GL.UseProgram(plain); GL.Enable(EnableCap.Dither); bind.Invoke(bindings,new object[]{plain});
        if(GL.IsEnabled(EnableCap.Dither)) throw new Exception("Non-atmosphere shader bypassed dithering policy");
        bindings.Dispose();
        if(!GL.IsEnabled(EnableCap.Dither)) throw new Exception("Disposal failed to restore incoming dithering state");
        GL.Disable(EnableCap.Dither);
        using (var disabledOwner=new AtmosphereProgramBindings(api,sky,buffer)) {
            GL.Enable(EnableCap.Dither); bind.Invoke(disabledOwner,new object[]{plain});
            if(GL.IsEnabled(EnableCap.Dither)) throw new Exception("Disabled incoming state was not enforced");
        }
        if(GL.IsEnabled(EnableCap.Dither)) throw new Exception("Disposal enabled previously disabled dithering");
        GL.DeleteProgram(plain);
        for(int unit=10;unit<=20;unit++) GL.BindSampler(unit,0);
        GL.BindFramebuffer(FramebufferTarget.Framebuffer,0); GL.BindVertexArray(0);
        GL.DeleteFramebuffer(fbo); GL.DeleteVertexArray(vao); GL.DeleteSampler(sentinel); GL.DeleteProgram(program);
        sky.Dispose();
        foreach(int texture in new[]{liquid,resizedLiquid,wrong,volume,output}) GL.DeleteTexture(texture);
        if(GL.GetError()!=ErrorCode.NoError) throw new Exception("Atmosphere sampler allocation/restoration GL error");
        Console.WriteLine("PASS production sampler binding: exhausted 10..13 pool, nonconsecutive mod array, units above 15, reload/new liquid resource, texture/sampler restoration");
        Console.WriteLine("PASS production dithering policy: creation, cached Use, Stop, shader without atmosphere, disposal");
        if(oldDither) GL.Enable(EnableCap.Dither); else GL.Disable(EnableCap.Dither);
    }
}
