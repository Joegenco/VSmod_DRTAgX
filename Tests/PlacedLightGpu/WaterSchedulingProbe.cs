using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using DRTAgX;
using HarmonyLib;
using OpenTK.Graphics.OpenGL4;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;

/// <summary>Execute the checked face-loop adapter against the captured provider shape.</summary>
internal static class WaterSchedulingProbe
{
    internal static void Run()
    {
        var events=SurfaceApiProxy.Make<IClientEventAPI>((_,_)=>null);
        var logger=SurfaceApiProxy.Make<ILogger>((_,_)=>null);
        using var saved=new HdrPassState();
        RunQualityCaps(logger);
        var entity=(EntityPlayer)RuntimeHelpers.GetUninitializedObject(typeof(EntityPlayer));
        entity.CameraPos=new Vec3d(522417,126,522611);
        var player=HdrFixturePlayer.Create(entity);
        IClientWorldAccessor World()=>SurfaceApiProxy.Make<IClientWorldAccessor>((method,_)=>method.Name=="get_Player"?player:throw new NotSupportedException(method.Name));
        var world=World();
        var buffers=new List<FrameBufferRef>{new(){Width=64,Height=32,ColorTextureIds=new[]{3},DepthTextureId=4}};
        float[] view={1,0,0,0,0,1,0,0,0,0,1,0,0,0,0,1};
        var render=SurfaceApiProxy.Make<IRenderAPI>((method,_)=>method.Name switch {
            "get_FrameBuffers"=>buffers,"get_CameraMatrixOriginf"=>view,_=>throw new NotSupportedException(method.Name)
        });
        var api=SurfaceApiProxy.Make<ICoreClientAPI>((method,_)=>method.Name switch {
            "get_Event"=>events,"get_Logger"=>logger,"get_Render"=>render,"get_World"=>world,_=>throw new NotSupportedException(method.Name)
        });
        void Check(bool ok,string label) { if(!ok) throw new Exception(label); }
        using(var owner=new WaterQualityScheduling(api)) {
            var cube=new SheyderMod.Features.WaterShader.EnvCubemap.WaterEnvCubemap();
            var config=new AgxConfig();FrameQuality.Publish(config);
            cube.Capture();Check(cube.Mask==63 && cube.Count==6,"Normal captures six faces");
            config.PerformanceMode=true;FrameQuality.Publish(config);
            for(int i=0;i<18;++i) {cube.Capture();Check(cube.Count==1 && cube.Mask==(1<<(i%6)),"Performance cycles one face per frame");}
            cube.Capture();cube.ClearFaces();cube.Capture();Check(cube.Mask==1,"confidence clear resets face cycle");
            cube.Capture();cube.Release();cube.Capture();Check(cube.Mask==1,"resource release resets face cycle");
            config.PerformanceMode=false;FrameQuality.Publish(config);cube.Capture();Check(cube.Mask==63,"Normal restores all six faces");
            config.PerformanceMode=true;FrameQuality.Publish(config);cube.Capture();Check(cube.Mask==1,"quality cycle restarts after Normal");
            var historyCube=new SheyderMod.Features.WaterShader.EnvCubemap.WaterEnvCubemap();
            var water=new SheyderMod.Features.WaterShader.WaterSsrRenderer(historyCube);
            // Reset only confidence/cycle; current HDR color/depth ownership stays native.
            void Invalidate(string label) {
                int draw=GL.GetInteger(GetPName.DrawFramebufferBinding),read=GL.GetInteger(GetPName.ReadFramebufferBinding);
                water._historyValid=true;water.OnRenderFrame(EnumRenderStage.Opaque);Check(!water._historyValid,label);
                Check(GL.GetInteger(GetPName.DrawFramebufferBinding)==draw && GL.GetInteger(GetPName.ReadFramebufferBinding)==read,"confidence clear restores native framebuffer bindings");
            }
            Invalidate("first frame invalidates unknown history");
            int oldCube=GL.GetInteger(GetPName.TextureBindingCubeMap);GL.BindTexture(TextureTarget.TextureCubeMap,historyCube._texId);
            float[] confidence=new float[4];
            for(int i=0;i<6;++i){GL.GetTexImage(TextureTarget.TextureCubeMapPositiveX+i,0,PixelFormat.Rgba,PixelType.Float,confidence);Check(confidence[3]==0,"all six confidence faces cleared");}
            GL.BindTexture(TextureTarget.TextureCubeMap,oldCube);
            water._historyValid=true;water.OnRenderFrame(EnumRenderStage.Opaque);Check(water._historyValid,"stable camera/resources keep history");
            entity.CameraPos.X+=65;Invalidate("teleport resets SSR confidence");
            view[10]=-1;Invalidate("camera cut resets SSR confidence");
            buffers[0].Width=32;Invalidate("resize resets SSR confidence");
            buffers[0].ColorTextureIds[0]=7;Invalidate("same-size GL identity change resets SSR confidence");
            var previous=buffers[0];buffers[0]=new(){Width=previous.Width,Height=previous.Height,ColorTextureIds=previous.ColorTextureIds,DepthTextureId=previous.DepthTextureId};
            Invalidate("same-size wrapper recreation resets SSR confidence");
            typeof(WaterQualityScheduling).GetMethod("Reload",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(owner,null);Invalidate("relink/reload resets SSR confidence");
            world=World();Invalidate("world identity change resets SSR confidence");
            config.PerformanceMode=false;FrameQuality.Publish(config);Invalidate("quality change resets SSR confidence");
            // A new provider/changed binary with an ambiguous loop must retain every instruction.
            var unsupported=new List<CodeInstruction>{new(OpCodes.Ldc_I4_6),new(OpCodes.Ret)};
            var result=(IEnumerable<CodeInstruction>)typeof(WaterQualityScheduling).GetMethod("FaceLoop",BindingFlags.Static|BindingFlags.NonPublic).Invoke(null,new object[]{unsupported});
            foreach(var code in result) Check(code.opcode!=OpCodes.Call,"unsupported face loop retains native instructions");
            config.PerformanceMode=false;FrameQuality.Publish(config);
            historyCube.Release();cube.Release();
        }
        var unpatched=new SheyderMod.Features.WaterShader.EnvCubemap.WaterEnvCubemap();unpatched.Capture();Check(unpatched.Count==6,"disposal restores native loop");unpatched.Release();
        var cycle=new WaterQualityScheduling.FaceCycle();for(int i=0;i<10000;++i)cycle.Select(true);
        long before=GC.GetAllocatedBytesForCurrentThread();for(int i=0;i<10000;++i)cycle.Select(true);
        long allocated=GC.GetAllocatedBytesForCurrentThread()-before;
        if(allocated!=0) throw new Exception("Face-cycle allocated bytes: "+allocated);
        Check(GL.GetError()==ErrorCode.NoError,"water lifecycle fixture GL error");
        Console.WriteLine("PASS water scheduling/history: real Harmony loop, cyclic faces, clear/release/mode resets, stable history; teleport/rotation/resize/recreation/reload/world invalidation; unsupported fallback, disposal, zero-allocation cycle");
    }

    private sealed class SystemFixture : ModSystem
    {
        private readonly object[] _renderers;
        private readonly object _waterSsr;
        internal SystemFixture(object water, bool duplicate) { _waterSsr=water; _renderers=duplicate?new[]{water}:Array.Empty<object>(); }
    }

    private static void RunQualityCaps(ILogger logger)
    {
        void Check(bool ok,string label) { if(!ok) throw new Exception(label); }
        foreach(bool duplicate in new[]{false,true})
        {
            // Match the captured provider registration: water is normally outside _renderers.
            // Also guard against wrapping twice if a later provider puts it in both locations.
            var source=new SheyderMod.Features.WaterShader.ConfigFixture();
            var cube=new SheyderMod.Features.WaterShader.EnvCubemap.WaterEnvCubemap();
            var water=new SheyderMod.Features.WaterShader.WaterSsrRenderer(cube,()=>source);
            var system=new SystemFixture(water,duplicate);
            var loader=SurfaceApiProxy.Make<IModLoader>((method,_)=>method.Name=="GetModSystem"?system:throw new NotSupportedException(method.Name));
            var api=SurfaceApiProxy.Make<ICoreClientAPI>((method,_)=>method.Name switch {
                "get_ModLoader"=>loader,"get_Logger"=>logger,_=>throw new NotSupportedException(method.Name)
            });
            var config=new AgxConfig();FrameQuality.Publish(config);
            using(var adapter=new SheyderQualityAdapter(api))
            {
                Check(ReferenceEquals(source,water.Config),"Normal returns original water preferences");
                config.PerformanceMode=true;FrameQuality.Publish(config);
                var capped=water.Config;
                Check(capped.Water.Steps==4 && capped.Water.Downsample==6,"separately registered water receives Performance caps");
                Check(source.Water.Steps==6 && source.Water.Downsample==5,"Performance leaves saved water preferences untouched");
                Check(!capped.Water.Enabled && capped.DeferredEnabled,"caps retain disabled effects and deferred preference");
                source.Water.Steps=2;source.Water.Downsample=8;source.Water.Enabled=true;
                Check(water.Config.Water.Steps==2 && water.Config.Water.Downsample==8 && water.Config.Water.Enabled,"live edits retain lower samples/higher downsample and enabled preference");
                source.Water=new(){Steps=12,Downsample=2,Enabled=false};
                Check(water.Config.Water.Steps==4 && water.Config.Water.Downsample==6 && !water.Config.Water.Enabled,"effect replacement rebuilds capped view");
                source=new();
                Check(water.Config.Water.Steps==4 && water.Config.Water.Downsample==6,"root config replacement rebuilds capped view");
                for(int i=0;i<10;++i) {
                    config.PerformanceMode=false;FrameQuality.Publish(config);
                    Check(ReferenceEquals(source,water.Config),"toggle restores original water config identity");
                    config.PerformanceMode=true;FrameQuality.Publish(config);
                    Check(water.Config.Water.Steps==4,"toggle reapplies water caps");
                }
                for(int i=0;i<10000;++i)_=water.Config;
                long before=GC.GetAllocatedBytesForCurrentThread();
                for(int i=0;i<10000;++i)_=water.Config;
                Check(GC.GetAllocatedBytesForCurrentThread()==before,"water config caps allocate no steady-state managed bytes");
            }
            Check(ReferenceEquals(source,water.Config),"disposal restores original water getter");
            config.PerformanceMode=false;FrameQuality.Publish(config);cube.Release();
        }
        Console.WriteLine("PASS water quality caps: separate registration, duplicate guard, preferences/disabled effects, config replacements, repeated modes, original getter restoration and zero warm allocations");
    }
}

namespace SheyderMod.Features.WaterShader
{
    public sealed class WaterFixture
    {
        public int Steps {get;set;}=6;
        public int Downsample {get;set;}=5;
        public bool Enabled {get;set;}
    }
    public sealed class ConfigFixture
    {
        public WaterFixture Water {get;set;}=new();
        public bool DeferredEnabled {get;set;}=true;
    }
    internal sealed class WaterSsrRenderer
    {
        internal bool _historyValid;
        private readonly EnvCubemap.WaterEnvCubemap _env;
        private readonly Func<ConfigFixture> _getConfig;
        internal ConfigFixture Config=>_getConfig();
        internal WaterSsrRenderer(EnvCubemap.WaterEnvCubemap environment,Func<ConfigFixture> getConfig=null)
        {
            _env=environment;
            var defaults=new ConfigFixture();
            _getConfig=getConfig??(()=>defaults);
        }
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal void OnRenderFrame(EnumRenderStage stage) { }
    }
}

namespace SheyderMod.Features.WaterShader.EnvCubemap
{
    internal sealed class WaterEnvCubemap
    {
        internal int Mask,Count;
        internal int _texId;
        private int _fbo;
        internal WaterEnvCubemap() {
            int old=GL.GetInteger(GetPName.TextureBindingCubeMap);
            _texId=GL.GenTexture();GL.BindTexture(TextureTarget.TextureCubeMap,_texId);
            for(int i=0;i<6;++i) GL.TexImage2D(TextureTarget.TextureCubeMapPositiveX+i,0,PixelInternalFormat.Rgba16f,1,1,0,PixelFormat.Rgba,PixelType.Float,new float[]{4,2,1,1});
            GL.BindTexture(TextureTarget.TextureCubeMap,old);_fbo=GL.GenFramebuffer();
        }
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        internal void Capture() { Mask=Count=0;for(int i=0;i<6;++i){Mask|=1<<i;++Count;} }
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        internal void ClearFaces() {
            Mask=Count=0;if(_texId==0)return;
            GL.BindFramebuffer(FramebufferTarget.Framebuffer,_fbo);GL.DrawBuffer(DrawBufferMode.ColorAttachment0);
            for(int i=0;i<6;++i) {
                GL.FramebufferTexture2D(FramebufferTarget.Framebuffer,FramebufferAttachment.ColorAttachment0,TextureTarget.TextureCubeMapPositiveX+i,_texId,0);
                GL.ClearBuffer(ClearBuffer.Color,0,new float[]{0,0,0,0});
            }
        }
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        internal void Release() { Mask=Count=0;if(_texId!=0)GL.DeleteTexture(_texId);if(_fbo!=0)GL.DeleteFramebuffer(_fbo);_texId=_fbo=0; }
    }
}
