using System.Collections.Generic;

namespace DRTAgX;

internal sealed partial class StaticLightSources
{
    // Compact loaded source truth is independent of the camera shortlist. Reuse
    // the existing chunk metadata; no block snapshots or native chunk ownership.
    private readonly List<Source> _knownSources = new(256);
    private readonly List<ChunkKey> _knownChunks = new(256);
    private bool _knownDirty = true;
    private int _knownIdentityCursor;
    internal IReadOnlyList<Source> KnownSources => _knownSources;
    internal int KnownRevision { get; private set; }

    private void ValidateKnownSourceChunks()
    {
        // Active and depth-resident identities are already checked above. Cold
        // source-bearing identities need a finite rotating check too, otherwise
        // silent unloads could strand prerequisites or the global source budget.
        int checks = System.Math.Min(16, _knownChunks.Count);
        for (int i = 0; i < checks; ++i)
        {
            if (_knownIdentityCursor >= _knownChunks.Count) _knownIdentityCursor = 0;
            ChunkKey key = _knownChunks[_knownIdentityCursor++];
            if (_activeChunks.Contains(key) || _residentChunks.Contains(key) ||
                !_byChunk.TryGetValue(key, out var cached) || cached.Chunk is { Disposed: false }) continue;
            RemoveCached(key);
            if (_activeChunks.Contains(key)) _missingChunks.Add(key);
            Changed(key);
        }
    }

    private void RefreshKnownSources()
    {
        if (!_knownDirty) return;
        _knownSources.Clear();
        _knownChunks.Clear();
        _staleChunks.Clear();
        foreach (var pair in _byChunk)
        {
            if (pair.Value.Sources.Count == 0) continue;
            if (pair.Value.Chunk is not { Disposed: false })
            {
                _staleChunks.Add(pair.Key);
                continue;
            }
            _knownChunks.Add(pair.Key);
            _knownSources.AddRange(pair.Value.Sources);
        }
        // Verification itself may find an unload. Report every old footprint
        // before publishing the new truth, without mutating dictionary iteration.
        foreach (ChunkKey key in _staleChunks)
        {
            RemoveCached(key);
            if (_activeChunks.Contains(key)) _missingChunks.Add(key);
            Changed(key);
        }
        _knownSources.Sort(SourceOrderComparison);
        KnownRevision++;
        _knownDirty = false;
    }
}
