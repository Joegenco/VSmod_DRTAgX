using System;
using System.Collections.Generic;
using System.Diagnostics;
using OpenTK.Graphics.OpenGL4;
using Vintagestory.API.Client;
using Vintagestory.API.MathTools;
using Vintagestory.Client.NoObf;

namespace DRTAgX;

/// <summary>Render-thread owner of complete world-anchored placed-light depth maps.</summary>
internal sealed partial class StaticTerrainShadowMaps : IDisposable
{
    internal const int MaxSources = 128, MaxPublishedSources = MaxSources + 1;
    internal const int MaxDepthSources = 1024;

    // Sparse edits can affect several lamps. Finish a small visible refresh
    // batch without letting one edit turn into an unbounded shadow draw burst.
    private const int VisibleEditPromotionLimit = 4;
    private const double VisibleEditCpuBudgetMilliseconds = 2;

    internal const int FullResolutionFaceSize = 192;

    // Manual resolution experiment: 1 gives 192px; 2 restores the previous 96px.
    // Allocation, bake viewport and layer copies all use TargetFaceSize; GLSL reads
    // textureSize for its filter footprint. Rebuild/restart to replace the cache.
    internal const int ResolutionDivisor = 1;

    internal const int FaceSize = FullResolutionFaceSize / ResolutionDivisor;

    // Each quality owner retains its complete cache; toggling does not repeatedly discard depth.
    internal int TargetFaceSize { get; init; } = FaceSize;
    internal const float Range = 22f;
    internal const double SourceHeight = 0.5;

    internal readonly record struct ActiveLight(
        StaticLightSources.Source Source,
        int Slot,
        float Fade,
        float ShadowBlend,
        int SecondarySlot = -1,
        bool ReplacementPair = false)
    {
        internal PlacedLightView.Bounds Bounds { get; init; }
    }

    private static readonly float[] LightOrigin = { 0f, 0f, 0f };
    private readonly MovingLightShadowRenderer _terrainDraws;
    private readonly PointTerrainShadowProgram _pointProgram = new("drtagx_static_point_shadowmap");
    private readonly StableContainerShadowRenderer _containerShadows = new("drtagx_static_container_shadow");
    private int _excludeWindLocation;
    private readonly Slot[] _slots = new Slot[MaxDepthSources];
    private readonly Stack<int> _free = new(MaxDepthSources);
    private readonly Dictionary<StaticLightSources.Position, int> _residents = new(MaxDepthSources);
    private readonly HashSet<StaticLightSources.Position> _preloadSuppressed = new();
    private readonly PlacedLightPendingQueue _pending = new();
    private readonly PlacedLightPolicy.Resident[] _victims = new PlacedLightPolicy.Resident[MaxDepthSources];
    private readonly List<ActiveLight> _active = new(MaxPublishedSources), _readyLights = new(MaxPublishedSources);
    private readonly ActiveLight[] _published = new ActiveLight[MaxPublishedSources];

    private readonly List<ActiveLight> _completedLights = new(MaxDepthSources),
        _publicationCandidates = new(MaxDepthSources);

    private readonly PublicationComparer _publicationComparer;
    private readonly Comparison<ActiveLight> _publicationOrder;
    private static readonly Comparison<ActiveLight> CompletedOrder = CompareCompleted;
    private int _publishedCount = -1;

    private readonly float[] _scratchView = new float[16],
        _scratchProj = new float[16],
        _scratchLightMatrix = new float[16];

    private readonly Vec3d _lightWorld = new();
    private readonly Vec3d _retentionWorld = new();
    private readonly ShadowGlState _savedState = new(capture: false);
    private int _texture, _framebuffer, _sourceGeneration;
    private int _lastSourceRevision = -1, _lastViewRevision = -1, _lastResidencyRevision = -1, _residencyRevision;
    private double _nextSchedulerWake;

    internal int PublicationRevision { get; private set; }

    // Unlike source publication, this revision also changes when a completed
    // refresh replaces depth without changing any resident source record.
    internal int DepthGeneration { get; private set; }

    // Precision is valid only for this exact completed promotion revision.
    // Broader invalidation/skipped revisions require conservative region dirtying.
    internal StaticLightSources.Source LastPromotedSource { get; private set; }
    internal int LastPromotionDepthGeneration { get; private set; } = -1;

