using System;
using System.Linq.Expressions;
using HarmonyLib;
using OpenTK.Graphics.OpenGL4;
using Vintagestory.API.Client;

namespace DRTAgX;

/// <summary>Owns only the plane and diffuse-irradiance textures lent to native MRTs.</summary>
internal sealed partial class DeferredReceiverResources : IDisposable
{
    private readonly ICoreClientAPI _api;
    private readonly Func<FrameBufferRef?> _scratch;
    private readonly HdrPassState _allocationState = new(false);

    private readonly DrawBuffersEnum[] _geometrySaved = new DrawBuffersEnum[6],
        _relightSaved = new DrawBuffersEnum[6],
        _route = new DrawBuffersEnum[6];

    private readonly int[] _invalid = { int.MinValue, 0, 0, 0 };
    private readonly uint[] _invalidBounds = { uint.MaxValue, uint.MaxValue, uint.MaxValue, uint.MaxValue };
    private readonly float[] _zero = new float[4];
    private readonly bool[] _planeMask = new bool[4], _boundsMask = new bool[4];
    private int _plane, _bounds, _irradiance, _width, _height, _geometryFbo, _primaryFbo, _normal;

    private bool _geometryActive,
        _relightActive,
        _prepared,
        _schedulingReady,
        _failed,
        _anchorReady,
        _irradianceAvailable,
        _relightTexturesBound,
        _auxiliaryWarning;

    private int _anchorX, _anchorY, _anchorZ;
    internal int PlaneTexture => _plane;
    internal int BoundsTexture => _bounds;
    internal int IrradianceTexture => _irradiance;
    internal int NormalTexture => _normal;
    internal int Width => _width;
    internal int Height => _height;

    internal bool Ready => _prepared;

    // A validated deferred route can discover/bake its first light without
    // writing auxiliary receiver targets while the published source set is empty.
    internal bool SchedulingReady => _schedulingReady;

    internal DeferredReceiverResources(ICoreClientAPI api, object deferredOwner)
    {
        _api = api;
        var field = AccessTools.Field(deferredOwner.GetType(), "_gbuffer") ??
                    throw new MissingFieldException("DeferredRenderer._gbuffer");
        _scratch = Expression.Lambda<Func<FrameBufferRef?>>(Expression.Field(Expression.Constant(deferredOwner), field))
            .Compile();
        InstallBindings();
    }

    internal void BeginGeometry(bool bound)
    {
        EndRelight();
        EndGeometry();
        _prepared = false;
        _schedulingReady = false;
        _irradianceAvailable = false;
        SetProducerPayload(false);
        var cache = PlacedLightSurfaceCache.Current;
        _anchorReady = cache is { Ready: true, Active: true };
        // Active is the feature policy published before terrain, independent of
        // completed maps/pages. Inactive PLS has no auxiliary consumer: retain
        // the native payload and decal fallback without attaching/clearing MRTs.
        // A not-yet-ready cache still needs bootstrap ownership. Only a known
        // ready empty snapshot skips the payload after route validation below.
        if (cache is { Active: false }) return;
        if (_anchorReady)
        {
            _anchorX = cache!.AnchorCellX;
            _anchorY = cache.AnchorCellY;
            _anchorZ = cache.AnchorCellZ;
        }

        if (!bound || _failed || _scratch() is not { Disposed: false, FboId: > 0, Width: > 0, Height: > 0 } scratch ||
            _api.Render.FrameBuffers is not { Count: > 0 } buffers ||
            buffers[0] is not { Disposed: false, FboId: > 0 } primary) return;
        if (scratch.Width != primary.Width || scratch.Height != primary.Height) return;
        int previous = GL.GetInteger(GetPName.DrawFramebufferBinding);
        try
        {
            if (previous != scratch.FboId)
                throw new InvalidOperationException("deferred scratch is not the native draw owner");
            GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer, primary.FboId);
            GL.GetFramebufferAttachmentParameter(FramebufferTarget.DrawFramebuffer,
                FramebufferAttachment.ColorAttachment4,
                FramebufferParameterName.FramebufferAttachmentObjectName, out int primaryOccupied);
            GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer, scratch.FboId);
            if (primaryOccupied != 0) throw new InvalidOperationException("primary auxiliary MRT slot already owned");
            Ensure(scratch.Width, scratch.Height);
            _geometryFbo = scratch.FboId;
            _primaryFbo = primary.FboId;
            GL.GetFramebufferAttachmentParameter(FramebufferTarget.DrawFramebuffer,
                FramebufferAttachment.ColorAttachment2,
                FramebufferParameterName.FramebufferAttachmentObjectName, out _normal);
            if (_normal <= 0) throw new InvalidOperationException("native normal attachment missing");
            Attach(_geometryFbo, _plane, _geometrySaved, true);
            _geometryActive = true;
            _schedulingReady = true;
            if (cache is { Ready: true, SourceCount: 0 })
            {
                // Keep the exact native attachment/format validation above and
                // retain storage, but do not clear or rasterize three unused
                // full-resolution targets. Discovery uses ordinary scene depth.
                // A first source published after this boundary receives native
                // fallback until next frame's matching geometry is prepared.
                EndGeometry();
                return;
            }

