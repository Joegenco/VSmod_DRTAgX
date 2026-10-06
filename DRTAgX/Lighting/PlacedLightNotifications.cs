using System.Collections.Concurrent;
using System.Collections.Generic;
using Vintagestory.API.Common;

namespace DRTAgX;

// Callbacks can arrive off the render thread. Deduplicate before consuming the
// frame budget, and remove a claim before processing so concurrent updates are
// queued again rather than lost during an emitter query.
internal sealed class PlacedLightNotifications
{
    internal const byte Metadata = 1, Geometry = 2, Loaded = 4;
    private sealed class Batch
    {
        internal readonly ConcurrentDictionary<StaticLightSources.ChunkKey, byte> Chunks = new();
        internal readonly ConcurrentQueue<StaticLightSources.ChunkKey> ChunkOrder = new();
        internal readonly ConcurrentDictionary<StaticLightSources.Position, byte> Positions = new();
        internal readonly ConcurrentQueue<StaticLightSources.Position> PositionOrder = new();
        internal readonly ConcurrentDictionary<IWorldChunk, byte> Uploads = new(ReferenceEqualityComparer.Instance);
        internal readonly ConcurrentQueue<IWorldChunk> UploadOrder = new();
    }
    private volatile Batch _batch = new();

    internal void Chunk(StaticLightSources.ChunkKey key, byte flags)
    {
        Batch batch = _batch;
        while (true)
        {
            if (batch.Chunks.TryAdd(key, flags)) { batch.ChunkOrder.Enqueue(key); return; }
            if (batch.Chunks.TryGetValue(key, out byte previous) &&
                batch.Chunks.TryUpdate(key, (byte)(previous | flags), previous)) return;
        }
    }

    internal void Position(StaticLightSources.Position position)
    {
        Batch batch = _batch;
        if (batch.Positions.TryAdd(position, 0)) batch.PositionOrder.Enqueue(position);
    }

    internal void Uploaded(IWorldChunk chunk)
    {
        Batch batch = _batch;
        if (batch.Uploads.TryAdd(chunk, 0)) batch.UploadOrder.Enqueue(chunk);
    }

    internal bool TryPosition(out StaticLightSources.Position position)
    {
        Batch batch = _batch;
        if (!batch.PositionOrder.TryDequeue(out position)) return false;
        batch.Positions.TryRemove(position, out _); return true;
    }

    internal bool TryChunk(out StaticLightSources.ChunkKey key, out byte flags)
    {
        Batch batch = _batch;
        flags = 0;
        return batch.ChunkOrder.TryDequeue(out key) && batch.Chunks.TryRemove(key, out flags);
    }

    internal bool TryUpload(out IWorldChunk? chunk)
    {
        Batch batch = _batch;
        if (!batch.UploadOrder.TryDequeue(out chunk)) return false;
        batch.Uploads.TryRemove(chunk, out _); return true;
    }

    internal void Clear()
    {
        // Swap claims and ordering together. Clearing separate concurrent
        // containers could strand a new-world claim with no queued notification.
        _batch = new Batch();
    }
}
