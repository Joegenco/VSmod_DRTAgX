using System.Diagnostics;
using Vintagestory.API.Common;

namespace DRTAgX;

internal sealed partial class StaticLightSources
{
    private const int PendingGeometryEditLimit = 256;
    private const byte BothGeometryParts = 3;

    private static readonly long GeometryEditWaitTicks =
        (long)(PlacedLightGeometry.EditFallbackSeconds * Stopwatch.Frequency);

    private readonly object _pendingGeometryLock = new();
    private readonly PendingGeometryEdit[] _pendingGeometryEdits = new PendingGeometryEdit[PendingGeometryEditLimit];
    private volatile int _pendingGeometryCount;

    private struct PendingGeometryEdit
    {
        internal Position Position;
        internal long Deadline, LatestNotification;
        internal byte ProcessedParts;
        internal bool Queued;
    }

    // Block callbacks may be off-thread. Store only primitives, before queuing
    // the source query, so an older upload cannot acknowledge a later callback.
    private void RememberPendingGeometryEdit(Position position)
    {
        if (!NativeGeometryNotifications) return;
        long now = Stopwatch.GetTimestamp();
        lock (_pendingGeometryLock)
        {
            for (int i = 0; i < _pendingGeometryCount; i++)
            {
                ref PendingGeometryEdit edit = ref _pendingGeometryEdits[i];
                if (edit.Position != position) continue;
                edit.LatestNotification = now;
                edit.ProcessedParts = 0;
                edit.Queued = true;
                return; // Repeated notifications never extend the first deadline.
            }

            // Overflow retains the existing immediate conservative path instead
            // of dropping an edit or allocating an unbounded fallback collection.
            if (_pendingGeometryCount == PendingGeometryEditLimit) return;
            _pendingGeometryEdits[_pendingGeometryCount++] = new PendingGeometryEdit
            {
                Position = position, Deadline = now + GeometryEditWaitTicks,
                LatestNotification = now, Queued = true
            };
        }
    }

    // Render-thread upload finalizer. Center/edge arrays replace independently;
    // without a verified per-voxel ownership rule, require both parts after the
    // latest callback. Unknown replacements revoke that part's acknowledgement.
    internal void OnGeometryUploadProcessed(IWorldChunk chunk, long uploadStarted,
        bool centerReplaced, bool centerKnown, bool edgeReplaced, bool edgeKnown)
    {
        if (!_chunkCoordinates.TryGetValue(chunk, out var coordinates) ||
            !_byChunk.TryGetValue(coordinates.Key, out var cached) || !ReferenceEquals(cached.Chunk, chunk)) return;
        lock (_pendingGeometryLock)
        {
            for (int i = 0; i < _pendingGeometryCount; i++)
            {
                ref PendingGeometryEdit edit = ref _pendingGeometryEdits[i];
                Position position = edit.Position;
                if (new ChunkKey(position.X >> 5, position.Y >> 5, position.Z >> 5) != coordinates.Key ||
                    uploadStarted <= edit.LatestNotification) continue;
                if (centerReplaced)
                    edit.ProcessedParts = (byte)((edit.ProcessedParts & ~1) | (centerKnown ? 1 : 0));
                if (edgeReplaced)
                    edit.ProcessedParts = (byte)((edit.ProcessedParts & ~2) | (edgeKnown ? 2 : 0));
                // Keep an acknowledged queued entry until its source notification
                // is consumed; otherwise absence would look like overflow/failure.
                if (edit.ProcessedParts == BothGeometryParts && !edit.Queued)
                    RemovePendingGeometryEdit(i--);
            }
        }
    }

    private bool ConsumePendingGeometryEdit(Position position)
    {
        long now = Stopwatch.GetTimestamp();
        lock (_pendingGeometryLock)
        {
            for (int i = 0; i < _pendingGeometryCount; i++)
            {
                ref PendingGeometryEdit edit = ref _pendingGeometryEdits[i];
                if (edit.Position != position) continue;
                if (edit.ProcessedParts == BothGeometryParts)
                {
                    RemovePendingGeometryEdit(i);
                    return false;
                }

                if (!NativeGeometryNotifications || now >= edit.Deadline)
                {
                    RemovePendingGeometryEdit(i);
                    return true;
                }

                edit.Queued = false;
                return false;
            }
        }

        return true; // Hook absent, bounded queue full, or lifecycle reset.
    }

    private void ExpirePendingGeometryEdits()
    {
        if (_pendingGeometryCount == 0) return; // Settled frames do no scan/locking.
        long now = Stopwatch.GetTimestamp();
        lock (_pendingGeometryLock)
        {
            for (int i = 0; i < _pendingGeometryCount; i++)
            {
                ref PendingGeometryEdit edit = ref _pendingGeometryEdits[i];
                // Queued callbacks remain covered by the existing 64/frame drain.
                if (edit.Queued || NativeGeometryNotifications && now < edit.Deadline) continue;
                _blockEdits.Add(edit.Position);
                RemovePendingGeometryEdit(i--);
            }
        }
    }

    private void DiscardPendingGeometryEdit(Position position)
    {
        lock (_pendingGeometryLock)
        {
            for (int i = 0; i < _pendingGeometryCount; i++)
                if (_pendingGeometryEdits[i].Position == position)
                {
                    RemovePendingGeometryEdit(i);
                    return;
                }
        }
    }

    private void RemovePendingGeometryEdit(int index)
    {
        _pendingGeometryEdits[index] = _pendingGeometryEdits[--_pendingGeometryCount];
        _pendingGeometryEdits[_pendingGeometryCount] = default;
    }

    private void ClearPendingGeometryEdits()
    {
        lock (_pendingGeometryLock)
        {
            System.Array.Clear(_pendingGeometryEdits, 0, _pendingGeometryCount);
            _pendingGeometryCount = 0;
        }
    }
}