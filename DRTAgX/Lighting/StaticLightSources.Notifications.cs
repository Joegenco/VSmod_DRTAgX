namespace DRTAgX;

internal sealed partial class StaticLightSources
{
    private void DrainNotifications()
    {
        // Reserve progress for mesh uploads and chunk metadata during an edit
        // burst; an otherwise empty queue still gives placements all 64 slots.
        int remaining = 64;
        for (int i = 0; i < 8 && UploadNotification(); i++) remaining--;
        for (int i = 0; i < 8 && ChunkNotification(); i++) remaining--;
        while (remaining > 0 && _notifications.TryPosition(out var position))
        {
            remaining--;
            NotificationsThisFrame++;
            ChunkKey key = new(position.X >> 5, position.Y >> 5, position.Z >> 5);
            if (!InRange(key) && !_byChunk.ContainsKey(key))
            {
                DiscardPendingGeometryEdit(position);
                continue;
            }

            // Source truth stays immediate. Caster edits wait briefly for actual
            // uploaded content, with a finite fallback if upload evidence misses.
            if (ConsumePendingGeometryEdit(position)) _blockEdits.Add(position);
            UpdateChangedEmitter(key, position);
            if (!Indexed(key)) _dirty.Add(key);
        }

        while (remaining > 0 && UploadNotification()) remaining--;
        while (remaining > 0 && ChunkNotification()) remaining--;
        ExpirePendingGeometryEdits();
    }

    private bool UploadNotification()
    {
        if (!_notifications.TryUpload(out var chunk)) return false;
        NotificationsThisFrame++;
        if (chunk != null && _chunkCoordinates.TryGetValue(chunk, out var coordinates)) Changed(coordinates.Key);
        return true;
    }

    private bool ChunkNotification()
    {
        if (!_notifications.TryChunk(out var key, out _)) return false;
        NotificationsThisFrame++;
        if (!InRange(key) && !_byChunk.ContainsKey(key)) return true;
        _missingChunks.Remove(key);
        _dirty.Add(key);
        // Without upload observation, a same-ID block-entity/chisel edit may
        // report only ChunkDirty. Coalesce one delayed refresh conservatively;
        // the installed hook keeps lighting-only metadata out of depth work.
        if (!NativeGeometryNotifications) Changed(key);
        return true;
    }
}