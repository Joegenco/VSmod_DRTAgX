using System;
using System.Collections.Generic;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;

namespace DRTAgX;

internal sealed partial class PlacedLightSurfaceCache
{
    private readonly StaticLightSources.Source[] _sources =
        new StaticLightSources.Source[StaticTerrainShadowMaps.MaxDepthSources];

    private readonly int[] _slots = new int[StaticTerrainShadowMaps.MaxDepthSources];
    private readonly bool[] _depthReady = new bool[StaticTerrainShadowMaps.MaxDepthSources];
    private readonly uint[] _eligible = new uint[RegionCount];
    private StaticLightSources? _sourceIndex;
    private bool _sourceTruthChanged;
    private int _residentSourceCount;
    private int _knownSourceRevision = -1;
    private int _demandSourceRevision = -1;

    private readonly StaticLightSources.Source[] _previousDemandSources =
        new StaticLightSources.Source[StaticTerrainShadowMaps.MaxDepthSources];

    private readonly HashSet<StaticLightSources.Position> _previousDemandPositions =
        new(StaticTerrainShadowMaps.MaxDepthSources);

    private readonly HashSet<StaticLightSources.Position> _currentDemandPositions =
        new(StaticTerrainShadowMaps.MaxDepthSources);

    private readonly PlacedSourceGeometryRevision _denseGeometry = new();
    internal uint DenseSourceRevision => _denseGeometry.Revision;
    internal bool SourceBudgetExceeded { get; private set; }

    private readonly float[] _records =
        new float[StaticTerrainShadowMaps.MaxDepthSources * StaticLightGpuRecord.FloatCount];

    private bool SourcesChanged(StaticTerrainShadowMaps maps)
    {
        int count = 0;
        var lights = maps.CompletedLights;
        for (int i = 0; i < lights.Count; ++i)
        {
            var light = lights[i];
            // Staging may be complete but still crossfading a replacement.
            // Keep the promoted resident's field until its atomic depth copy.
            if (light.Slot < 0 || light.Slot >= maps.Capacity) continue;
            if (_sourceIndex != null && !_sourceIndex.IsDemanded(light.Source)) continue;
            if (count >= SourceCount || _sources[count] != light.Source || _slots[count] != light.Slot ||
                _depthReady[count] != maps.IsDepthCurrent(light.Source)) return true;
            count++;
        }

        if (count != _residentSourceCount) return true;
        bool indexChanged = _sourceTruthChanged || _sourceIndex != null &&
            (_knownSourceRevision != _sourceIndex.KnownRevision ||
             _demandSourceRevision != _sourceIndex.DemandRevision);
        if (!indexChanged) return false;

        // Depth readbacks can refine receiving bounds without changing any
        // contributing light. Compare the exact record sequence before a large
        // source/eligibility upload; a revision alone is not changed lighting.
        _currentDemandPositions.Clear();
        for (int i = 0; i < count; ++i)
            _currentDemandPositions.Add(StaticLightSources.Identity(_sources[i]));
        bool overflow = false;
        if (_sourceIndex != null)
        {
            var truth = _sourceIndex.KnownSources;
            for (int i = 0; i < truth.Count; ++i)
            {
                var source = truth[i];
                if (!_sourceIndex.IsDemanded(source) ||
                    _currentDemandPositions.Contains(StaticLightSources.Identity(source))) continue;
                if (count >= StaticTerrainShadowMaps.MaxDepthSources)
                {
                    // An unchanged full prefix must still preserve genuine
                    // overflow fallback. Returning below the budget is a change.
                    overflow = true;
                    break;
                }

                if (count >= SourceCount || _sources[count] != source || _slots[count] != -1 ||
                    _depthReady[count]) return true;
                _currentDemandPositions.Add(StaticLightSources.Identity(source));
                count++;
            }
        }

        if (count != SourceCount || overflow != SourceBudgetExceeded) return true;
        // Membership, HSV, readiness and atlas slots all match. Acknowledge the
        // observation without advancing evaluation or invalidating page banks;
        // semantic region stamps were already applied by ObserveSourceChanges.
        _knownSourceRevision = _sourceIndex?.KnownRevision ?? -1;
        _demandSourceRevision = _sourceIndex?.DemandRevision ?? -1;
        _sourceTruthChanged = false;
        return false;
    }

