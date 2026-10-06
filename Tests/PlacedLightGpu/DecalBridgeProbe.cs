#nullable enable
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using DRTAgX;
using OpenTK.Graphics.OpenGL4;
using Vintagestory.API.Client;
using Vintagestory.API.MathTools;

// Use real native manager dispatch and Harmony interception with empty pools:
// no game/world is required to verify routing, reload and exact GL restoration.
internal static class DecalBridgeProbe
{
    public sealed class Gate { public bool _boundThisFrame=true; }
    public class ApiProxy : DispatchProxy
    {
        internal Func<MethodInfo,object?[]?,object?>? Handler;
        protected override object? Invoke(MethodInfo? method,object?[]? args)=>Handler!(method!,args);
    }
    private static T Proxy<T>(Func<MethodInfo,object?[]?,object?> handler) where T:class
    {var proxy=DispatchProxy.Create<T,ApiProxy>();((ApiProxy)(object)proxy).Handler=handler;return proxy;}
    private static object? Default(MethodInfo method)=>method.ReturnType==typeof(void)?null:
        method.ReturnType.IsValueType?Activator.CreateInstance(method.ReturnType):null;
    private static FieldInfo Field(string name)=>typeof(DeferredTerrainDecalBridge).GetField(name,BindingFlags.NonPublic|BindingFlags.Instance)!;

