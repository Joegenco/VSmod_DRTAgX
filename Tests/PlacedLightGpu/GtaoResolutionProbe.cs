using System;
using System.Collections.Generic;
using System.IO;
using DRTAgX;
using OpenTK.Graphics.OpenGL4;
using Vintagestory.API.Client;
using Vintagestory.API.Common;

internal static class GtaoResolutionProbe
{
    internal static void Run(string directory)
    {
        ProbeAssets.Api([]);
        using var state=new HdrPassState();
        using var shaders=new HdrShaderFixture(directory);
        var buffers=new List<FrameBufferRef>(); for(int i=0;i<16;++i) buffers.Add(null);
        var textures=new List<int>(); var fbos=new List<int>();
        int Texture(int w,int h,PixelInternalFormat format,float[] values=null)
        {
            int texture=GL.GenTexture(); textures.Add(texture); GL.ActiveTexture(TextureUnit.Texture0); GL.BindTexture(TextureTarget.Texture2D,texture);
            GL.TexImage2D(TextureTarget.Texture2D,0,format,w,h,0,PixelFormat.Rgba,PixelType.Float,values);
            GL.TexParameter(TextureTarget.Texture2D,TextureParameterName.TextureMinFilter,(int)TextureMinFilter.Nearest);
            GL.TexParameter(TextureTarget.Texture2D,TextureParameterName.TextureMagFilter,(int)TextureMagFilter.Nearest);
            GL.TexParameter(TextureTarget.Texture2D,TextureParameterName.TextureWrapS,(int)TextureWrapMode.ClampToEdge);
            GL.TexParameter(TextureTarget.Texture2D,TextureParameterName.TextureWrapT,(int)TextureWrapMode.ClampToEdge);
            return texture;
        }
        FrameBufferRef Target(int w,int h,int texture) {
            int fbo=GL.GenFramebuffer(); fbos.Add(fbo); GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer,fbo);
            GL.FramebufferTexture2D(FramebufferTarget.DrawFramebuffer,FramebufferAttachment.ColorAttachment0,TextureTarget.Texture2D,texture,0);
            GL.DrawBuffer(DrawBufferMode.ColorAttachment0);
            return new FrameBufferRef{Width=w,Height=h,FboId=fbo,ColorTextureIds=new[]{texture}};
        }
        void Check(bool condition,string label) { if(!condition) throw new Exception(label); }
        float Read(int texture) {
            GL.ActiveTexture(TextureUnit.Texture0); GL.BindTexture(TextureTarget.Texture2D,texture);
            float[] values=new float[32*16]; GL.GetTexImage(TextureTarget.Texture2D,0,PixelFormat.Red,PixelType.Float,values); return values[0];
        }
        try {
            var positions=new float[64*32*4]; for(int i=0;i<positions.Length;i+=4) {positions[i]=1;positions[i+2]=-2;}
            int position=Texture(64,32,PixelInternalFormat.Rgba32f,positions);
            buffers[0]=Target(64,32,Texture(64,32,PixelInternalFormat.Rgba16f));
            buffers[0].ColorTextureIds=new[]{buffers[0].ColorTextureIds[0],0,0,position};
            for(int i=0;i<3;++i) buffers[13+i]=Target(32,16,Texture(32,16,PixelInternalFormat.R8));
            int noise=Texture(16,16,PixelInternalFormat.Rgba32f); buffers[13].ColorTextureIds=new[]{buffers[13].ColorTextureIds[0],noise};
            var render=SurfaceApiProxy.Make<IRenderAPI>((method,args)=>method.Name switch {
                "get_FrameBuffers"=>buffers,"get_CurrentActiveShader"=>shaders.Active,_=>throw new NotSupportedException(method.Name)
            });
            var events=SurfaceApiProxy.Make<IClientEventAPI>((_,_)=>null);
            var logger=SurfaceApiProxy.Make<ILogger>((_,_)=>null);
            var api=SurfaceApiProxy.Make<ICoreClientAPI>((method,args)=>method.Name switch {
                "get_Render"=>render,"get_Event"=>events,"get_Logger"=>logger,"get_Shader"=>shaders.Api,_=>throw new NotSupportedException(method.Name)
            });
            var renderer=new SheyderMod.Features.GTAO.GtaoRenderer(api);
            using var owner=new GtaoResolutionAdapter(api);
            var originals=new[]{buffers[13],buffers[14],buffers[15]};
            var config=new AgxConfig(); FrameQuality.Publish(config);
            Check(renderer.Invoke() && renderer.RawWidth==32,"Normal retains original GTAO target width");
            float reference=Read(buffers[14].ColorTextureIds[0]);
            config.PerformanceMode=true; FrameQuality.Publish(config);
            Check(renderer.Invoke() && renderer.RawWidth==16 && renderer.Noise==noise,"quarter GTAO lends its target and retains raw noise alias");
            Check(Math.Abs(Read(buffers[14].ColorTextureIds[0])-reference)<1f/255f,"quarter constant visibility reconstructs to native output");
            for(int i=0;i<3;++i) Check(ReferenceEquals(originals[i],buffers[13+i]),"native AO references restored after pass");
            renderer.Split=true;
            for(int y=0;y<32;++y)for(int x=0;x<64;++x)positions[(y*64+x)*4+2]=x<32?-2:-20;
            GL.ActiveTexture(TextureUnit.Texture0);GL.BindTexture(TextureTarget.Texture2D,position);
            GL.TexSubImage2D(TextureTarget.Texture2D,0,0,0,64,32,PixelFormat.Rgba,PixelType.Float,positions);
            renderer.Invoke();GL.BindTexture(TextureTarget.Texture2D,buffers[14].ColorTextureIds[0]);
            var visibility=new float[32*16];GL.GetTexImage(TextureTarget.Texture2D,0,PixelFormat.Red,PixelType.Float,visibility);
            for(int y=0;y<16;++y)for(int x=0;x<32;++x)Check(Math.Abs(visibility[y*32+x]-(x<16?.1f:.9f))<=1f/255f,"quarter reconstruction rejects silhouette cross-depth mixing");
            renderer.Split=false;
            Array.Clear(positions); GL.BindTexture(TextureTarget.Texture2D,position);
            GL.TexSubImage2D(TextureTarget.Texture2D,0,0,0,64,32,PixelFormat.Rgba,PixelType.Float,positions);
            renderer.Invoke(); Check(Read(buffers[14].ColorTextureIds[0])==1,"sky zero sentinels retain unit AO");
            renderer.Throw=true;
            try { renderer.Invoke(); throw new Exception("Expected provider failure"); } catch(InvalidOperationException) { }
            renderer.Throw=false;
            for(int i=0;i<3;++i) Check(ReferenceEquals(originals[i],buffers[13+i]),"native AO references restored after provider exception");
            shaders.Failure="ao_reconstruct:Use"; int before=renderer.Calls;
            Check(renderer.Invoke() && renderer.Calls==before+2 && renderer.RawWidth==32,"owned reconstruction failure reruns original GTAO");
            shaders.Failure=null;
            Check(renderer.Invoke() && renderer.RawWidth==32,"failed generation remains on original GTAO without repeated failures");
            typeof(GtaoResolutionAdapter).GetMethod("Reload",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic).Invoke(owner,null);
            Check(renderer.Invoke() && renderer.RawWidth==16,"shader reload recovers quarter GTAO");
            for(int i=0;i<10;++i) {
                config.PerformanceMode=false;FrameQuality.Publish(config);Check(renderer.Invoke() && renderer.RawWidth==32,"repeated toggle restores native AO");
                config.PerformanceMode=true;FrameQuality.Publish(config);Check(renderer.Invoke() && renderer.RawWidth==16,"repeated toggle retains quarter AO");
                for(int j=0;j<3;++j)Check(ReferenceEquals(originals[j],buffers[13+j]),"repeated toggle restores exact native wrappers");
            }
            buffers[0].Width=96;buffers[0].Height=48;
            Check(renderer.Invoke() && renderer.RawWidth==24,"primary resize recreates quarter targets");
            for(int i=0;i<3;++i) {
                var old=buffers[13+i];buffers[13+i]=new FrameBufferRef{FboId=old.FboId,ColorTextureIds=old.ColorTextureIds,Width=old.Width,Height=old.Height};
            }
            Check(renderer.Invoke() && renderer.RawWidth==24,"same-size native wrapper recreation remains supported");
            config.PerformanceMode=false; FrameQuality.Publish(config);
            Check(GL.GetError()==ErrorCode.NoError,"GTAO lifecycle fixture GL state valid");
            Console.WriteLine("PASS quarter GTAO: real owner/Harmony loan, noise alias, constant/silhouette/sky reconstruction, exact references, provider/owned failure, reload, ten toggles, resize and same-size recreation");
        }
        finally { foreach(int texture in textures) GL.DeleteTexture(texture); foreach(int fbo in fbos) GL.DeleteFramebuffer(fbo); }
    }
}

