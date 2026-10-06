using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using DRTAgX;
using Vintagestory.API.Common;

// Difficult source-cache lifetimes and notification bursts. None of this setup
// enters the warm performance fixture or depends on native scene rendering.
internal static class SourceCacheLifecycleProbe
{
    internal static void Run(Action<bool, string> check)
    {
        using var fixture = new SourceCacheFixture(1);
        fixture.Settle(); var source = fixture.Sources.Sources[0];
        fixture.IndexReadFailures = 1; fixture.Dirty(EnumChunkDirtyReason.MarkedDirty); fixture.Update();
        check(fixture.Sources.TryGet(source, out _), "concurrent index snapshot failure preserves known emitters");
        fixture.ResetCounts(); fixture.Update();
        check(fixture.IndexReads == 1 && fixture.Sources.TryGet(source, out _), "concurrent index snapshot retries once on the next frame");
        fixture.ChunkReadFailures = 1; fixture.Dirty(EnumChunkDirtyReason.MarkedDirty); fixture.Update();
        check(fixture.Sources.TryGet(source, out _), "transient chunk accessor failure is not mistaken for unload");
        fixture.ResetCounts(); fixture.Update();
        check(fixture.ChunkQueries == 1 && fixture.Sources.TryGet(source, out _), "failed chunk lookup retries within the scan budget");
        fixture.BlockReadFailures = 1; fixture.Dirty(EnumChunkDirtyReason.MarkedDirty); fixture.Update();
        check(fixture.Sources.TryGet(source, out _), "failed per-instance emitter query preserves the previous source");

        var released = DetachChunk(fixture);
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        check(!released.IsAlive, "position cache and reverse upload index do not retain native chunk graphs");
        fixture.Update();
        check(!fixture.Sources.TryGet(source, out _), "collected native identity cannot publish cached source positions");
        fixture.Dirty(EnumChunkDirtyReason.NewlyLoaded); fixture.Settle();
        check(fixture.Sources.Sources.Count == 1, "loaded replacement identity restores discovery through its notification");
        int generation = fixture.Sources.WorldGeneration;
        fixture.Remove(0, 0, 0); fixture.NewWorld(); fixture.Settle();
        check(fixture.Sources.WorldGeneration > generation && !fixture.Sources.TryGet(source, out _),
            "new world identity clears all old source-cache entries");

        using var missing = new SourceCacheFixture(1) { Available = false };
        missing.Settle(); missing.ResetCounts();
        for (int i = 0; i < 100; i++) missing.Update();
        check(missing.ChunkQueries == 0, "unavailable chunks do not cause idle polling");
        missing.Available = true; missing.Dirty(EnumChunkDirtyReason.NewlyLoaded); missing.Update();
        check(missing.Sources.Sources.Count == 1, "load notification discovers a previously unavailable chunk immediately");
        Saturation(check); Notifications(check); Bound(check);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference DetachChunk(SourceCacheFixture fixture)
    {
        var old = new WeakReference(fixture.Chunk); fixture.Reload(); return old;
    }

    private static void Saturation(Action<bool, string> check)
    {
        using var fixture = new SourceCacheFixture(300); fixture.Settle();
        var queue = new PlacedLightPendingQueue();
        var residents = new Dictionary<StaticLightSources.Position, int>();
        int inspected = 0;
        for (int i = 0; i < 10; i++) {
            queue.Update(fixture.Sources, fixture.Camera, fixture.View, fixture.Projection, residents, 100);
            if (queue.InspectedThisFrame > 64 || queue.Count > 128) throw new Exception("Source shortlist exceeded its budgets");
            inspected += queue.InspectedThisFrame;
        }
        check(true, "source shortlist obeys per-frame and capacity budgets");
        check(inspected == 300 && !queue.DiscoveryPending && queue.Count == 128,
            "saturated pending queue inspects each source once then settles");
        var urgent = fixture.Sources.Sources[^1];
        queue.WeakestResidentPriority = 0;
        // An off-centre edit must not block an admissible stronger candidate.
        typeof(PlacedLightPendingQueue).GetMethod("Enqueue", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(queue, new object[] { urgent, fixture.Camera, fixture.View, fixture.Projection, residents, 100.0, true });
        check(queue.ChoosePlacement() < 0, "inadmissible urgent candidate respects current resident priority");
    }

    private static void Notifications(Action<bool, string> check)
    {
        var notifications = new PlacedLightNotifications();
        Parallel.For(0, 4096, i => {
            notifications.Position(new(i % 64, 0, 0));
            notifications.Chunk(new(0, 0, 0), (byte)(i % 2 == 0 ? PlacedLightNotifications.Metadata : PlacedLightNotifications.Loaded));
        });
        int count = 0; while (notifications.TryPosition(out _)) count++;
        check(count == 64 && notifications.TryChunk(out _, out byte flags) &&
            flags == (PlacedLightNotifications.Metadata | PlacedLightNotifications.Loaded) && !notifications.TryChunk(out _, out _),
            "concurrent duplicates preserve one notification per key and combine flags");
        notifications.Position(new(1, 0, 0)); notifications.TryPosition(out _); notifications.Position(new(1, 0, 0));
        check(notifications.TryPosition(out _), "notification claim is released before render-thread processing");
        Parallel.For(0, 1000, i => { if (i % 3 == 0) notifications.Clear(); else notifications.Position(new(i % 64, 0, 0)); });
        notifications.Clear();
        for (int i = 0; i < 64; i++) notifications.Position(new(i, 0, 0));
        count = 0; while (notifications.TryPosition(out _)) count++;
        check(count == 64, "world-reset batch swap cannot strand notification claims");
    }

    private static void Bound(Action<bool, string> check)
    {
        using var fixture = new SourceCacheFixture(0) { LoadAllChunks = true };
        for (int i = 0; i < 12; i++) {
            fixture.Camera.X = i * 384; fixture.View[12] = -fixture.Camera.X; fixture.Settle();
            if (fixture.Sources.CachedChunks > 4096) throw new Exception("Retained chunk cache exceeded its bound");
        }
        check(fixture.Sources.CachedChunks == 4096, "retained loaded chunk metadata stays within its 4096-entry LRU bound");
        // Validate a current caster-only identity after repeated cold eviction.
        // This catches stale tracking indices without inspecting implementation fields.
        var key = new StaticLightSources.ChunkKey((int)fixture.Camera.X >> 5, 0, 0);
        fixture.UnloadEmpty(key);
        bool invalidated = false;
        for (int i = 0; i < 100; i++) {
            fixture.ResetCounts(); fixture.Update();
            if (fixture.IdentityReads > 16 || fixture.ChunkQueries != 0 || fixture.IndexReads != 0 || fixture.BlockQueries != 0)
                throw new Exception("Cache trimming broke bounded empty-identity validation");
            for (int c = 0; c < fixture.Sources.ChangedChunks.Count; c++)
                invalidated |= fixture.Sources.ChangedChunks[c] == key;
        }
        check(invalidated && fixture.Sources.CachedChunks == 4095,
            "caster-only unload invalidates geometry after cold cache trimming");
    }
}
