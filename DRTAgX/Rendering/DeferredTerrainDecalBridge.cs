using System;
using System.Linq.Expressions;
using HarmonyLib;
using OpenTK.Graphics.OpenGL4;
using Vintagestory.API.Client;
using Vintagestory.API.MathTools;
using Vintagestory.Client.NoObf;

namespace DRTAgX;

/// <summary>Replays native Decor batches onto lit terrain, preserving fractional
/// alpha without blending categorical G-buffer metadata. Late water-plant draws
/// stay forward after the G-buffer closes. No extra terrain draw.</summary>
internal sealed class DeferredTerrainDecalBridge : IRenderer
{
    private static DeferredTerrainDecalBridge? _current;
    private readonly ICoreClientAPI _api;
    private readonly Func<bool> _bound;
    private readonly Harmony _harmony=new("drtagx.deferred.terrain-decals");
    private readonly ShadowGlState _state=new(false);
    private MeshDataPoolManager[]? _decor;
    private Entry[] _entries=CreateEntries(4);
    private int _count;
    private int _terrainProgram,_terrainForwardLocation=-1;
    private bool _collecting,_replaying;
    public double RenderOrder=>0.36;
    public int RenderRange=>int.MaxValue;

    private sealed class Entry
    {
        internal MeshDataPoolManager? Manager;
        internal IShaderProgram? Shader;
        internal readonly Vec3d Player=new();
        internal string Origin="origin";
        internal EnumFrustumCullMode CullMode;
        internal int Program,AlphaLocation,HaxyLocation,BypassLocation,TextureLocation,LinearLocation,OriginLocation;
        internal readonly float[] SavedOrigin=new float[3];
        internal int TextureUnit,LinearUnit,Texture,LinearTexture,Sampler,LinearSampler;
        internal int SrcRgb,DstRgb,SrcAlpha,DstAlpha,EquationRgb,EquationAlpha,DepthFunction;
        internal float Alpha;
        internal int Haxy;
        internal bool Blend,Depth,Cull,DepthMask;
    }
    private static Entry[] CreateEntries(int size)
    {var result=new Entry[size];for(int i=0;i<size;++i)result[i]=new();return result;}

