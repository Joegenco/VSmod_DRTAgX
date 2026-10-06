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
    internal const int FullResolutionFaceSize = 192;
    // Requested static size: 2 gives 96px; 3 restores 64px, 4 gives 48px.
    // Allocation, bake viewport and layer copies all use FaceSize; GLSL reads
    // textureSize for its filter footprint. Rebuild/restart to replace the cache.
    internal const int ResolutionDivisor = 2;
    internal const int FaceSize = FullResolutionFaceSize / ResolutionDivisor;
    // Each quality owner retains its complete cache; toggling does not repeatedly discard depth.
    internal int TargetFaceSize { get; init; } = FaceSize;
    internal const float Range = 22f;
    internal const double SourceHeight = 0.5;
    internal readonly record struct ActiveLight(StaticLightSources.Source Source, int Slot, float Fade,
        float ShadowBlend, int SecondarySlot = -1, bool ReplacementPair = false)
    {
        internal PlacedLightView.Bounds Bounds { get; init; }
    }

    private static readonly float[] LightOrigin = { 0f, 0f, 0f };
    private readonly MovingLightShadowRenderer _terrainDraws;
    private readonly Slot[] _slots = new Slot[MaxSources];
    private readonly Stack<int> _free = new(MaxSources);
    private readonly Dictionary<StaticLightSources.Position, int> _residents = new(MaxSources);
    private readonly HashSet<StaticLightSources.Position> _preloadSuppressed = new();
    private readonly PlacedLightPendingQueue _pending = new();
    private readonly PlacedLightPolicy.Resident[] _victims = new PlacedLightPolicy.Resident[MaxSources];
    private readonly List<ActiveLight> _active = new(MaxPublishedSources), _readyLights = new(MaxPublishedSources);
    private readonly ActiveLight[] _published = new ActiveLight[MaxPublishedSources];
    private int _publishedCount = -1;
    private readonly float[] _oldMvp = new float[16], _oldOrigin = new float[3], _oldBlockMin = new float[3], _oldPlayerPos = new float[3];
    private readonly float[] _scratchView = new float[16], _scratchProj = new float[16], _scratchLightMatrix = new float[16];
    private readonly Vec3d _lightWorld = new();
    private readonly Vec3d _retentionWorld = new();
    private readonly ShadowGlState _savedState = new(capture: false);
    private readonly int[] _gpuStartQueries = new int[2], _gpuEndQueries = new int[2];
    private readonly bool[] _gpuPending = new bool[2];
    private int _nextGpuQuery, _texture, _framebuffer, _sourceGeneration;
    private int _lastSourceRevision = -1, _lastViewRevision = -1, _lastResidencyRevision = -1, _residencyRevision;
    private double _nextSchedulerWake;
    internal int PublicationRevision { get; private set; }
    internal bool ViewCullingEnabled
    {
        get => _pending.ViewCullingEnabled;
        set
        {
            if (_pending.ViewCullingEnabled == value) return;
            _pending.ViewCullingEnabled = value;
            // Reclassify residents without clearing completed depth or partial bakes.
            _lastViewRevision = -1;
            _nextSchedulerWake = 0;
        }
    }
    internal bool UpdatedThisFrame { get; private set; }
    private bool _allTerrainPassesEnabled;
    internal bool AllTerrainPassesEnabled
    {
        get => _allTerrainPassesEnabled;
        set
        {
            if (_allTerrainPassesEnabled == value) return;
            _allTerrainPassesEnabled = value;
            // Render-thread setting change: cancel staged faces and retire old
            // maps so the two caster sets cannot be mixed or reused on toggles.
            Invalidate();
        }
    }
    private int _shadowProgram, _mvpLocation, _originLocation, _atlasLocation, _excludeLocation, _blockMinLocation, _terrainPassLocation, _playerPosLocation;
    private object? _world;
    private bool _warned, _allocationFailed;
    private double _time, _blendStart = -1;
    private int _stagingSlot = -1;
    private enum StageKind { None, Admission, Refresh, Replacement }
    private StageKind _stageKind;
    private PlacedLightBake _bake;
    internal int Capacity { get; private set; } = MaxSources;
    internal int Admissions { get; private set; }
    internal int OffViewEvictions { get; private set; }
    internal int DistanceEvictions { get; private set; }
    internal int UnusedEvictions { get; private set; }
    internal int RangeEvictions { get; private set; }
    internal int BakeFailures { get; private set; }
    internal int FacesBakedThisFrame { get; private set; }
    internal double LastBakeCpuMilliseconds { get; private set; }
    internal double LastBakeGpuMilliseconds { get; private set; }
    internal double LastSchedulerCpuMilliseconds { get; private set; }
    internal double LastTotalCpuMilliseconds { get; private set; }
    internal int PendingVisibleWork { get; private set; }
    internal float OldestVisibleWait { get; private set; }
    internal string StagingOwner => _stageKind switch { StageKind.Admission => "Admission", StageKind.Refresh => "Refresh",
        StageKind.Replacement => "Replacement", _ => "None" };
    internal int QueueDepth => _pending.Count;
    internal IReadOnlyList<ActiveLight> Active => _active;
    internal IReadOnlyList<ActiveLight> ReadyLights => _readyLights;
    internal bool Ready { get; private set; }
    internal int TextureId => _texture;
    internal int CandidateCount { get; private set; }
    internal int ValidCount { get; private set; }

    private sealed class Slot
    {
        internal StaticLightSources.Source Source;
        internal PlacedLightView.Bounds Bounds;
        internal bool Occupied, Valid, Dirty, Immediate;
        internal int Revision, BakedRevision, Generation, Priority;
        internal double LastHit, CompletedAt, FadeAt, DirtySince, RecheckAt, FailedUntil, ExitAt = -1;
        internal double CameraDistance = double.PositiveInfinity;
        internal double PlayerDistance = double.PositiveInfinity;
        internal float Fade;
    }

    internal StaticTerrainShadowMaps(MovingLightShadowRenderer terrainDraws)
    {
        _terrainDraws = terrainDraws;
        for (int i = 0; i < MaxSources; i++) _slots[i] = new Slot();
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
        // The quality define changes only receivers, never caster geometry.
        // Retire old program IDs/partial work while retaining completed depth.
        CancelStage();
        _shadowProgram = 0;
        _nextSchedulerWake = 0;
    }

    internal void Invalidate()
    {
        Ready = false;
        _allocationFailed = false;
        _bake = default;
        _stageKind = StageKind.None;
        _stagingSlot = -1;
        _blendStart = -1;
        _shadowProgram = 0;
        _active.Clear();
        _readyLights.Clear();
        _residents.Clear();
        _pending.Clear();
        _preloadSuppressed.Clear();
        _free.Clear();
        _lastSourceRevision = _lastViewRevision = _lastResidencyRevision = -1;
        _nextSchedulerWake = 0; _residencyRevision++; PublicationRevision++;
        _publishedCount = -1;
        for (int i = Capacity - 1; i >= 0; i--)
        {
            _slots[i].Occupied = _slots[i].Valid = _slots[i].Dirty = false;
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
            Capacity = PlacedLightPolicy.Capacity(GL.GetInteger(GetPName.MaxArrayTextureLayers));
            if (Capacity == 0) throw new InvalidOperationException("Insufficient static depth array layers.");
            Invalidate();
            if (Capacity < MaxSources) api.Logger.Warning($"[DRT AgX] Static shadows limited to {Capacity} resident maps by array layer capacity.");
            _texture = GL.GenTexture();
            GL.BindTexture(TextureTarget.Texture2DArray, _texture);
            GL.TexStorage3D(TextureTarget3d.Texture2DArray, 1, SizedInternalFormat.DepthComponent24,
                TargetFaceSize, TargetFaceSize, (Capacity + 1) * 6);
            GL.TexParameter(TextureTarget.Texture2DArray, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
            GL.TexParameter(TextureTarget.Texture2DArray, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
            GL.TexParameter(TextureTarget.Texture2DArray, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
            GL.TexParameter(TextureTarget.Texture2DArray, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
            GL.TexParameter(TextureTarget.Texture2DArray, TextureParameterName.TextureCompareMode, (int)TextureCompareMode.CompareRefToTexture);
            GL.TexParameter(TextureTarget.Texture2DArray, TextureParameterName.TextureCompareFunc, (int)DepthFunction.Lequal);
            _framebuffer = GL.GenFramebuffer();
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, _framebuffer);
            GL.FramebufferTextureLayer(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment,
                _texture, 0, Capacity * 6);
            GL.DrawBuffer(DrawBufferMode.None);
            GL.ReadBuffer(ReadBufferMode.None);
            if (GL.CheckFramebufferStatus(FramebufferTarget.Framebuffer) == FramebufferErrorCode.FramebufferComplete)
                return true;
            if (!_warned) { api.Logger.Warning("[DRT AgX] Static-light shadow atlas is incomplete."); _warned = true; }
            Dispose();
            _allocationFailed = true;
            return false;
        }
        catch (Exception ex)
        {
            if (!_warned) { api.Logger.Warning("[DRT AgX] Static-light atlas allocation failed: " + ex.Message); _warned = true; }
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
    }

    private bool RenderSource(ICoreClientAPI api, ActiveLight active)
    {
        long startTicks = Stopwatch.GetTimestamp();
        if (api.World is not ClientMain world || NativeShadowFields.Renderer(world) is not { } renderer ||
            NativeShadowFields.Passes(renderer) is not { } passes || passes.Length <= 5 ||
            NativeShadowFields.Atlases(renderer) is not { } atlases ||
            api.Render.GetEngineShader(EnumShaderProgram.Chunkshadowmap) is not { } shader || shader.Disposed || shader.LoadError)
            return false;
        _savedState.Capture();
        using var state = _savedState;
        IShaderProgram? previous = api.Render.CurrentActiveShader;
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
        }
        int mvp = _mvpLocation, origin = _originLocation, atlas = _atlasLocation,
            exclude = _excludeLocation, blockMin = _blockMinLocation;
        if (mvp < 0 || origin < 0 || atlas < 0) return false;
        int oldExclude = 0, oldTerrainPass = -1;
        GL.GetUniform(shader.ProgramId, mvp, _oldMvp);
        GL.GetUniform(shader.ProgramId, origin, _oldOrigin);
        GL.GetUniform(shader.ProgramId, atlas, out int oldAtlas);
        if (exclude >= 0) GL.GetUniform(shader.ProgramId, exclude, out oldExclude);
        if (blockMin >= 0) GL.GetUniform(shader.ProgramId, blockMin, _oldBlockMin);
        if (_terrainPassLocation >= 0) GL.GetUniform(shader.ProgramId, _terrainPassLocation, out oldTerrainPass);
        if (_playerPosLocation >= 0) GL.GetUniform(shader.ProgramId, _playerPosLocation, _oldPlayerPos);
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
        int gpuQuery = BeginGpuTimer();
        try
        {
            shader.Use();
            GL.Uniform1(atlas, 0);
            // These pools are emitter-relative. Keep the native position
            // reference consistent even though static wind/wave casters use
            // their rest pose; native sun and moving maps retain live animation.
            if (AllTerrainPassesEnabled && _playerPosLocation >= 0)
                GL.Uniform3(_playerPosLocation, (float)worldPosition.X, (float)worldPosition.Y, (float)worldPosition.Z);
            // The vertex shader supplies light-relative positions. Only the
            // emitter's own block is omitted; adjacent faces keep their depth.
            if (exclude >= 0 && blockMin >= 0)
            {
                GL.Uniform3(blockMin, (float)(source.X - worldPosition.X),
                    (float)(source.Y - worldPosition.Y), (float)(source.Z - worldPosition.Z));
                GL.Uniform1(exclude, 1);
            }
            // The shared native program retains its normal behavior outside
            // this bake; restore the pass selector with its other uniforms.
            if (_terrainPassLocation >= 0) GL.Uniform1(_terrainPassLocation, -1);
            if (AllTerrainPassesEnabled)
                _terrainDraws.Casters.BeginAllTerrainPasses(passes, atlases, _terrainPassLocation);
            else
                _terrainDraws.Casters.Begin(passes, atlases);
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
                FacesBakedThisFrame++;
            }
            return true;
        }
        catch (Exception ex)
        {
            if (!_warned) { api.Logger.Warning("[DRT AgX] Static-light shadow build failed: " + ex.Message); _warned = true; }
            return false;
        }
        finally
        {
            try
            {
                _terrainDraws.Casters.End();
                if (gpuQuery >= 0) GL.QueryCounter(_gpuEndQueries[gpuQuery], QueryCounterTarget.Timestamp);
                LastBakeCpuMilliseconds = Stopwatch.GetElapsedTime(startTicks).TotalMilliseconds;
            }
            finally
            {
                try
                {
                    // A partially failed Use may leave another program bound.
                    // Restore uniforms only on their owner; the outer guard
                    // restores the exact entry GL program after managed cleanup.
                    GL.UseProgram(shader.ProgramId);
                    GL.UniformMatrix4(mvp, 1, false, _oldMvp);
                    GL.Uniform3(origin, _oldOrigin[0], _oldOrigin[1], _oldOrigin[2]);
                    GL.Uniform1(atlas, oldAtlas);
                    if (exclude >= 0) GL.Uniform1(exclude, oldExclude);
                    if (blockMin >= 0) GL.Uniform3(blockMin, _oldBlockMin[0], _oldBlockMin[1], _oldBlockMin[2]);
                    if (_terrainPassLocation >= 0) GL.Uniform1(_terrainPassLocation, oldTerrainPass);
                    if (_playerPosLocation >= 0) GL.Uniform3(_playerPosLocation, _oldPlayerPos[0], _oldPlayerPos[1], _oldPlayerPos[2]);
                }
                finally
                {
                    try { shader.Stop(); }
                    finally { previous?.Use(); }
                }
            }
        }
    }

    private int BeginGpuTimer()
    {
        if (!PlacedLightGpuProfile.Enabled) return -1;
        int index = _nextGpuQuery;
        if (_gpuPending[index]) return -1; // Never wait for a GPU result in the render loop.
        if (_gpuStartQueries[index] == 0)
        {
            _gpuStartQueries[index] = GL.GenQuery();
            _gpuEndQueries[index] = GL.GenQuery();
        }
        GL.QueryCounter(_gpuStartQueries[index], QueryCounterTarget.Timestamp);
        _gpuPending[index] = true;
        _nextGpuQuery = (index + 1) & 1;
        return index;
    }

    private void PollGpuTimers()
    {
        for (int i = 0; i < _gpuPending.Length; i++)
        {
            if (!_gpuPending[i]) continue;
            GL.GetQueryObject(_gpuEndQueries[i], GetQueryObjectParam.QueryResultAvailable, out int available);
            if (available == 0) continue;
            GL.GetQueryObject(_gpuStartQueries[i], GetQueryObjectParam.QueryResult, out long start);
            GL.GetQueryObject(_gpuEndQueries[i], GetQueryObjectParam.QueryResult, out long end);
            LastBakeGpuMilliseconds = Math.Max(0, end - start) / 1_000_000.0;
            _gpuPending[i] = false;
        }
    }

    public void Dispose()
    {
        for (int i = 0; i < _gpuStartQueries.Length; i++)
        {
            if (_gpuStartQueries[i] != 0) GL.DeleteQuery(_gpuStartQueries[i]);
            if (_gpuEndQueries[i] != 0) GL.DeleteQuery(_gpuEndQueries[i]);
            _gpuStartQueries[i] = _gpuEndQueries[i] = 0;
            _gpuPending[i] = false;
        }
        if (_framebuffer != 0) GL.DeleteFramebuffer(_framebuffer);
        if (_texture != 0) GL.DeleteTexture(_texture);
        _framebuffer = _texture = 0;
        Ready = false;
        CancelStage();
        foreach (Slot slot in _slots) if (slot != null) { slot.Valid = false; slot.Dirty = false; }
    }
}
