using System;
using System.Collections.Generic;

namespace DRTAgX;

internal sealed partial class StaticLightSources
{
    private const int EmptyIdentityChecksPerFrame = 16;
    private readonly List<ChunkKey> _emptyChunks = new(1331);
    private int _emptyCursor;

    // Render-thread bookkeeping only. The frozen client API has no unload
    // event; empty chunks can still own casters affecting a resident light.
    private void TrackEmptyChunk(ChunkKey key, CachedChunk cached)
    {
        if (!cached.Indexed || cached.Sources.Count != 0 || !_activeChunks.Contains(key))
        {
            UntrackEmptyChunk(cached);
            return;
        }
        if (cached.EmptyIndex >= 0) return;
        cached.EmptyIndex = _emptyChunks.Count;
        _emptyChunks.Add(key);
    }

    private void UntrackEmptyChunk(CachedChunk cached)
    {
        int index = cached.EmptyIndex;
        if (index < 0) return;
        // Swap removal keeps edits/unloads O(1), including during validation.
        int last = _emptyChunks.Count - 1;
        ChunkKey moved = _emptyChunks[last];
        _emptyChunks[index] = moved;
        _emptyChunks.RemoveAt(last);
        if (index < _emptyChunks.Count) _byChunk[moved].EmptyIndex = index;
        cached.EmptyIndex = -1;
        if (_emptyCursor >= _emptyChunks.Count) _emptyCursor = 0;
    }

    private void ValidateEmptyChunks()
    {
        int checks = Math.Min(EmptyIdentityChecksPerFrame, _emptyChunks.Count);
        for (int i = 0; i < checks && _emptyChunks.Count > 0; i++)
        {
            if (_emptyCursor >= _emptyChunks.Count) _emptyCursor = 0;
            ChunkKey key = _emptyChunks[_emptyCursor];
            if (_byChunk[key].Chunk is { Disposed: false }) { _emptyCursor++; continue; }
            // No chunk lookup, LightPositions read, or emitter query. Missing
            // chunks wait for notifications and are never polled here.
            RemoveCached(key);
            _missingChunks.Add(key);
            Changed(key);
        }
    }
}
