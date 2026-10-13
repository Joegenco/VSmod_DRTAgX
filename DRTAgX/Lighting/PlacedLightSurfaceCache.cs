using System;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using Vintagestory.API.Client;

namespace DRTAgX;

/// <summary>Render-thread GPU surface memo; completed world lighting survives camera motion.</summary>
internal sealed partial class PlacedLightSurfaceCache : IDisposable
{
    // Each four-way page owns an exact native plane and finite quad bounds.
    // An 80-byte header precedes two complete 17x17 banks of 32-byte nodes.
    // 16K pages cost ~290 MiB including the previous complete lighting field.
    internal const int Capacity = 1 << 12, RecordBytes = 18576, HeaderBytes = 16, FillLimit = 4;
    internal const int BufferBytes = HeaderBytes + Capacity * RecordBytes;
    internal const int TargetEntryCapacity = 1 << 14;
    internal const int SourcePrefixBytes = 1024 * 64, RegionCount = 1 << 16;
    internal const int SourceBufferBytes = SourcePrefixBytes + RegionCount * sizeof(uint) * 2, TransitionFrames = 12;
    private const uint GenerationMask = 0x3fffffffu;
    private readonly uint[] _zero = new uint[1];
    private object? _world;
    private bool _anchorValid, _failed, _invalidate = true, _needsReconciliation = true;
    private bool _visibleRefreshPending;
    private double _anchorX, _anchorY, _anchorZ;
    private StaticTerrainShadowMaps? _maps;
    private int _depthGeneration = -1, _calibrationGeneration = -1;
    private float _saturation = float.NaN;
    private readonly PlacedLightSurfaceWorker _worker = new();
    internal int PageSetMask => RuntimeEntryCapacity / 4 - 1;
    internal static PlacedLightSurfaceCache? Current { get; private set; }
    internal int Buffer { get; private set; }
    internal int RuntimeEntryCapacity { get; private set; } = Capacity;
    internal int BufferSizeBytes => HeaderBytes + RuntimeEntryCapacity * RecordBytes;
    internal long MaxShaderStorageBlockBytes { get; private set; }
    internal int SourceBuffer { get; private set; }
    internal int SourceCount { get; private set; }
    internal bool Active { get; private set; }
    internal bool Ready { get; private set; }
    internal bool Failed => _failed;
    internal uint Generation { get; private set; } = 1;
    internal uint Frame { get; private set; }
    internal int EpochChanges { get; private set; }

    internal int SourceUploads { get; private set; }

    // Queue prerequisites retry on coherent lighting/depth publication only.
    internal uint EvaluationRevision { get; private set; } = 1;

    // Actual regional edits need immediate screen coverage. Ordinary demand
    // readbacks may change EvaluationRevision without changing any light field.
    internal uint VisibleRefreshRevision { get; private set; } = 1;
    internal int HeaderResets { get; private set; }
    internal int AnchorCellX { get; private set; }
    internal int AnchorCellY { get; private set; }
    internal int AnchorCellZ { get; private set; }
    internal Vector3 BoundsMinimum { get; private set; }
    internal Vector3 BoundsMaximum { get; private set; }
    internal int ShadowTexture => _maps?.TextureId ?? 0;
    internal bool Diagnostics => false; // Shipping draws never enable probe counters.

    internal PlacedLightSurfaceCache() => Current = this;

    // Before terrain .37: publish prior completed geometry and reset the GPU
    // fill budget once. GPU clears preserve command ordering without readback.
    internal void BeforeFrame(ICoreClientAPI api, StaticTerrainShadowMaps maps, AgxConfig config, int calibration)
    {

        Frame = (Frame + 1) & GenerationMask;
        if (Frame == 0) Frame = 1;
        Publish(api, maps, config, calibration, afterScheduler: false);
        // A resumed/suspended owner waits for .38 source/map reconciliation.
        // Existing storage still gets a fresh budget for any later .38 fills;
        // initial storage allocation clears its header when first published.
        if (Buffer == 0 || _failed || !Active) return;
        int previous = GL.GetInteger(GetPName.ShaderStorageBufferBinding);
        try
        {
            GL.MemoryBarrier(MemoryBarrierFlags.ShaderStorageBarrierBit | MemoryBarrierFlags.BufferUpdateBarrierBit);
            GL.BindBuffer(BufferTarget.ShaderStorageBuffer, Buffer);
            GL.ClearBufferSubData(BufferTarget.ShaderStorageBuffer, PixelInternalFormat.R32ui,
                IntPtr.Zero, (IntPtr)HeaderBytes, PixelFormat.RedInteger, PixelType.UnsignedInt, _zero);
            HeaderResets++;
        }
        finally
        {
            GL.BindBuffer(BufferTarget.ShaderStorageBuffer, previous);
        }
    }

