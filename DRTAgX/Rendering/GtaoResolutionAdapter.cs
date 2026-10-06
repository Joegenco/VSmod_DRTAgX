using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using HarmonyLib;
using OpenTK.Graphics.OpenGL4;
using Vintagestory.API.Client;
using Vintagestory.Client.NoObf;

namespace DRTAgX;

/// <summary>Temporarily lends owned quarter targets to the known GTAO pass, then restores native ownership.</summary>
internal sealed class GtaoResolutionAdapter : IDisposable
{
    private const string PatchId = "drtagx.gtao.resolution";
    private static GtaoResolutionAdapter? _current;
    private readonly Harmony _harmony = new(PatchId);
    private readonly ICoreClientAPI _api;
    private readonly HdrPassState _state = new(false);
    private readonly FrameBufferRef?[] _owned = new FrameBufferRef[3], _original = new FrameBufferRef[3];
    private readonly Signature[] _validated = new Signature[3];
    private readonly record struct Signature(FrameBufferRef Buffer, int Fbo, int Texture, int Width, int Height);
    private IList<FrameBufferRef>? _buffers;
    private Func<object, bool>? _native;
    private ShaderProgram? _shader;
    private int _vao, _w, _h;
    private int _quality=-1;
    private bool _borrowed, _failed, _fallback;

    internal GtaoResolutionAdapter(ICoreClientAPI api)
    {
        _api = api;
        try
        {
            var method = AccessTools.Method("SheyderMod.Features.GTAO.GtaoRenderer:RenderGtaoPass");
            if (method == null || method.ReturnType != typeof(bool)) return;
            var owner = Expression.Parameter(typeof(object));
            _native = Expression.Lambda<Func<object,bool>>(Expression.Call(Expression.Convert(owner, method.DeclaringType!), method), owner).Compile();
            _current = this;
            _harmony.Patch(method, prefix: new HarmonyMethod(typeof(GtaoResolutionAdapter), nameof(Begin)),
                postfix: new HarmonyMethod(typeof(GtaoResolutionAdapter), nameof(End)), finalizer: new HarmonyMethod(typeof(GtaoResolutionAdapter), nameof(Restore)));
            var rebuild=AccessTools.Method("Vintagestory.Client.NoObf.ClientPlatformWindows:RebuildFrameBuffers");
            if(rebuild!=null) _harmony.Patch(rebuild,postfix:new HarmonyMethod(typeof(GtaoResolutionAdapter),nameof(Recreated)));
            api.Event.ReloadShader += Reload; Reload();
        }
        catch (Exception ex) { api.Logger.Warning("[DRTAgX] Quarter GTAO inactive; original targets retained: " + ex.Message); }
    }

    private bool Reload()
    {
        Restore(); _failed = false; Array.Clear(_validated); _shader?.Dispose();
        _shader = (ShaderProgram)_api.Shader.NewShaderProgram(); _shader.AssetDomain="drtagx";
        _api.Shader.RegisterFileShaderProgram("ao_reconstruct", _shader);
        if (!_shader.Compile()) { _shader.Dispose(); _shader=null; _api.Logger.Warning("[DRTAgX] Quarter GTAO reconstruction unavailable; original targets retained."); }
        return true;
    }