    // Every recorded footprint belongs to a complete six-face promotion in
    // this render frame. Consumers validate the contiguous generation range
    // before dirtying just these regions; invalidation is never a promotion.
    private readonly List<StaticLightSources.Source> _promotedSourcesThisFrame = new(VisibleEditPromotionLimit);
    internal IReadOnlyList<StaticLightSources.Source> PromotedSourcesThisFrame => _promotedSourcesThisFrame;
    internal int PromotionBatchStartDepthGeneration { get; private set; }
    internal object? WorldIdentity => _world;

    internal bool UpdatedThisFrame { get; private set; }

    private int _shadowProgram,
        _mvpLocation,
        _originLocation,
        _atlasLocation,
        _excludeLocation,
        _blockMinLocation,
        _terrainPassLocation,
        _playerPosLocation;

    private object? _world;
    private bool _warned, _allocationFailed;
    private double _time, _blendStart = -1;
    private int _stagingSlot = -1;

    private enum StageKind
    {
        None,
        Admission,
        Refresh,
        Replacement
    }

    private StageKind _stageKind;
    private PlacedLightBake _bake;
    internal int Capacity { get; private set; } = MaxSources;
    internal int DepthCapacity => Capacity;
    internal int Admissions { get; private set; }
    internal int OffViewEvictions { get; private set; }
    internal int DistanceEvictions { get; private set; }
    internal int UnusedEvictions { get; private set; }
    internal int RangeEvictions { get; private set; }
    internal int BakeFailures { get; private set; }
    internal int FacesBakedThisFrame { get; private set; }
    internal int PendingVisibleWork { get; private set; }
    internal float OldestVisibleWait { get; private set; }

    internal string StagingOwner => _stageKind switch
    {
        StageKind.Admission => "Admission", StageKind.Refresh => "Refresh",
        StageKind.Replacement => "Replacement", _ => "None"
    };

    internal int QueueDepth => _pending.Count;
    internal IReadOnlyList<ActiveLight> Active => _active;

    internal IReadOnlyList<ActiveLight> ReadyLights => _readyLights;

    // All completed, loaded source records in stable world-coordinate order.
    // No screen/fade fields participate in this compute snapshot's identity.
    internal IReadOnlyList<ActiveLight> CompletedLights => _completedLights;
    internal bool Ready { get; private set; }
    internal int TextureId => _texture;
    internal int CandidateCount { get; private set; }
    internal int ValidCount { get; private set; }

    private sealed class Slot
    {
        internal StaticLightSources.Source Source;
        internal PlacedLightView.Bounds Bounds;
        internal bool Occupied, Valid, Dirty, Immediate, KnownLoaded;
        internal int Pins, ContentGeneration;
        internal int Revision, BakedRevision, Generation, Priority;
        internal double LastHit, CompletedAt, FadeAt, DirtySince, RecheckAt, FailedUntil, ExitAt = -1;
        internal double CameraDistance = double.PositiveInfinity;
        internal double PlayerDistance = double.PositiveInfinity;
        internal float Fade;
    }

    internal StaticTerrainShadowMaps(MovingLightShadowRenderer terrainDraws)
    {
        _terrainDraws = terrainDraws;
        for (int i = 0; i < MaxDepthSources; i++) _slots[i] = new Slot();
        _publicationComparer = new PublicationComparer(_slots);
        _publicationOrder = _publicationComparer.Compare;
        Invalidate();
    }

    internal void ResetShaderLocations()
    {
        // Completed maps can also contain obsolete wind geometry after shader
        // reload. Rebuild every generation before publishing its source records.
        Invalidate();
    }

    internal void RetainQualityDepth()
    {
        // Assets may already contain a newly edited geometry stage before the
        // quality reload's two snapshots. Check the policy actually used to bake.
        if (!_pointProgram.GeometrySourceUnchanged())
        {
            Invalidate();
            return;
        }

        // The quality define changes only receivers, never caster geometry.
        // Retire old program IDs/partial work while retaining completed depth.
        CancelStage();
        _pointProgram.Reload();
        _containerShadows.Reload();
        _shadowProgram = 0;
        _nextSchedulerWake = 0;
    }