    private void UploadSources(StaticTerrainShadowMaps maps, float saturation)
    {
        bool demandChanged = _sourceIndex != null && _demandSourceRevision != _sourceIndex.DemandRevision;
        int previousDemandCount = demandChanged ? SourceCount : 0;
        if (demandChanged)
        {
            // Keep the old truthful footprints until the new demanded set is
            // packed. Membership changes invalidate only those world regions;
            // completed page banks remain readable during their coherent refresh.
            Array.Copy(_sources, _previousDemandSources, previousDemandCount);
            _previousDemandPositions.Clear();
            _currentDemandPositions.Clear();
            for (int i = 0; i < previousDemandCount; ++i)
                _previousDemandPositions.Add(StaticLightSources.Identity(_previousDemandSources[i]));
        }

        int count = 0;
        Array.Clear(_records);
        Array.Clear(_eligible);
        BoundsReady = false;
        SourceBudgetExceeded = false;
        BoundsMinimum = BoundsMaximum = Vector3.Zero;
        var lights = maps.CompletedLights;
        for (int i = 0; i < lights.Count; ++i)
        {
            var light = lights[i];
            if (light.Slot < 0 || light.Slot >= maps.Capacity) continue;
            if (_sourceIndex != null && !_sourceIndex.IsDemanded(light.Source)) continue;
            if (count >= StaticTerrainShadowMaps.MaxDepthSources) break;
            var source = light.Source;
            int at = count * StaticLightGpuRecord.FloatCount;
            float x = (float)(source.X + .5 - _anchorX),
                y = (float)(source.Y + StaticTerrainShadowMaps.SourceHeight - _anchorY),
                z = (float)(source.Z + .5 - _anchorZ),
                range = PlacedLightView.Reach(source);
            // Source7 retains the four-vec4 ABI, but its geometry is anchored
            // in world axes and its complete resident contribution has no fade.
            _records[at] = _records[at + 12] = x;
            _records[at + 1] = _records[at + 13] = y;
            _records[at + 2] = _records[at + 14] = z;
            _records[at + 3] = range;
            StaticLightGpuRecord.Rgb(source.Hue, source.Saturation, saturation,
                out _records[at + 4], out _records[at + 5], out _records[at + 6]);
            _records[at + 7] = light.Slot;
            _depthReady[count] = maps.IsDepthCurrent(source);
            // Source7 transition.x certifies fresh page evaluation. Its .y
            // certifies an existing complete map for read-only receivers:
            // dirty refreshes use staging and leave all six resident faces
            // intact until promotion, so animation must not expose native
            // lighting merely because a replacement is still being baked.
            _records[at + 8] = _depthReady[count] ? 1 : 0;
            _records[at + 9] = 1;
            _records[at + 10] = -1;
            _records[at + 11] = 0;
            _records[at + 15] = source.Level;
            _sources[count] = source;
            _slots[count] = light.Slot;
            count++;
            MarkEligibility(source);
            var position = new Vector3(x, y, z);
            var extent = new Vector3(range + .125f);
            // Rebuilt from current source truth, never a historical union.
            BoundsMinimum = BoundsReady ? Vector3.ComponentMin(BoundsMinimum, position - extent) : position - extent;
            BoundsMaximum = BoundsReady ? Vector3.ComponentMax(BoundsMaximum, position + extent) : position + extent;
            BoundsReady = true;
        }

        _residentSourceCount = count;
        if (_sourceIndex != null)
        {
            var truth = _sourceIndex.KnownSources;
            for (int i = 0; i < truth.Count; ++i)
            {
                var source = truth[i];
                if (!_sourceIndex.IsDemanded(source)) continue;
                bool resident = false;
                for (int j = 0; j < count; ++j)
                    if (StaticLightSources.Identity(_sources[j]) == StaticLightSources.Identity(source))
                    {
                        resident = true;
                        break;
                    }

                if (resident) continue;
                if (count >= StaticTerrainShadowMaps.MaxDepthSources)
                {
                    // Never certify a page against an incomplete source set.
                    // The owner publishes coherent native lighting until the
                    // retained truthful snapshot fits the fixed source budget.
                    SourceBudgetExceeded = true;
                    break;
                }

                int at = count * StaticLightGpuRecord.FloatCount;
                float x = (float)(source.X + .5 - _anchorX),
                    y = (float)(source.Y + StaticTerrainShadowMaps.SourceHeight - _anchorY),
                    z = (float)(source.Z + .5 - _anchorZ),
                    range = PlacedLightView.Reach(source);
                _records[at] = _records[at + 12] = x;
                _records[at + 1] = _records[at + 13] = y;
                _records[at + 2] = _records[at + 14] = z;
                _records[at + 3] = range;
                _records[at + 7] = -1;
                _records[at + 15] = source.Level;
                _sources[count] = source;
                _slots[count] = -1;
                _depthReady[count] = false;
                count++;
                MarkEligibility(source);
                var position = new Vector3(x, y, z);
                var extent = new Vector3(range + .125f);
                BoundsMinimum =
                    BoundsReady ? Vector3.ComponentMin(BoundsMinimum, position - extent) : position - extent;
                BoundsMaximum =
                    BoundsReady ? Vector3.ComponentMax(BoundsMaximum, position + extent) : position + extent;
                BoundsReady = true;
            }
        }

        SourceCount = count;
        if (demandChanged)
        {
            for (int i = 0; i < count; ++i)
            {
                var identity = StaticLightSources.Identity(_sources[i]);
                _currentDemandPositions.Add(identity);
                if (!_previousDemandPositions.Contains(identity)) MarkInfluence(_sources[i]);
            }

            for (int i = 0; i < previousDemandCount; ++i)
                if (!_currentDemandPositions.Contains(StaticLightSources.Identity(_previousDemandSources[i])))
                    MarkInfluence(_previousDemandSources[i]);
        }

        if (_denseGeometry.Update(_records, count, out bool densityWrapped) && densityWrapped)
        {
            // Never let a wrapped density revision certify an ancient sentinel.
            // Clear world ownership and rebuild regional truth before publication.
            bool boundsReady = BoundsReady;
            Vector3 minimum = BoundsMinimum, maximum = BoundsMaximum;
            NextGeneration();
            BoundsReady = boundsReady;
            BoundsMinimum = minimum;
            BoundsMaximum = maximum;
            BeginRegionEvent();
            MarkCurrentInfluence(maps);
        }

        _knownSourceRevision = _sourceIndex?.KnownRevision ?? -1;
        _demandSourceRevision = _sourceIndex?.DemandRevision ?? -1;
        _sourceTruthChanged = false;
        int previous = GL.GetInteger(GetPName.ShaderStorageBufferBinding);
        try
        {
            GL.MemoryBarrier(MemoryBarrierFlags.ShaderStorageBarrierBit | MemoryBarrierFlags.BufferUpdateBarrierBit);
            GL.BindBuffer(BufferTarget.ShaderStorageBuffer, SourceBuffer);
            GL.BufferSubData(BufferTarget.ShaderStorageBuffer, IntPtr.Zero, SourcePrefixBytes, _records);
            GL.BufferSubData(BufferTarget.ShaderStorageBuffer, (IntPtr)(SourcePrefixBytes + RegionCount * sizeof(uint)),
                RegionCount * sizeof(uint), _eligible);
            SourceUploads++;
        }
        finally
        {
            GL.BindBuffer(BufferTarget.ShaderStorageBuffer, previous);
        }
    }