    internal static void Run()
    {
        // Standalone routing runs need the same installed-game dependency
        // resolver that the full GPU suite initializes before native API proxies.
        ProbeAssets.Api([]);
        using var saved=new ShadowGlState();
        int program=ProbeShader.Program((ShaderType.VertexShader,"""
            #version 430 core
            uniform vec3 origin;
            void main(){gl_Position=vec4(origin,1);}
            """),(ShaderType.FragmentShader,"""
            #version 430 core
            uniform sampler2D terrainTex,terrainTexLinear;
            uniform int drtForwardDecalPass,haxyFade;
            uniform float alphaTest;
            out vec4 color;
            void main(){color=(texture(terrainTex,vec2(.5))+texture(terrainTexLinear,vec2(.5))*float(haxyFade+1))*alphaTest*float(drtForwardDecalPass+1);}
            """));
        int texture0=GL.GenTexture(),texture2=GL.GenTexture();
        int src=GL.GetInteger(GetPName.BlendSrcRgb),dst=GL.GetInteger(GetPName.BlendDstRgb),srcA=GL.GetInteger(GetPName.BlendSrcAlpha),dstA=GL.GetInteger(GetPName.BlendDstAlpha);
        int eq=GL.GetInteger(GetPName.BlendEquationRgb),eqA=GL.GetInteger(GetPName.BlendEquationAlpha);
        GL.ActiveTexture(TextureUnit.Texture2);int old2=GL.GetInteger(GetPName.TextureBinding2D);
        try
        {
            IShaderProgram shader=Proxy<IShaderProgram>((method,args)=>method.Name switch {
                "get_PassName"=>"chunkopaque","get_ProgramId"=>program,
                "Use"=>Use(),"Stop"=>Stop(),_=>Default(method)});
            object? Use(){GL.UseProgram(program);return null;}
            object? Stop(){GL.UseProgram(0);return null;}
            var render=Proxy<IRenderAPI>((method,args)=>method.Name is "get_CurrentActiveShader" or "GetEngineShader"?shader:Default(method));
            var events=Proxy<IClientEventAPI>((method,args)=>Default(method));
            var api=Proxy<ICoreClientAPI>((method,args)=>method.Name switch {"get_Render"=>render,"get_Event"=>events,_=>Default(method)});
            var gate=new Gate();
            using var bridge=new DeferredTerrainDecalBridge(api,gate);
            bridge.OnRenderFrame(0,EnumRenderStage.Opaque);
            var manager=(MeshDataPoolManager)RuntimeHelpers.GetUninitializedObject(typeof(MeshDataPoolManager));
            typeof(MeshDataPoolManager).GetField("pools",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(manager,new List<MeshDataPool>());
            Field("_decor").SetValue(bridge,new[]{manager});Field("_collecting").SetValue(bridge,true);
            GL.UseProgram(program);GL.Uniform1(GL.GetUniformLocation(program,"terrainTex"),0);GL.Uniform1(GL.GetUniformLocation(program,"terrainTexLinear"),2);
            GL.Uniform1(GL.GetUniformLocation(program,"alphaTest"),.2f);GL.Uniform1(GL.GetUniformLocation(program,"haxyFade"),0);
            GL.ActiveTexture(TextureUnit.Texture0);GL.BindTexture(TextureTarget.Texture2D,texture0);
            GL.ActiveTexture(TextureUnit.Texture2);GL.BindTexture(TextureTarget.Texture2D,texture2);
            GL.Enable(EnableCap.Blend);GL.Enable(EnableCap.DepthTest);GL.Enable(EnableCap.CullFace);GL.DepthMask(true);
            GL.BlendFuncSeparate(BlendingFactorSrc.SrcAlpha,BlendingFactorDest.OneMinusSrcAlpha,BlendingFactorSrc.One,BlendingFactorDest.OneMinusSrcAlpha);
            manager.Render(new Vec3d(1,2,3),"origin",default);
            if((int)Field("_count").GetValue(bridge)! !=1)throw new Exception("Native Decor draw was not deferred exactly once");
            // The state after relighting can differ from the native decal draw.
            // Flush must apply the captured state and restore this incoming state.
            GL.Disable(EnableCap.Blend);GL.Disable(EnableCap.DepthTest);GL.Disable(EnableCap.CullFace);GL.DepthMask(false);
            GL.BlendFuncSeparate(BlendingFactorSrc.One,BlendingFactorDest.Zero,BlendingFactorSrc.Zero,BlendingFactorDest.One);
            GL.BlendEquationSeparate(BlendEquationMode.FuncSubtract,BlendEquationMode.FuncReverseSubtract);
            GL.Uniform1(GL.GetUniformLocation(program,"alphaTest"),.7f);GL.Uniform1(GL.GetUniformLocation(program,"haxyFade"),1);
            GL.Uniform3(GL.GetUniformLocation(program,"origin"),7f,8f,9f);GL.ActiveTexture(TextureUnit.Texture7);
            GL.ActiveTexture(TextureUnit.Texture0);GL.BindTexture(TextureTarget.Texture2D,0);
            GL.ActiveTexture(TextureUnit.Texture2);GL.BindTexture(TextureTarget.Texture2D,0);GL.ActiveTexture(TextureUnit.Texture7);
            bridge.Flush();
            if((int)Field("_count").GetValue(bridge)! !=0||GL.IsEnabled(EnableCap.Blend)||GL.IsEnabled(EnableCap.DepthTest)||GL.IsEnabled(EnableCap.CullFace)||
                GL.GetInteger(GetPName.BlendSrcRgb)!=(int)BlendingFactorSrc.One||GL.GetInteger(GetPName.BlendDstAlpha)!=(int)BlendingFactorDest.One||
                GL.GetInteger(GetPName.BlendEquationRgb)!=(int)BlendEquationMode.FuncSubtract||GL.GetInteger(GetPName.ActiveTexture)!=(int)TextureUnit.Texture7)
                throw new Exception("Decal flush leaked draw state or requeued its replay");
            GL.GetUniform(program,GL.GetUniformLocation(program,"alphaTest"),out float alpha);GL.GetUniform(program,GL.GetUniformLocation(program,"haxyFade"),out int haxy);
            GL.GetUniform(program,GL.GetUniformLocation(program,"drtForwardDecalPass"),out int bypass);
            if(alpha!=.7f||haxy!=1||bypass!=1)throw new Exception("Decal flush lost native uniforms or the late-terrain forward gate");
            GL.ActiveTexture(TextureUnit.Texture0);int restored0=GL.GetInteger(GetPName.TextureBinding2D);
            GL.ActiveTexture(TextureUnit.Texture2);int restored2=GL.GetInteger(GetPName.TextureBinding2D);
            if(restored0!=0||restored2!=0)throw new Exception("Decal flush leaked either terrain texture binding");
            Console.WriteLine("PASS native Decor routing: once-only capture/replay, exact GL and shader uniform restoration");
            typeof(DeferredTerrainDecalBridge).GetMethod("Reload",BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(bridge,null);
            if((bool)Field("_collecting").GetValue(bridge)!)throw new Exception("Reload left the decal collection active");
            gate._boundThisFrame=false;bridge.OnRenderFrame(0,EnumRenderStage.Opaque);
            manager.Render(new Vec3d(),"origin",default);
            if((int)Field("_count").GetValue(bridge)! !=0)throw new Exception("Forward-only manager draw was intercepted");
            // A scene with no Decor must still close deferred output. Exercise
            // consecutive frames and reload with no shader bound to catch stale
            // gates or accidental changes to the current GL program.
            gate._boundThisFrame=true;
            for(int frame=0;frame<2;++frame)
            {
                GL.UseProgram(0);bridge.OnRenderFrame(0,EnumRenderStage.Opaque);
                GL.GetUniform(program,GL.GetUniformLocation(program,"drtForwardDecalPass"),out bypass);
                if(bypass!=0||GL.GetInteger(GetPName.CurrentProgram)!=0)
                    throw new Exception("Next terrain frame did not reopen deferred output without rebinding");
                bridge.Flush();
                GL.GetUniform(program,GL.GetUniformLocation(program,"drtForwardDecalPass"),out bypass);
                if(bypass!=1||GL.GetInteger(GetPName.CurrentProgram)!=0|| (int)Field("_count").GetValue(bridge)! !=0)
                    throw new Exception("Empty Decor queue left AfterOIT water plants in deferred mode");
                manager.Render(new Vec3d(),"origin",default);
                if((int)Field("_count").GetValue(bridge)! !=0)throw new Exception("Late water-plant submission was captured for a finished relight");
                typeof(DeferredTerrainDecalBridge).GetMethod("Reload",BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(bridge,null);
            }
            Console.WriteLine("PASS water-plant routing: empty Decor queues, consecutive frames and reload retain the forward gate without rebinding");
            bridge.Dispose();
            GL.GetUniform(program,GL.GetUniformLocation(program,"drtForwardDecalPass"),out bypass);
            if(bypass!=0)throw new Exception("Removing terrain bridge left its forward gate active");
            Console.WriteLine("PASS water-plant routing: disposal restores the original deferred gate");
            if(GL.GetError()!=ErrorCode.NoError)throw new Exception("Native decal bridge probe GL error");
            Console.WriteLine("PASS native Decor routing: reload clears collection and forward-only drawing stays native");
        }
        finally
        {
            GL.ActiveTexture(TextureUnit.Texture2);GL.BindTexture(TextureTarget.Texture2D,old2);
            GL.BlendFuncSeparate((BlendingFactorSrc)src,(BlendingFactorDest)dst,(BlendingFactorSrc)srcA,(BlendingFactorDest)dstA);
            GL.BlendEquationSeparate((BlendEquationMode)eq,(BlendEquationMode)eqA);
            GL.DeleteTexture(texture0);GL.DeleteTexture(texture2);GL.DeleteProgram(program);
        }
    }
}
