using System;
using Vintagestory.API.MathTools;

namespace DRTAgX;

internal sealed partial class StaticLightSources
{
    private bool Indexed(ChunkKey key) => _byChunk.TryGetValue(key, out var cached) && cached.Indexed;

    private void ResetWorld(object world)
    {
        WorldGeneration++; Revision++;
        _world = world;
        _byChunk.Clear(); _chunkCoordinates.Clear(); _cacheLru.Clear();
        _missingChunks.Clear(); _dirty.Clear(); _retryNextFrame.Clear();
        _residentChunks.Clear(); _activeChunks.Clear(); _scanOrder.Clear();
        foreach (var queue in _discovery) queue.Clear();
        _queued.Clear(); _byPosition.Clear(); _notifications.Clear();
        _sources.Clear(); _sourceChunks.Clear();
        _emptyChunks.Clear(); _emptyCursor = 0;
        _blockEdits.Clear();
        _unconfirmedRemovals.Clear();
        _outstanding = 0;
        _nextDiscoveryRebucket = 0;
        _hasCenter = _hasOrderCamera = _hasInitialCoverage = false;
        _needsFlatten = _rebucket = true;
        _viewState.Reset();
    }

    private void ReconcileNeighborhood(ChunkKey center, Vec3d camera)
    {
        _center = center; _hasCenter = _hasOrderCamera = true;
        _orderCameraX = camera.X; _orderCameraY = camera.Y; _orderCameraZ = camera.Z;
        // Rebuild active empty identities without retaining checks for cold chunks.
        foreach (ChunkKey key in _emptyChunks)
            if (_byChunk.TryGetValue(key, out var cached)) cached.EmptyIndex = -1;
        _emptyChunks.Clear(); _emptyCursor = 0;
        _scanOrder.Clear(); _activeChunks.Clear();
        for (int y = -RadiusInChunks; y <= RadiusInChunks; y++)
            for (int z = -RadiusInChunks; z <= RadiusInChunks; z++)
                for (int x = -RadiusInChunks; x <= RadiusInChunks; x++)
                {
                    ChunkKey key = new(center.X + x, center.Y + y, center.Z + z);
                    if (!InRange(key)) continue;
                    _scanOrder.Add(key); _activeChunks.Add(key);
                    if (!_byChunk.TryGetValue(key, out var cached)) continue;
                    if (cached.Chunk is not { Disposed: false }) { RemoveCached(key); Changed(key); continue; }
                    Touch(cached);
                    TrackEmptyChunk(key, cached);
                }
        // Missing keys retain coverage only within one neighborhood. A later
        // return checks availability once; ordinary idle frames never poll them.
        _staleChunks.Clear();
        foreach (ChunkKey key in _missingChunks)
            if (!_activeChunks.Contains(key)) _staleChunks.Add(key);
        foreach (ChunkKey key in _staleChunks) _missingChunks.Remove(key);
        _needsFlatten = _rebucket = true;
        _nextDiscoveryRebucket = 0; // Neighborhood changes must queue new coverage immediately.
    }

    private void RebucketDiscovery()
    {
        foreach (var queue in _discovery) queue.Clear();
        _queued.Clear(); _outstanding = 0;
        foreach (ChunkKey key in _scanOrder)
        {
            if (Indexed(key) || _missingChunks.Contains(key)) continue;
            _queued.Add(key); _outstanding++;
            _discovery[ChunkPriority(key, ChunkDistance(key))].Enqueue(key);
        }
        _rebucket = false;
    }

    private void ValidateResidentChunks()
    {
        // Disposal is a cheap property read, not a chunk lookup/metadata scan.
        // Check source-bearing chunks so unloads are noticed even without a
        // public unload event or camera movement.
        foreach (ChunkKey key in _sourceChunks)
            if (_byChunk.TryGetValue(key, out var cached) && cached.Sources.Count > 0 &&
                cached.Chunk is not { Disposed: false })
            {
                RemoveCached(key); _missingChunks.Add(key); Changed(key);
            }
    }

    private CachedChunk Cached(ChunkKey key)
    {
        if (_byChunk.TryGetValue(key, out var cached)) { Touch(cached); return cached; }
        cached = new CachedChunk { Node = _cacheLru.AddFirst(key) };
        _byChunk.Add(key, cached); return cached;
    }

    private void Touch(CachedChunk cached)
    {
        _cacheLru.Remove(cached.Node); _cacheLru.AddFirst(cached.Node);
    }

    private void RemoveCached(ChunkKey key)
    {
        if (!_byChunk.Remove(key, out var cached)) return;
        UntrackEmptyChunk(cached);
        foreach (Source source in cached.Sources)
        {
            Position position = Identity(source);
            _byPosition.Remove(position); _unconfirmedRemovals.Remove(position);
        }
        var chunk = cached.Chunk;
        if (chunk != null) _chunkCoordinates.Remove(chunk);
        _cacheLru.Remove(cached.Node);
        if (cached.Sources.Count > 0) Revision++;
        if (_activeChunks.Contains(key)) _needsFlatten = true;
    }

    private void TrimCache()
    {
        while (_byChunk.Count > CachedChunkLimit)
        {
            var node = _cacheLru.Last;
            while (node != null && (_activeChunks.Contains(node.Value) || _residentChunks.Contains(node.Value)))
                node = node.Previous;
            if (node == null) return;
            RemoveCached(node.Value);
        }
    }

    private void Changed(ChunkKey key)
    {
        if (_changedSet.Add(key)) _changedChunks.Add(key);
    }

    private void RetryScan(ChunkKey key)
    {
        _retryNextFrame.Add(key);
        // A failed first snapshot remains outstanding until the dirty retry
        // completes; it must not publish complete discovery coverage early.
        if (!Indexed(key) && !_missingChunks.Contains(key) && _queued.Add(key)) _outstanding++;
    }

    private static void InsertSource(System.Collections.Generic.List<Source> sources, Source source)
    {
        int first = 0, end = sources.Count;
        while (first < end)
        {
            int middle = first + (end - first) / 2;
            if (CompareSources(sources[middle], source) < 0) first = middle + 1;
            else end = middle;
        }
        sources.Insert(first, source);
    }
}