    private static void Begin(out bool __state)
    {
        __state = _current?.Borrow() == true;
    }
    private bool Borrow()
    {
        if(_quality!=FrameQuality.Generation) { _quality=FrameQuality.Generation; _failed=false; }
        if (!FrameQuality.Current.Performance || _fallback || _failed || _shader==null) return false;
        var buffers = _api.Render.FrameBuffers;
        if (buffers == null || buffers.Count<=15 || buffers[0]?.ColorTextureIds is not {Length: >=4}) return false;
        for(int i=0;i<3;++i)
            if (buffers[13+i] is not {Disposed:false, FboId:>0, Width:>1, Height:>1} || buffers[13+i].ColorTextureIds is not {Length:>0}) return false;
        try
        {
            for(int i=0;i<3;++i)
            {
                var target=buffers[13+i];
                var signature=new Signature(target,target.FboId,target.ColorTextureIds[0],target.Width,target.Height);
                if(_validated[i]==signature) continue;
                _state.Capture(); using(_state)
                {
                    // The raw metadata array also holds an unattached 16x16 noise texture.
                    // Validate physical attachments, not the metadata array length.
                    GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer,target.FboId);
                    GL.GetInteger(GetPName.MaxColorAttachments,out int maxAttachments);
                    for(int attachment=0;attachment<maxAttachments;++attachment)
                    {
                        GL.GetFramebufferAttachmentParameter(FramebufferTarget.DrawFramebuffer,FramebufferAttachment.ColorAttachment0+attachment,
                            FramebufferParameterName.FramebufferAttachmentObjectName,out int id);
                        if(id!=(attachment==0?signature.Texture:0)) throw new InvalidOperationException("unrecognized AO MRT attachments");
                    }
                    GL.GetFramebufferAttachmentParameter(FramebufferTarget.DrawFramebuffer,FramebufferAttachment.DepthAttachment,
                        FramebufferParameterName.FramebufferAttachmentObjectName,out int attachedDepth);
                    if(attachedDepth!=0) throw new InvalidOperationException("unrecognized physical AO depth attachment");
                    GL.ActiveTexture(TextureUnit.Texture0); GL.BindTexture(TextureTarget.Texture2D,signature.Texture);
                    GL.GetTexLevelParameter(TextureTarget.Texture2D,0,GetTextureParameter.TextureRedSize,out int red);
                    if(red!=8 || target.DepthTextureId!=0) throw new InvalidOperationException("unrecognized AO precision/depth ownership");
                }
                _validated[i]=signature;
            }
            int w=Math.Max(1,buffers[0].Width/4), h=Math.Max(1,buffers[0].Height/4);
            if (_owned[0]==null || _owned[1]==null || _owned[2]==null || _vao==0 || _w!=w || _h!=h)
            {
                _state.Capture(); using (_state) Ensure(w,h);
            }
            _buffers=buffers;
            if (_owned[0]!.ColorTextureIds.Length!=buffers[13].ColorTextureIds.Length)
            {
                // Preserve the native raw noise alias for optional wrappers during the loan.
                int color=_owned[0]!.ColorTextureIds[0];
                _owned[0]!.ColorTextureIds=(int[])buffers[13].ColorTextureIds.Clone();
                _owned[0]!.ColorTextureIds[0]=color;
            }
            for(int i=1;i<_owned[0]!.ColorTextureIds.Length;++i) _owned[0]!.ColorTextureIds[i]=buffers[13].ColorTextureIds[i];
            for(int i=0;i<3;++i) { _original[i]=buffers[13+i]; buffers[13+i]=_owned[i]!; }
            _borrowed=true; return true;
        }
        catch(Exception ex) { Fail(ex); return false; }
    }

    private static void End(object __instance, bool __state, ref bool __result)
    {
        var owner = _current;
        if (!__state || owner == null) return;
        Restore();
        if (!__result) return; // Retain the provider's own inactive/failure decision.
        try { owner.Reconstruct(); }
        catch(Exception ex)
        {
            owner.Fail(ex);
            // Re-run the original GTAO on native targets; do not silently turn on vanilla SSAO.
            owner._fallback=true;
            try { __result=owner._native!(__instance); }
            finally { owner._fallback=false; }
        }
    }

    private void Reconstruct()
    {
        var output=_original[1]!; // Native vertical denoise slot 14 is the final visibility consumer.
        var active=_api.Render.CurrentActiveShader;
        _state.Capture(); using (_state)
        {
            try
            {
                GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer,output.FboId);
                GL.Viewport(0,0,output.Width,output.Height); GL.BindVertexArray(_vao);
                _shader!.Use();
                _shader.BindTexture2D("aoLow",_owned[1]!.ColorTextureIds[0],0);
                _shader.BindTexture2D("gPosition",_api.Render.FrameBuffers[0].ColorTextureIds[3],1);
                GL.DrawArrays(PrimitiveType.Triangles,0,3);
            }
            finally { try { _shader!.Stop(); } finally { active?.Use(); } }
        }
    }

    private static void Restore()
    {
        var owner=_current;
        if (owner?._borrowed!=true || owner._buffers==null) return;
        for(int i=0;i<3;++i) owner._buffers[13+i]=owner._original[i]!;
        owner._borrowed=false;
    }
    private static void Recreated() { if(_current!=null) Array.Clear(_current._validated); }

    private void Ensure(int w,int h)
    {
        DeleteTargets(); _w=w; _h=h;
        GL.BindBuffer(BufferTarget.PixelUnpackBuffer,0); GL.ActiveTexture(TextureUnit.Texture0);
        for(int i=0;i<3;++i)
        {
            var target=new FrameBufferRef {Width=w,Height=h,FboId=GL.GenFramebuffer(),ColorTextureIds=new[]{GL.GenTexture()}};
            _owned[i]=target;
            GL.BindTexture(TextureTarget.Texture2D,target.ColorTextureIds[0]);
            GL.TexImage2D(TextureTarget.Texture2D,0,PixelInternalFormat.R8,w,h,0,PixelFormat.Red,PixelType.UnsignedByte,IntPtr.Zero);
            GL.TexParameter(TextureTarget.Texture2D,TextureParameterName.TextureMinFilter,(int)TextureMinFilter.Linear);
            GL.TexParameter(TextureTarget.Texture2D,TextureParameterName.TextureMagFilter,(int)TextureMagFilter.Linear);
            GL.TexParameter(TextureTarget.Texture2D,TextureParameterName.TextureWrapS,(int)TextureWrapMode.ClampToEdge);
            GL.TexParameter(TextureTarget.Texture2D,TextureParameterName.TextureWrapT,(int)TextureWrapMode.ClampToEdge);
            GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer,target.FboId);
            GL.FramebufferTexture2D(FramebufferTarget.DrawFramebuffer,FramebufferAttachment.ColorAttachment0,TextureTarget.Texture2D,target.ColorTextureIds[0],0);
            GL.DrawBuffer(DrawBufferMode.ColorAttachment0);
            if(GL.CheckFramebufferStatus(FramebufferTarget.DrawFramebuffer)!=FramebufferErrorCode.FramebufferComplete) throw new InvalidOperationException("quarter AO framebuffer incomplete");
        }
        _vao=GL.GenVertexArray();
    }
    private void Fail(Exception ex)
    {
        Restore(); _failed=true;
        _api.Logger.Warning("[DRTAgX] Quarter GTAO failed; original GTAO retained until reload: "+ex.Message);
    }
    private void DeleteTargets()
    {
        foreach(var target in _owned) if(target!=null) { GL.DeleteTexture(target.ColorTextureIds[0]); GL.DeleteFramebuffer(target.FboId); }
        Array.Clear(_owned);
        if(_vao!=0) GL.DeleteVertexArray(_vao); _vao=0;
    }
    public void Dispose()
    {
        Restore(); _harmony.UnpatchAll(PatchId); _api.Event.ReloadShader-=Reload;
        _shader?.Dispose(); DeleteTargets();
        if(ReferenceEquals(_current,this)) _current=null;
    }
}