    // Also called at .38 after completed depth promotion. Camera/view and fades
    // never invalidate lighting. Semantic edits stamp only affected world regions.
    internal void Publish(ICoreClientAPI api, StaticTerrainShadowMaps maps, AgxConfig config, int calibration)
        => Publish(api, maps, config, calibration, afterScheduler: true);

    private void Publish(ICoreClientAPI api, StaticTerrainShadowMaps maps, AgxConfig config, int calibration,
        bool afterScheduler)
    {
        Active = FrameQuality.Current.PlacedLights(config.StaticLightShadows);
        Ready = false;
        bool disabled = !Active;
        if (disabled)
        {
            _needsReconciliation = true;
            return;
        }

        if (!afterScheduler && _needsReconciliation) return;
        // Before precedes the map scheduler. Never fill a new world's memo
        // from the prior world's completed atlas during that first boundary.
        if (_failed || api.World?.Player?.Entity == null || !ReferenceEquals(maps.WorldIdentity, api.World)) return;
        try
        {
            var camera = api.World.Player.Entity.CameraPos;
            bool newWorld = !ReferenceEquals(_world, api.World);
            if (newWorld || !_anchorValid || Math.Abs(camera.X - _anchorX) > 4096 ||
                Math.Abs(camera.Y - _anchorY) > 4096 || Math.Abs(camera.Z - _anchorZ) > 4096)
            {
                _world = api.World;
                SetAnchor(camera.X, camera.Y, camera.Z);
                _invalidate = true;
            }

            Ensure();
            bool mapChanged = !ReferenceEquals(_maps, maps), sourcesChanged = SourcesChanged(maps);
            bool depthChanged = _depthGeneration != maps.DepthGeneration;
            // The 1/32 grid changes lookup presentation weights only. Reuse the
            // same plane nodes/payloads when toggled; do not force a field refresh.
            bool appearanceChanged = _calibrationGeneration != calibration || _saturation != config.PlsSaturation;
            bool changed = _invalidate || mapChanged || sourcesChanged || depthChanged || appearanceChanged ||
                           _regionsChanged;
            if (changed)
            {
                // Epochs distinguish world ownership, not ordinary completed lamp
                // updates. A wrapped regional stamp also requires a hard clear.
                bool hardReset = _invalidate || RegionStamp == uint.MaxValue;
                var visibleRefresh = _visibleRefreshPending || hardReset || mapChanged || appearanceChanged;
                if (hardReset) NextGeneration();
                BeginRegionEvent();
                if (hardReset) MarkCurrentInfluence(maps);
                else if (mapChanged || appearanceChanged) MarkAllInfluence(maps);
                else
                {
                    // Physical residency and view publication are not semantic
                    // changes. Source-content events stamp old/new footprints.
                    if (depthChanged)
                    {
                        var promoted = maps.PromotedSourcesThisFrame;
                        if (promoted.Count > 0 && _depthGeneration == maps.PromotionBatchStartDepthGeneration &&
                            unchecked(maps.DepthGeneration - maps.PromotionBatchStartDepthGeneration) == promoted.Count)
                        {
                            // Several visible dirty cubes may complete in one
                            // bounded edit burst. Stamp their exact footprints
                            // instead of refreshing every retained lamp region.
                            for (var i = 0; i < promoted.Count; ++i)
                            {
                                MarkInfluence(promoted[i]);
                                visibleRefresh |= _sourceIndex?.IsDemanded(promoted[i]) ?? true;
                            }
                        }
                        else if (maps.DepthGeneration == unchecked(_depthGeneration + 1) &&
                                 maps.LastPromotionDepthGeneration == maps.DepthGeneration)
                        {
                            MarkInfluence(maps.LastPromotedSource);
                            visibleRefresh |= _sourceIndex?.IsDemanded(maps.LastPromotedSource) ?? true;
                        }
                        else
                        {
                            // A skipped promotion/invalidate has no precise footprint.
                            MarkAllInfluence(maps);
                            visibleRefresh |= SourceCount > 0;
                        }
                    }
                }

                if (hardReset || mapChanged || sourcesChanged || _saturation != config.PlsSaturation)
                    UploadSources(maps, config.PlsSaturation);
                UploadRegions();
                // View-driven membership stamps preserve truthful footprints,
                // but only semantic source/depth edits need a full-screen burst.
                if ((visibleRefresh || _visibleRefreshPending) && ++VisibleRefreshRevision == 0) VisibleRefreshRevision = 1;
                _visibleRefreshPending = false;
                _maps = maps;
                _depthGeneration = maps.DepthGeneration;
                _calibrationGeneration = calibration;
                _saturation = config.PlsSaturation;
                _invalidate = false;
                if (++EvaluationRevision == 0) EvaluationRevision = 1;
            }

            // Completed history remains readable while maps refresh, including
            // removing the last lamp. An initially empty field never fills entries.
            Ready = Buffer != 0 && SourceBuffer != 0 && maps.TextureId != 0 && !SourceBudgetExceeded;
            // Only .38 knows whether disabled-period removals/edits were applied.
            // Old complete depth may remain during its existing staged refresh,
            // whose promotion stamps the affected region only after completion.
            if (afterScheduler) _needsReconciliation = false;
        }
        catch (Exception ex)
        {
            _failed = true;
            Ready = false;
            api.Logger.Warning("[DRT AgX] Placed surface cache unavailable; retaining native voxel lighting: {0}",
                ex.Message);
        }
    }