    internal DeferredTerrainDecalBridge(ICoreClientAPI api,object deferredOwner)
    {
        _api=api;
        // Resolve the existing Sheyder ownership gate once; avoid reflective
        // reads/boxed bools in the steady-state frame path.
        var field=AccessTools.Field(deferredOwner.GetType(),"_boundThisFrame")
            ?? throw new MissingFieldException("DeferredRenderer._boundThisFrame");
        _bound=Expression.Lambda<Func<bool>>(Expression.Field(Expression.Constant(deferredOwner),field)).Compile();
        var method=AccessTools.Method(typeof(MeshDataPoolManager),"Render",new[]{typeof(Vec3d),typeof(string),typeof(EnumFrustumCullMode)})
            ?? throw new MissingMethodException("MeshDataPoolManager.Render");
        try
        {
            _harmony.Patch(method,prefix:new HarmonyMethod(typeof(DeferredTerrainDecalBridge),nameof(BeforeNativeDraw)));
            _current=this;
            api.Event.RegisterRenderer(this,EnumRenderStage.Opaque,"drtagx_deferred_decal_capture");
            api.Event.ReloadShader+=Reload;
        }
        catch{Dispose();throw;} // A partial install must never suppress native draws.
    }
    public void OnRenderFrame(float deltaTime,EnumRenderStage stage)
    {
        Clear();_collecting=_bound();_decor=null;
        // Only draws inside the prepared G-buffer may emit unlit metadata.
        // Reopen this gate every frame before native opaque terrain at .37.
        SetTerrainForward(!_collecting);
        if(_collecting&&_api.World is ClientMain world&&NativeShadowFields.Renderer(world) is { } renderer&&
            NativeShadowFields.Passes(renderer) is { } passes&&passes.Length>(int)EnumChunkRenderPass.Decor)
            _decor=passes[(int)EnumChunkRenderPass.Decor];
    }
    private static bool BeforeNativeDraw(MeshDataPoolManager __instance,Vec3d __0,string __1,EnumFrustumCullMode __2)
    {
        var owner=_current;
        if(owner==null||!owner._collecting||owner._replaying||owner._decor==null)return true;
        bool match=false;
        for(int i=0;i<owner._decor.Length;++i)if(ReferenceEquals(owner._decor[i],__instance)){match=true;break;}
        if(!match||owner._api.Render.CurrentActiveShader is not { PassName:"chunkopaque" } shader)return true;
        return !owner.Capture(__instance,__0,__1,__2,shader);
    }
    private bool Capture(MeshDataPoolManager manager,Vec3d player,string origin,EnumFrustumCullMode mode,IShaderProgram shader)
    {
        if(_count==_entries.Length)
        {
            // Only atlas-page growth allocates; entries/vectors are reused.
            int old=_entries.Length;Array.Resize(ref _entries,old*2);
            for(int i=old;i<_entries.Length;++i)_entries[i]=new();
        }
        var e=_entries[_count];
        bool programChanged=e.Program!=shader.ProgramId;
        if(programChanged)
        {
            e.Program=shader.ProgramId;e.AlphaLocation=GL.GetUniformLocation(e.Program,"alphaTest");
            e.HaxyLocation=GL.GetUniformLocation(e.Program,"haxyFade");e.BypassLocation=GL.GetUniformLocation(e.Program,"drtForwardDecalPass");
            e.TextureLocation=GL.GetUniformLocation(e.Program,"terrainTex");e.LinearLocation=GL.GetUniformLocation(e.Program,"terrainTexLinear");
        }
        // Shader reload/incompatible variants keep their original native draw.
        if(e.BypassLocation<0||e.AlphaLocation<0||e.HaxyLocation<0||e.TextureLocation<0||e.LinearLocation<0)return false;
        if(programChanged||e.Origin!=origin)e.OriginLocation=GL.GetUniformLocation(e.Program,origin);
        e.Manager=manager;e.Shader=shader;e.Player.Set(player.X,player.Y,player.Z);e.Origin=origin;e.CullMode=mode;
        GL.GetUniform(e.Program,e.AlphaLocation,out e.Alpha);
        GL.GetUniform(e.Program,e.HaxyLocation,out e.Haxy);
        GL.GetUniform(e.Program,e.TextureLocation,out e.TextureUnit);GL.GetUniform(e.Program,e.LinearLocation,out e.LinearUnit);
        int active=GL.GetInteger(GetPName.ActiveTexture);
        try
        {
            GL.ActiveTexture(TextureUnit.Texture0+e.TextureUnit);e.Texture=GL.GetInteger(GetPName.TextureBinding2D);e.Sampler=GL.GetInteger(GetPName.SamplerBinding);
            GL.ActiveTexture(TextureUnit.Texture0+e.LinearUnit);e.LinearTexture=GL.GetInteger(GetPName.TextureBinding2D);e.LinearSampler=GL.GetInteger(GetPName.SamplerBinding);
        }
        finally{GL.ActiveTexture((TextureUnit)active);}
        e.Blend=GL.IsEnabled(EnableCap.Blend);e.Depth=GL.IsEnabled(EnableCap.DepthTest);e.Cull=GL.IsEnabled(EnableCap.CullFace);
        GL.GetBoolean(GetPName.DepthWritemask,out e.DepthMask);e.DepthFunction=GL.GetInteger(GetPName.DepthFunc);
        e.SrcRgb=GL.GetInteger(GetPName.BlendSrcRgb);e.DstRgb=GL.GetInteger(GetPName.BlendDstRgb);
        e.SrcAlpha=GL.GetInteger(GetPName.BlendSrcAlpha);e.DstAlpha=GL.GetInteger(GetPName.BlendDstAlpha);
        e.EquationRgb=GL.GetInteger(GetPName.BlendEquationRgb);e.EquationAlpha=GL.GetInteger(GetPName.BlendEquationAlpha);
        ++_count;return true;
    }
    internal void Flush()
    {
        _collecting=false;
        // OpaqueWaterPlant uses chunkopaque in AfterOIT, after .39 relighting.
        // Leave the shared bypass on even when there are no Decor submissions,
        // or a daylight-only lily would emit zero blocklight with no later relight.
        SetTerrainForward(true);
        if(_count==0)return;
        // The existing .39 relight has restored the primary framebuffer. These
        // are the same native submissions, now over radiance instead of metadata.
        _replaying=true;
        try{for(int i=0;i<_count;++i)Replay(_entries[i]);}
        finally{_replaying=false;Clear();}
    }
    private void SetTerrainForward(bool forward)
    {
        var shader=_api.Render.GetEngineShader(EnumShaderProgram.Chunkopaque);
        if(shader==null||shader.Disposed||shader.LoadError||shader.ProgramId<=0)return;
        if(_terrainProgram!=shader.ProgramId)
        {
            _terrainProgram=shader.ProgramId;
            _terrainForwardLocation=GL.GetUniformLocation(_terrainProgram,"drtForwardDecalPass");
        }
        // OpenGL 4.3 supports direct uniform updates: preserve both the current
        // GL program and the native API's active-shader tracking without rebinding.
        if(_terrainForwardLocation>=0)GL.ProgramUniform1(_terrainProgram,_terrainForwardLocation,forward?1:0);
    }
    private void Replay(Entry e)
    {
        var shader=e.Shader!;if(shader.Disposed||shader.LoadError||shader.ProgramId!=e.Program)return;
        var previous=_api.Render.CurrentActiveShader;
        _state.Capture();
        int src=GL.GetInteger(GetPName.BlendSrcRgb),dst=GL.GetInteger(GetPName.BlendDstRgb),
            srcA=GL.GetInteger(GetPName.BlendSrcAlpha),dstA=GL.GetInteger(GetPName.BlendDstAlpha),
            eq=GL.GetInteger(GetPName.BlendEquationRgb),eqA=GL.GetInteger(GetPName.BlendEquationAlpha);
        GL.GetUniform(e.Program,e.BypassLocation,out int bypass);GL.GetUniform(e.Program,e.AlphaLocation,out float alpha);
        GL.GetUniform(e.Program,e.HaxyLocation,out int haxy);
        if(e.OriginLocation>=0)GL.GetUniform(e.Program,e.OriginLocation,e.SavedOrigin);
        GL.ActiveTexture(TextureUnit.Texture0+e.TextureUnit);
        int texture=GL.GetInteger(GetPName.TextureBinding2D),sampler=GL.GetInteger(GetPName.SamplerBinding);
        GL.ActiveTexture(TextureUnit.Texture0+e.LinearUnit);
        int linear=GL.GetInteger(GetPName.TextureBinding2D),linearSampler=GL.GetInteger(GetPName.SamplerBinding);
        try
        {
            shader.Use();GL.Uniform1(e.BypassLocation,1);GL.Uniform1(e.AlphaLocation,e.Alpha);GL.Uniform1(e.HaxyLocation,e.Haxy);
            GL.ActiveTexture(TextureUnit.Texture0+e.TextureUnit);GL.BindTexture(TextureTarget.Texture2D,e.Texture);GL.BindSampler(e.TextureUnit,e.Sampler);
            GL.ActiveTexture(TextureUnit.Texture0+e.LinearUnit);GL.BindTexture(TextureTarget.Texture2D,e.LinearTexture);GL.BindSampler(e.LinearUnit,e.LinearSampler);
            Set(EnableCap.Blend,e.Blend);Set(EnableCap.DepthTest,e.Depth);Set(EnableCap.CullFace,e.Cull);
            GL.DepthMask(e.DepthMask);GL.DepthFunc((DepthFunction)e.DepthFunction);
            GL.BlendFuncSeparate((BlendingFactorSrc)e.SrcRgb,(BlendingFactorDest)e.DstRgb,(BlendingFactorSrc)e.SrcAlpha,(BlendingFactorDest)e.DstAlpha);
            GL.BlendEquationSeparate((BlendEquationMode)e.EquationRgb,(BlendEquationMode)e.EquationAlpha);
            e.Manager!.Render(e.Player,e.Origin,e.CullMode);
        }
        finally
        {
            GL.UseProgram(e.Program);GL.Uniform1(e.BypassLocation,bypass);GL.Uniform1(e.AlphaLocation,alpha);GL.Uniform1(e.HaxyLocation,haxy);
            if(e.OriginLocation>=0)GL.Uniform3(e.OriginLocation,e.SavedOrigin[0],e.SavedOrigin[1],e.SavedOrigin[2]);
            GL.ActiveTexture(TextureUnit.Texture0+e.LinearUnit);GL.BindTexture(TextureTarget.Texture2D,linear);GL.BindSampler(e.LinearUnit,linearSampler);
            GL.ActiveTexture(TextureUnit.Texture0+e.TextureUnit);GL.BindTexture(TextureTarget.Texture2D,texture);GL.BindSampler(e.TextureUnit,sampler);
            GL.BlendFuncSeparate((BlendingFactorSrc)src,(BlendingFactorDest)dst,(BlendingFactorSrc)srcA,(BlendingFactorDest)dstA);
            GL.BlendEquationSeparate((BlendEquationMode)eq,(BlendEquationMode)eqA);
            shader.Stop();previous?.Use();_state.Dispose();
        }
    }
    private static void Set(EnableCap cap,bool enabled){if(enabled)GL.Enable(cap);else GL.Disable(cap);}
    private void Clear(){for(int i=0;i<_count;++i){_entries[i].Manager=null;_entries[i].Shader=null;}_count=0;}
    private bool Reload(){Clear();_collecting=false;_terrainProgram=0;for(int i=0;i<_entries.Length;++i)_entries[i].Program=0;return true;}
    public void Dispose()
    {
        // Removing the bridge returns the producer to Sheyder's original gate.
        SetTerrainForward(false);
        _api.Event.ReloadShader-=Reload;
        _api.Event.UnregisterRenderer(this,EnumRenderStage.Opaque);_harmony.UnpatchAll(_harmony.Id);Clear();_decor=null;
        if(ReferenceEquals(_current,this))_current=null;
    }
}