namespace SheyderMod.Features.GTAO
{
    // Minimal captured provider ABI: real shaders/resources and the production adapter execute above.
    internal sealed class GtaoRenderer(ICoreClientAPI api)
    {
        internal int Calls,RawWidth,Noise;
        internal bool Throw,Split;
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private bool RenderGtaoPass()
        {
            ++Calls; var buffers=api.Render.FrameBuffers; RawWidth=buffers[13].Width;
            Noise=buffers[13].ColorTextureIds.Length>1?buffers[13].ColorTextureIds[1]:0;
            if(Throw) throw new InvalidOperationException("provider fixture failure");
            var target=buffers[14]; GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer,target.FboId);
            GL.ClearBuffer(ClearBuffer.Color,0,new float[]{.65f,.65f,.65f,1});
            if(Split) {
                GL.Enable(EnableCap.ScissorTest);GL.Scissor(0,0,target.Width/2,target.Height);GL.ClearBuffer(ClearBuffer.Color,0,new float[]{.1f,.1f,.1f,1});
                GL.Scissor(target.Width/2,0,target.Width-target.Width/2,target.Height);GL.ClearBuffer(ClearBuffer.Color,0,new float[]{.9f,.9f,.9f,1});GL.Disable(EnableCap.ScissorTest);
            }
            return true;
        }
        internal bool Invoke()=>RenderGtaoPass();
    }
}