    internal void Invalidate()
    {
        _pointProgram.Reload();
        _containerShadows.Reload();
        DepthGeneration++;
        LastPromotionDepthGeneration = -1;
        ResetPromotionBatch();
        Ready = false;
        _allocationFailed = false;
        _bake = default;
        _stageKind = StageKind.None;
        _stagingSlot = -1;
        _blendStart = -1;
        _shadowProgram = 0;
        _active.Clear();
        _readyLights.Clear();
        _completedLights.Clear();
        _publicationCandidates.Clear();
        _residents.Clear();
        _pending.Clear();
        _preloadSuppressed.Clear();
        _free.Clear();
        _lastSourceRevision = _lastViewRevision = _lastResidencyRevision = -1;
        _nextSchedulerWake = 0;
        _residencyRevision++;
        PublicationRevision++;
        _publishedCount = -1;
        for (int i = Capacity - 1; i >= 0; i--)
        {
            _slots[i].Occupied = _slots[i].Valid = _slots[i].Dirty = false;
            _slots[i].KnownLoaded = false;
            _slots[i].Pins = 0;
            _slots[i].Generation++;
            _free.Push(i);
        }

        ValidCount = CandidateCount = 0;
    }

    private bool Ensure(ICoreClientAPI api)
    {
        if (_texture != 0 && _framebuffer != 0) return true;
        if (_allocationFailed) return false;
        int active = GL.GetInteger(GetPName.ActiveTexture);
        int draw = GL.GetInteger(GetPName.DrawFramebufferBinding);
        int read = GL.GetInteger(GetPName.ReadFramebufferBinding);
        GL.ActiveTexture(TextureUnit.Texture0);
        int texture0 = GL.GetInteger(GetPName.TextureBinding2DArray);
        try
        {
            Capacity = PlacedLightPolicy.DepthCapacity(GL.GetInteger(GetPName.MaxArrayTextureLayers), TargetFaceSize);
            if (Capacity == 0) throw new InvalidOperationException("Insufficient static depth array layers.");
            Invalidate();
            api.Logger.Notification(
                $"[DRT AgX] Static depth residency: {Capacity} maps; published light cap {MaxSources}.");
            _texture = GL.GenTexture();
            GL.BindTexture(TextureTarget.Texture2DArray, _texture);
            GL.TexStorage3D(TextureTarget3d.Texture2DArray, 1, SizedInternalFormat.DepthComponent24,
                TargetFaceSize, TargetFaceSize, (Capacity + 1) * 6);
            GL.TexParameter(TextureTarget.Texture2DArray, TextureParameterName.TextureMinFilter,
                (int)TextureMinFilter.Linear);
            GL.TexParameter(TextureTarget.Texture2DArray, TextureParameterName.TextureMagFilter,
                (int)TextureMagFilter.Linear);
            GL.TexParameter(TextureTarget.Texture2DArray, TextureParameterName.TextureWrapS,
                (int)TextureWrapMode.ClampToEdge);
            GL.TexParameter(TextureTarget.Texture2DArray, TextureParameterName.TextureWrapT,
                (int)TextureWrapMode.ClampToEdge);
            GL.TexParameter(TextureTarget.Texture2DArray, TextureParameterName.TextureCompareMode,
                (int)TextureCompareMode.CompareRefToTexture);
            GL.TexParameter(TextureTarget.Texture2DArray, TextureParameterName.TextureCompareFunc,
                (int)DepthFunction.Lequal);
            _framebuffer = GL.GenFramebuffer();
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, _framebuffer);
            GL.FramebufferTextureLayer(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment,
                _texture, 0, Capacity * 6);
            GL.DrawBuffer(DrawBufferMode.None);
            GL.ReadBuffer(ReadBufferMode.None);
            if (GL.CheckFramebufferStatus(FramebufferTarget.Framebuffer) == FramebufferErrorCode.FramebufferComplete)
                return true;
            if (!_warned)
            {
                api.Logger.Warning("[DRT AgX] Static-light shadow atlas is incomplete.");
                _warned = true;
            }

            Dispose();
            _allocationFailed = true;
            return false;
        }
        catch (Exception ex)
        {
            if (!_warned)
            {
                api.Logger.Warning("[DRT AgX] Static-light atlas allocation failed: " + ex.Message);
                _warned = true;
            }

            Dispose();
            _allocationFailed = true;
            return false;
        }
        finally
        {
            GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer, draw);
            GL.BindFramebuffer(FramebufferTarget.ReadFramebuffer, read);
            GL.BindTexture(TextureTarget.Texture2DArray, texture0);
            GL.ActiveTexture((TextureUnit)active);
        }
    }

    private void PromoteStaging(int slot)
    {
        // Array-layer copy changes no binding and cannot expose partial faces.
        GL.CopyImageSubData(_texture, ImageTarget.Texture2DArray, 0, 0, 0, Capacity * 6,
            _texture, ImageTarget.Texture2DArray, 0, 0, 0, slot * 6, TargetFaceSize, TargetFaceSize, 6);
        // Publish only complete resident depth. Partial staging faces never
        // invalidate a surface-light memo or become its sampled geometry.
        DepthGeneration++;
        _slots[slot].ContentGeneration++;
        LastPromotionDepthGeneration = -1;
        if (_bake.Active && _bake.Complete)
        {
            LastPromotedSource = _bake.Source;
            LastPromotionDepthGeneration = DepthGeneration;
            // Capacity is fixed. A future caller exceeding the render-frame
            // budget withdraws precise batch evidence rather than allocating.
            if (_promotedSourcesThisFrame.Count == VisibleEditPromotionLimit)
                ResetPromotionBatch();
            else
                _promotedSourcesThisFrame.Add(_bake.Source);
        }
    }

    private void ResetPromotionBatch()
    {
        _promotedSourcesThisFrame.Clear();
        PromotionBatchStartDepthGeneration = DepthGeneration;
    }

    private bool RenderSource(ICoreClientAPI api, ActiveLight active)
    {
        if (api.World is not ClientMain world || NativeShadowFields.Renderer(world) is not { } renderer ||
            NativeShadowFields.Passes(renderer) is not { } passes || passes.Length <= 5 ||
            NativeShadowFields.Atlases(renderer) is not { } atlases ||
            api.Render.GetEngineShader(EnumShaderProgram.Chunkshadowmap) is not { } shader || shader.Disposed ||
            shader.LoadError)
            return false;
        _savedState.Capture();
        using var state = _savedState;
        IShaderProgram? previous = api.Render.CurrentActiveShader;
        // Compile/use the explicit point-only primitive gate inside the saved
        // native state. Sun shadow ownership and its shader remain unchanged.
        var pointShader = _pointProgram.Get(api, shader);
        if (pointShader == null) return false;
        shader = pointShader;
        if (_shadowProgram != shader.ProgramId)
        {
            _shadowProgram = shader.ProgramId;
            _mvpLocation = GL.GetUniformLocation(_shadowProgram, "mvpMatrix");
            _originLocation = GL.GetUniformLocation(_shadowProgram, "origin");
            _atlasLocation = GL.GetUniformLocation(_shadowProgram, "tex2d");
            _excludeLocation = GL.GetUniformLocation(_shadowProgram, "drtExcludeEmitter");
            _blockMinLocation = GL.GetUniformLocation(_shadowProgram, "drtEmitterBlockMin");
            _terrainPassLocation = GL.GetUniformLocation(_shadowProgram, "drtShadowTerrainPass");
            _playerPosLocation = GL.GetUniformLocation(_shadowProgram, "playerpos");
            _excludeWindLocation = GL.GetUniformLocation(_shadowProgram, "drtExcludeWindCasters");
        }

        int mvp = _mvpLocation,
            origin = _originLocation,
            atlas = _atlasLocation,
            exclude = _excludeLocation,
            blockMin = _blockMinLocation;
        if (mvp < 0 || origin < 0 || atlas < 0) return false;
        // This point program belongs exclusively to this cache owner. Every
        // consumed projection/emitter/pass value is assigned below, so reading
        // and restoring its private uniforms only adds driver round trips.
        // The native program and shared GL state remain protected by the guard.
        StaticLightSources.Source source = active.Source;
        _lightWorld.Set(source.X + 0.5, source.Y + SourceHeight, source.Z + 0.5);
        Vec3d worldPosition = _lightWorld;
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, _framebuffer);
        GL.Enable(EnableCap.DepthTest);
        GL.DepthFunc(DepthFunction.Less);
        GL.DepthMask(true);
        // Less-depth staging requires an empty far plane, regardless of an
        // earlier renderer's clear value. ShadowGlState restores that value.
        GL.ClearDepth(1.0);
        GL.Disable(EnableCap.Blend);
        // A preceding renderer's polygon offset must not alter the cached
        // depth of thin, two-sided furniture and chiseled surfaces.
        GL.Disable(EnableCap.PolygonOffsetFill);
        // Two-sided casting prevents edge gaps in chiseled and thin terrain.
        GL.Disable(EnableCap.CullFace);
        GL.Disable(EnableCap.ScissorTest);
        GL.Viewport(0, 0, TargetFaceSize, TargetFaceSize);
        GL.ColorMask(false, false, false, false);
        try
        {
            // Native containers remain in one visible renderer. Their opaque
            // base vertices supply a separate closed/rest depth caster, shared
            // across all six faces and unchanged when the lid animates.
            if (!_containerShadows.Prepare(api, worldPosition, worldPosition, Range, null, excludeEmitter: true))
                return false;
            shader.Use();
            if (_excludeWindLocation >= 0) GL.Uniform1(_excludeWindLocation, 1);
            GL.Uniform1(atlas, 0);
            // These pools are emitter-relative. Keep the native position
            // reference consistent even though static wind/wave casters use
            // their rest pose; native sun and moving maps retain live animation.
            if (_playerPosLocation >= 0)
                GL.Uniform3(_playerPosLocation, (float)worldPosition.X, (float)worldPosition.Y, (float)worldPosition.Z);
            // The vertex shader supplies light-relative positions. Only the
            // emitter's own block is omitted; adjacent faces keep their depth.
            if (exclude >= 0 && blockMin >= 0)
            {
                GL.Uniform3(blockMin, (float)(source.X - worldPosition.X),
                    (float)(source.Y - worldPosition.Y), (float)(source.Z - worldPosition.Z));
                GL.Uniform1(exclude, 1);
            }

            // Initialize the owned selector before submitting per-pass values.
            if (_terrainPassLocation >= 0) GL.Uniform1(_terrainPassLocation, -1);
            // Every resident terrain pass participates; animated entities remain
            // outside these native chunk pools and wind exclusion stays scoped.
            _terrainDraws.Casters.BeginAllTerrainPasses(passes, atlases, _terrainPassLocation);
            _terrainDraws.Casters.Prepare(worldPosition, worldPosition, Range, null);
            int endFace = Math.Min(6, _bake.NextFace + _bake.Budget);
            for (int face = _bake.NextFace; face < endFace; face++)
            {
                // Staging is the sole write target; all sampled resident layers remain intact.
                GL.FramebufferTextureLayer(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment,
                    _texture, 0, Capacity * 6 + face);
                GL.Clear(ClearBufferMask.DepthBufferBit);
                MovingLightShadowRenderer.MakeLightMatrix(LightOrigin,
                    MovingLightShadowRenderer.Directions[face], Range, _scratchView, _scratchProj, _scratchLightMatrix);
                GL.UniformMatrix4(mvp, 1, false, _scratchLightMatrix);
                _terrainDraws.Casters.Draw(face);
                _containerShadows.Draw(api, shader, -1, face, _scratchLightMatrix);
                FacesBakedThisFrame++;
            }

            return true;
        }
        catch (Exception ex)
        {
            if (!_warned)
            {
                api.Logger.Warning("[DRT AgX] Static-light shadow build failed: " + ex.Message);
                _warned = true;
            }

            return false;
        }
        finally
        {
            try
            {
                _terrainDraws.Casters.End();
                _containerShadows.End();
            }
            finally
            {
                // A failed Use can leave another GL program active. Managed
                // cleanup restores the native tracker, then the outer state
                // guard restores the exact incoming GL program and bindings.
                try
                {
                    shader.Stop();
                }
                finally
                {
                    previous?.Use();
                }
            }
        }
    }

    public void Dispose()
    {
        _pointProgram.Dispose();
        _containerShadows.Dispose();

        if (_framebuffer != 0) GL.DeleteFramebuffer(_framebuffer);
        if (_texture != 0) GL.DeleteTexture(_texture);
        _framebuffer = _texture = 0;
        Ready = false;
        CancelStage();
        foreach (Slot slot in _slots)
            if (slot != null)
            {
                slot.Valid = false;
                slot.Dirty = false;
            }
    }
}