    private void MarkEligibility(StaticLightSources.Source source)
    {
        double radius = PlacedLightView.Reach(source) + .125;
        double sx = source.X + .5, sy = source.Y + StaticTerrainShadowMaps.SourceHeight, sz = source.Z + .5;
        int x0 = (int)Math.Floor((sx - radius) / 8), x1 = (int)Math.Floor((sx + radius) / 8);
        int y0 = (int)Math.Floor((sy - radius) / 8), y1 = (int)Math.Floor((sy + radius) / 8);
        int z0 = (int)Math.Floor((sz - radius) / 8), z1 = (int)Math.Floor((sz + radius) / 8);
        for (int z = z0; z <= z1; ++z)
        for (int y = y0; y <= y1; ++y)
        for (int x = x0; x <= x1; ++x)
        {
            // True sphere/AABB intersection; a hashed conservative bit rejects
            // sparse empty neighborhoods without a per-fragment source loop.
            double dx = Math.Max(Math.Max(x * 8 - sx, sx - (x + 1) * 8), 0),
                dy = Math.Max(Math.Max(y * 8 - sy, sy - (y + 1) * 8), 0),
                dz = Math.Max(Math.Max(z * 8 - sz, sz - (z + 1) * 8), 0);
            if (dx * dx + dy * dy + dz * dz <= radius * radius) _eligible[RegionBucket(x, y, z)] = 1;
        }
    }
}