            ClearAuxiliary(true);
            _prepared = true;
            SetProducerPayload(true);
        }
        catch (Exception ex)
        {
            EndGeometry();
            _prepared = _schedulingReady = false;
            _failed = true;
            _api.Logger.Warning("[DRTAgX] Authored receiver targets inactive; native lighting retained: " + ex.Message);
        }
        finally
        {
            GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer, previous);
        }
    }

    internal void BeforeRelight()
    {
        if (!_prepared)
        {
            return;
        }

        EndGeometry();
        // The worker reads the completed native terrain attachments, once per
        // frame before the existing .39 relight. It owns all queue/SSBO work.
        var cache = PlacedLightSurfaceCache.Current;
        if (!AnchorMatches(cache))
        {
            _anchorReady = false;
            return;
        }

        cache!.PrepareDeferred(_api, _plane, _normal, _bounds, _width, _height);
    }

    private bool AnchorMatches(PlacedLightSurfaceCache? cache) =>
        _anchorReady && cache is { Ready: true, Active: true } &&
        cache.AnchorCellX == _anchorX && cache.AnchorCellY == _anchorY && cache.AnchorCellZ == _anchorZ;

    private void Ensure(int width, int height)
    {
        if (_plane != 0 && _bounds != 0 && _irradiance != 0 && _width == width && _height == height) return;
        _allocationState.Capture();
        using (_allocationState)
        {
            GL.GetInteger(GetPName.MaxDrawBuffers, out int draws);
            GL.GetInteger(GetPName.MaxColorAttachments, out int attachments);
            if (draws < 6 || attachments < 6) throw new InvalidOperationException("six MRT outputs unavailable");
            DeleteTextures();
            try
            {
                _plane = Allocate(PixelInternalFormat.R32i, PixelFormat.RedInteger, PixelType.Int, width, height);
                _bounds = Allocate(PixelInternalFormat.Rg32ui, PixelFormat.RgInteger, PixelType.UnsignedInt, width,
                    height);
                _irradiance = Allocate(PixelInternalFormat.Rgba16f, PixelFormat.Rgba, PixelType.HalfFloat, width,
                    height);
                _width = width;
                _height = height;
            }
            catch
            {
                DeleteTextures();
                throw;
            }
        }
    }

    private static int Allocate(PixelInternalFormat format, PixelFormat channels, PixelType type, int width, int height)
    {
        int texture = GL.GenTexture();
        try
        {
            GL.ActiveTexture(TextureUnit.Texture0);
            GL.BindTexture(TextureTarget.Texture2D, texture);
            GL.BindBuffer(BufferTarget.PixelUnpackBuffer, 0);
            GL.TexImage2D(TextureTarget.Texture2D, 0, format, width, height, 0, channels, type, IntPtr.Zero);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter,
                (int)TextureMinFilter.Nearest);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter,
                (int)TextureMagFilter.Nearest);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS,
                (int)TextureWrapMode.ClampToEdge);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT,
                (int)TextureWrapMode.ClampToEdge);
            return texture;
        }
        catch
        {
            GL.DeleteTexture(texture);
            throw;
        }
    }

    private void Attach(int fbo, int texture, DrawBuffersEnum[] saved, bool geometry = false)
    {
        GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer, fbo);
        GL.GetFramebufferAttachmentParameter(FramebufferTarget.DrawFramebuffer, FramebufferAttachment.ColorAttachment4,
            FramebufferParameterName.FramebufferAttachmentObjectName, out int occupied);
        if (occupied != 0) throw new InvalidOperationException("auxiliary MRT slot already owned");
        for (int i = 0; i < 6; ++i)
        {
            saved[i] = (DrawBuffersEnum)GL.GetInteger(GetPName.DrawBuffer0 + i);
            _route[i] = saved[i];
        }

        _route[4] = DrawBuffersEnum.ColorAttachment4;
        GL.FramebufferTexture2D(FramebufferTarget.DrawFramebuffer, FramebufferAttachment.ColorAttachment4,
            TextureTarget.Texture2D, texture, 0);
        if (geometry)
        {
            GL.GetFramebufferAttachmentParameter(FramebufferTarget.DrawFramebuffer,
                FramebufferAttachment.ColorAttachment5,
                FramebufferParameterName.FramebufferAttachmentObjectName, out int boundsOccupied);
            if (boundsOccupied != 0)
            {
                GL.FramebufferTexture2D(FramebufferTarget.DrawFramebuffer, FramebufferAttachment.ColorAttachment4,
                    TextureTarget.Texture2D, 0, 0);
                throw new InvalidOperationException("receiver bounds MRT slot already owned");
            }

            _route[5] = DrawBuffersEnum.ColorAttachment5;
            GL.FramebufferTexture2D(FramebufferTarget.DrawFramebuffer, FramebufferAttachment.ColorAttachment5,
                TextureTarget.Texture2D, _bounds, 0);
        }

        GL.DrawBuffers(6, _route);
        if (GL.CheckFramebufferStatus(FramebufferTarget.DrawFramebuffer) != FramebufferErrorCode.FramebufferComplete)
        {
            GL.FramebufferTexture2D(FramebufferTarget.DrawFramebuffer, FramebufferAttachment.ColorAttachment4,
                TextureTarget.Texture2D, 0, 0);
            if (geometry)
                GL.FramebufferTexture2D(FramebufferTarget.DrawFramebuffer, FramebufferAttachment.ColorAttachment5,
                    TextureTarget.Texture2D, 0, 0);
            GL.DrawBuffers(6, saved);
            throw new InvalidOperationException("auxiliary MRT incomplete");
        }
    }

    private void ClearAuxiliary(bool integer)
    {
        // ClearBuffer obeys color masks/scissor; clear the entire validity field
        // without changing the subsequent native alpha/depth raster semantics.
        bool scissor = GL.IsEnabled(EnableCap.ScissorTest);
        GL.GetBoolean(GetIndexedPName.ColorWritemask, 4, _planeMask);
        if (integer) GL.GetBoolean(GetIndexedPName.ColorWritemask, 5, _boundsMask);
        try
        {
            GL.Disable(EnableCap.ScissorTest);
            // Indexed masks belong to draw-buffer slots. Never broadcast a
            // temporary clear mask across the native color/glow/normal MRTs.
            GL.ColorMask(4, true, true, true, true);
            if (integer) GL.ColorMask(5, true, true, true, true);
            if (integer)
            {
                GL.ClearBuffer(ClearBuffer.Color, 4, _invalid);
                GL.ClearBuffer(ClearBuffer.Color, 5, _invalidBounds);
            }
            else GL.ClearBuffer(ClearBuffer.Color, 4, _zero);
        }
        finally
        {
            GL.ColorMask(4, _planeMask[0], _planeMask[1], _planeMask[2], _planeMask[3]);
            if (integer) GL.ColorMask(5, _boundsMask[0], _boundsMask[1], _boundsMask[2], _boundsMask[3]);
            if (scissor) GL.Enable(EnableCap.ScissorTest);
        }
    }

    private void EndGeometry()
    {
        if (!_geometryActive) return;
        Detach(_geometryFbo, _geometrySaved, true);
        _geometryActive = false;
    }

    internal void EndRelight()
    {
        if (_relightActive) Detach(_primaryFbo, _relightSaved);
        if (_relightTexturesBound) RestoreRelightTextures();
        _relightActive = _relightTexturesBound = false;
    }

    private static void Detach(int fbo, DrawBuffersEnum[] saved, bool geometry = false)
    {
        int previous = GL.GetInteger(GetPName.DrawFramebufferBinding);
        GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer, fbo);
        GL.FramebufferTexture2D(FramebufferTarget.DrawFramebuffer, FramebufferAttachment.ColorAttachment4,
            TextureTarget.Texture2D, 0, 0);
        if (geometry)
            GL.FramebufferTexture2D(FramebufferTarget.DrawFramebuffer, FramebufferAttachment.ColorAttachment5,
                TextureTarget.Texture2D, 0, 0);
        GL.DrawBuffers(6, saved);
        GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer, previous);
    }

    private void DeleteTextures()
    {
        if (_plane != 0) GL.DeleteTexture(_plane);
        if (_irradiance != 0) GL.DeleteTexture(_irradiance);
        if (_bounds != 0) GL.DeleteTexture(_bounds);
        _plane = _bounds = _irradiance = _width = _height = 0;
    }

    internal void Reload()
    {
        EndRelight();
        EndGeometry();
        _failed = _prepared = _schedulingReady = false;
        _locations.Clear();
        SetProducerPayload(false);
    }

    public void Dispose()
    {
        Reload();
        _harmony.UnpatchAll(_harmony.Id);
        DeleteTextures();
        if (ReferenceEquals(_current, this)) _current = null;
    }
}