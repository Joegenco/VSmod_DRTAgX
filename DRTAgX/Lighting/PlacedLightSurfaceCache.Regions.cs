using System;
using OpenTK.Graphics.OpenGL4;

namespace DRTAgX;

internal sealed partial class PlacedLightSurfaceCache
{
    private readonly uint[] _regionStamps = new uint[RegionCount];
    private bool _regionsChanged;
    internal uint RegionStamp { get; private set; }
    internal int RegionEvents { get; private set; }
    internal int RegionUploads { get; private set; }
    internal int LastInvalidatedRegionCount { get; private set; }
    internal bool BoundsReady { get; private set; }
    private int _sourceContentRevision = -1;
    private object? _sourceWorld;

    internal void ObserveSourceChanges(StaticLightSources index)
    {
        _sourceIndex = index;
        if (_sourceContentRevision == index.SourceContentRevision && ReferenceEquals(_sourceWorld, _world)) return;
        _sourceTruthChanged = true;
        if (Buffer != 0)
        {
            BeginRegionEvent();
            if (_sourceContentRevision < 0 || index.SourceContentRevision != unchecked(_sourceContentRevision + 1) ||
                !ReferenceEquals(_sourceWorld, _world))
            {
                // A disabled/missing frame may have skipped its transient event
                // batch. Recover conservatively without deleting complete pages.
                for (int i = 0; i < SourceCount; ++i) MarkInfluence(_sources[i]);
                for (int i = 0; i < index.KnownSources.Count; ++i) MarkInfluence(index.KnownSources[i]);
                _visibleRefreshPending |= SourceCount > 0;
            }
            else
            {
                var changes = index.SourceChanges;
                for (int i = 0; i < changes.Count; ++i)
                {
                    if (changes[i].Previous.Level > 0) MarkInfluence(changes[i].Previous);
                    if (changes[i].Current.Level > 0) MarkInfluence(changes[i].Current);
                    // Hidden source edits may maintain cached truth, but they
                    // must not start expensive full-screen receiver collection.
                    _visibleRefreshPending |=
                        changes[i].Previous.Level > 0 && index.IsDemanded(changes[i].Previous) ||
                        changes[i].Current.Level > 0 && index.IsDemanded(changes[i].Current);
                }
            }
        }

        _sourceContentRevision = index.SourceContentRevision;
        _sourceWorld = _world;
    }

    // CPU and GLSL hash exactly the same signed world-region coordinates cast
    // to uint. Direct bucket collisions conservatively refresh extra regions;
    // there is no insertion failure or hash-only surface-key comparison.
    internal static int RegionBucket(int x, int y, int z)
    {
        unchecked
        {
            return (int)(Mix((uint)x ^ Mix((uint)y + 0x9e3779b9u) ^
                             Mix((uint)z + 0x85ebca6bu) ^ Mix(0x51ed270bu + 0xc2b2ae35u)) & (RegionCount - 1));
        }
    }

    private static uint Mix(uint value)
    {
        unchecked
        {
            value ^= value >> 16;
            value *= 0x7feb352du;
            value ^= value >> 15;
            value *= 0x846ca68bu;
            return value ^ (value >> 16);
        }
    }

    internal uint DesiredRegionStamp(int x, int y, int z)
    {
        uint stamp = _regionStamps[RegionBucket(x, y, z)];
        return stamp == 0 ? 1u : stamp;
    }

    private void BeginRegionEvent()
    {
        // Stamp 1 represents untouched storage. First edits must use 2 so a
        // previously filled untouched bucket cannot accidentally remain clean.
        if (RegionStamp == uint.MaxValue)
        {
            // ObserveSourceChanges precedes Publish. Preserve the wrap signal
            // until Publish clears old epoch ownership before any field is read.
            _invalidate = true;
            RegionStamp = 2;
        }
        else RegionStamp = RegionStamp == 0 ? 2u : RegionStamp + 1;

        RegionEvents++;
        LastInvalidatedRegionCount = 0;
    }

    private void MarkInfluence(StaticLightSources.Source source)
    {
        // Regions are 8 world blocks (signed 1/16 key cells >> 7). Include
        // the interpolation footprint around a lamp's exact reach sphere.
        double range = PlacedLightView.Reach(source) + .125;
        int x0 = (int)Math.Floor((source.X + .5 - range) / 8), x1 = (int)Math.Floor((source.X + .5 + range) / 8);
        int y0 = (int)Math.Floor((source.Y + StaticTerrainShadowMaps.SourceHeight - range) / 8);
        int y1 = (int)Math.Floor((source.Y + StaticTerrainShadowMaps.SourceHeight + range) / 8);
        int z0 = (int)Math.Floor((source.Z + .5 - range) / 8), z1 = (int)Math.Floor((source.Z + .5 + range) / 8);
        for (int z = z0; z <= z1; ++z)
        for (int y = y0; y <= y1; ++y)
        for (int x = x0; x <= x1; ++x)
        {
            int bucket = RegionBucket(x, y, z);
            if (_regionStamps[bucket] == RegionStamp) continue;
            _regionStamps[bucket] = RegionStamp;
            LastInvalidatedRegionCount++;
            _regionsChanged = true;
        }
    }

    private void MarkCurrentInfluence(StaticTerrainShadowMaps maps)
    {
        var lights = maps.CompletedLights;
        for (int i = 0; i < lights.Count; ++i)
            if (lights[i].Slot >= 0 && lights[i].Slot < maps.Capacity)
                MarkInfluence(lights[i].Source);
    }

    private void MarkAllInfluence(StaticTerrainShadowMaps maps)
    {
        for (int i = 0; i < SourceCount; ++i) MarkInfluence(_sources[i]);
        MarkCurrentInfluence(maps);
    }

    private void UploadRegions()
    {
        if (!_regionsChanged) return;
        int previous = GL.GetInteger(GetPName.ShaderStorageBufferBinding);
        try
        {
            GL.MemoryBarrier(MemoryBarrierFlags.ShaderStorageBarrierBit | MemoryBarrierFlags.BufferUpdateBarrierBit);
            GL.BindBuffer(BufferTarget.ShaderStorageBuffer, SourceBuffer);
            GL.BufferSubData(BufferTarget.ShaderStorageBuffer, (IntPtr)SourcePrefixBytes,
                RegionCount * sizeof(uint), _regionStamps);
            RegionUploads++;
            _regionsChanged = false;
        }
        finally
        {
            GL.BindBuffer(BufferTarget.ShaderStorageBuffer, previous);
        }
    }
}