    private void SetAnchor(double x, double y, double z)
    {
        _anchorX = Math.Floor(x / 32) * 32;
        _anchorY = Math.Floor(y / 32) * 32;
        _anchorZ = Math.Floor(z / 32) * 32;
        // Exact full-world key cells are 1/16 block. A very distant teleport
        // explicitly reanchors/invalidate; ordinary motion never changes keys.
        AnchorCellX = checked((int)(_anchorX * 16));
        AnchorCellY = checked((int)(_anchorY * 16));
        AnchorCellZ = checked((int)(_anchorZ * 16));
        _anchorValid = true;
    }

    internal Vector3 CameraOffset(double x, double y, double z) =>
        new((float)(x - _anchorX), (float)(y - _anchorY), (float)(z - _anchorZ));

    private void Ensure()
    {
        if (Buffer != 0 && SourceBuffer != 0) return;
        int previous = GL.GetInteger(GetPName.ShaderStorageBufferBinding);
        try
        {
            if (Buffer != 0) GL.DeleteBuffer(Buffer);
            if (SourceBuffer != 0) GL.DeleteBuffer(SourceBuffer);
            Buffer = SourceBuffer = 0;
            // Pick once when storage is created. Camera motion never resizes or
            // clears the table; all sizes keep the same record/key/bank ABI.
            // Bundled OpenTK omits this token from GetPName. Khronos registers
            // GL_MAX_SHADER_STORAGE_BLOCK_SIZE as 0x90DE (OpenGL-Registry/xml/gl.xml).
            GL.GetInteger64((GetPName)0x90DE, out long maximum);
            MaxShaderStorageBlockBytes = maximum;
            int selected = ChooseRuntimeEntryCapacity(maximum);
            if (selected == 0)
                throw new InvalidOperationException("Surface cache exceeds the shader-storage block limit.");
            for (int entries = selected; entries >= Capacity; entries >>= 1)
            {
                RuntimeEntryCapacity = entries;
                Buffer = GL.GenBuffer();
                GL.BindBuffer(BufferTarget.ShaderStorageBuffer, Buffer);
                GL.BufferData(BufferTarget.ShaderStorageBuffer, BufferSizeBytes, IntPtr.Zero,
                    BufferUsageHint.DynamicDraw);
                GL.GetBufferParameter(BufferTarget.ShaderStorageBuffer, BufferParameterName.BufferSize,
                    out int allocated);
                if (allocated == BufferSizeBytes) break;
                // The driver limit is not a promise of free VRAM. Retry only
                // the two smaller fixed tables, before any lighting is published.
                GL.DeleteBuffer(Buffer);
                Buffer = 0;
            }

            if (Buffer == 0) throw new InvalidOperationException("Surface cache buffer allocation failed.");
            GL.ClearBufferData(BufferTarget.ShaderStorageBuffer, PixelInternalFormat.R32ui, PixelFormat.RedInteger,
                PixelType.UnsignedInt, _zero);
            SourceBuffer = GL.GenBuffer();
            GL.BindBuffer(BufferTarget.ShaderStorageBuffer, SourceBuffer);
            // Source7 has a fixed four-vec4 source prefix followed by uint region
            // stamps. Event subdata uploads preserve this ABI and never reallocate.
            GL.BufferData(BufferTarget.ShaderStorageBuffer, SourceBufferBytes, IntPtr.Zero,
                BufferUsageHint.DynamicDraw);
            GL.GetBufferParameter(BufferTarget.ShaderStorageBuffer, BufferParameterName.BufferSize, out int size);
            if (size != SourceBufferBytes)
                throw new InvalidOperationException("Surface source buffer allocation failed.");
            GL.ClearBufferData(BufferTarget.ShaderStorageBuffer, PixelInternalFormat.R32ui, PixelFormat.RedInteger,
                PixelType.UnsignedInt, _zero);
        }
        catch
        {
            // A named buffer is not proof of successful storage allocation.
            // Reload must retry the complete pair rather than accept a partial one.
            if (Buffer != 0) GL.DeleteBuffer(Buffer);
            if (SourceBuffer != 0) GL.DeleteBuffer(SourceBuffer);
            Buffer = SourceBuffer = 0;
            throw;
        }
        finally
        {
            GL.BindBuffer(BufferTarget.ShaderStorageBuffer, previous);
        }
    }

    internal static int ChooseRuntimeEntryCapacity(long maximumBytes)
    {
        // A power-of-two table preserves the shader's mask-based hash lookup.
        // The header counts toward the actual bound SSBO block size as well.
        for (int entries = TargetEntryCapacity; entries >= Capacity; entries >>= 1)
            if (HeaderBytes + (long)entries * RecordBytes <= maximumBytes)
                return entries;
        return 0;
    }

    private void NextGeneration()
    {
        Generation = (Generation + 1) & GenerationMask;
        EpochChanges++;
        if (Generation == 0) Generation = 1;
        // World/reanchor and stamp wrap must never blend history from the
        // preceding ownership. These rare events clear both GPU payload banks.
        Array.Clear(_regionStamps);
        RegionStamp = 0;
        _regionsChanged = true;
        BoundsReady = false;
        BoundsMinimum = BoundsMaximum = Vector3.Zero;
        int previous = GL.GetInteger(GetPName.ShaderStorageBufferBinding);
        try
        {
            GL.MemoryBarrier(MemoryBarrierFlags.ShaderStorageBarrierBit | MemoryBarrierFlags.BufferUpdateBarrierBit);
            GL.BindBuffer(BufferTarget.ShaderStorageBuffer, Buffer);
            GL.ClearBufferData(BufferTarget.ShaderStorageBuffer, PixelInternalFormat.R32ui, PixelFormat.RedInteger,
                PixelType.UnsignedInt, _zero);
        }
        finally
        {
            GL.BindBuffer(BufferTarget.ShaderStorageBuffer, previous);
        }
    }

    // Shader assets may change page coordinates or source policy without an ABI
    // change. Gate old banks now, then clear their epoch at the next reconciled
    // render-thread publication before any new worker can interpret them.
    internal void Reload()
    {
        _worker.Reload();
        _invalidate = true;
        _failed = false;
        Ready = false;
        _needsReconciliation = true;
    }

    internal void Suspend()
    {
        Ready = false;
        _needsReconciliation = true;
    }

    internal void PrepareDeferred(ICoreClientAPI api, int plane, int normal, int bounds, int width, int height)
    {
        if (!Active || !Ready || _failed) return;
        if (!_worker.Dispatch(api, this, plane, normal, bounds, width, height))
        {
            _failed = true;
            Ready = false;
        }
    }

    // Explicit post-profile snapshot only. Shipping frames never poll/read back
    // counters. The worker publishes whole pages; visible fragments are read-only.
    public void Dispose()
    {
        Ready = Active = false;
        if (Buffer != 0) GL.DeleteBuffer(Buffer);
        if (SourceBuffer != 0) GL.DeleteBuffer(SourceBuffer);
        Buffer = SourceBuffer = 0;
        _worker.Dispose();
        if (ReferenceEquals(Current, this)) Current = null;
    }
}