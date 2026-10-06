using System;
using System.Collections.Generic;
using System.Linq;
using DRTAgX;
using Vintagestory.API.Common;

// Caster-only unloads have no emitter to trigger the source-bearing check.
// Count native property reads separately from actual rediscovery work.
internal static class EmptyChunkLifecycleProbe
{
    internal static void Run(Action<bool, string> check)
    {
        EmptyUnload(check);
        SourceTransitions(check);
        BoundedUnloads(check);
    }

    private static bool NoDiscovery(SourceCacheFixture fixture) =>
        fixture.ChunkQueries == 0 && fixture.IndexReads == 0 && fixture.BlockQueries == 0;

    private static void EmptyUnload(Action<bool, string> check)
    {
        using var fixture = new SourceCacheFixture(0);
        fixture.Settle(); fixture.Sources.NativeGeometryNotifications = true;
        fixture.ResetCounts(); fixture.Update();
        check(fixture.IdentityReads == 1 && NoDiscovery(fixture),
            "settled caster-only chunk validates its identity without discovery");
        fixture.Unload(); fixture.ResetCounts(); fixture.Update();
        check(fixture.Sources.ChangedChunks.Contains(new StaticLightSources.ChunkKey()) &&
            fixture.Sources.CachedChunks == 0 && NoDiscovery(fixture),
            "empty chunk unload invalidates caster depth without metadata or block queries");
        fixture.ResetCounts();
        for (int i = 0; i < 100; i++) fixture.Update();
        check(fixture.IdentityReads == 0 && NoDiscovery(fixture),
            "unloaded empty chunks stop identity checks and remain unpolled");
        fixture.Reload(); fixture.Dirty(EnumChunkDirtyReason.NewlyLoaded); fixture.Update();
        fixture.ResetCounts(); fixture.Unload(); fixture.Update();
        check(fixture.Sources.ChangedChunks.Count == 1 && NoDiscovery(fixture),
            "loaded replacement empty identity resumes caster invalidation");
    }

    private static void SourceTransitions(Action<bool, string> check)
    {
        using var fixture = new SourceCacheFixture(0);
        fixture.Settle(); fixture.Sources.NativeGeometryNotifications = true;
        fixture.Add(0, 0, 0); fixture.Changed(0, 0, 0); fixture.Update();
        fixture.ResetCounts(); fixture.Update();
        check(fixture.Sources.Sources.Count == 1 && fixture.IdentityReads == 1 && NoDiscovery(fixture),
            "adding a source removes duplicate empty-identity validation");
        fixture.Remove(0, 0, 0); fixture.Changed(0, 0, 0); fixture.Update();
        fixture.ResetCounts(); fixture.Unload(); fixture.Update();
        check(fixture.Sources.Sources.Count == 0 && fixture.Sources.ChangedChunks.Count == 1 &&
            fixture.IdentityReads == 1 && NoDiscovery(fixture),
            "removing the last source keeps caster-only unload invalidation active");
    }

    private static void BoundedUnloads(Action<bool, string> check)
    {
        using var fixture = new SourceCacheFixture(1) { LoadAllChunks = true };
        fixture.Settle(); fixture.Sources.NativeGeometryNotifications = true;
        var unloaded = fixture.EmptyKeys.Take(60).ToArray();
        foreach (var key in unloaded) fixture.UnloadEmpty(key);
        var seen = new HashSet<StaticLightSources.ChunkKey>();
        for (int i = 0; i < 100; i++) {
            fixture.ResetCounts(); fixture.Update();
            if (fixture.IdentityReads > 17 || !NoDiscovery(fixture))
                throw new Exception("Empty lifecycle validation exceeded its 16-read budget or rediscovered data");
            seen.UnionWith(fixture.Sources.ChangedChunks);
        }
        check(unloaded.All(seen.Contains) && fixture.Sources.Sources.Count == 1,
            "bounded empty identity checks cover all unloads through swap removals");

        // Leaving/reentering rebuilds the active identity ring, while the LRU
        // retains cold compact entries. No previously disposed identity returns.
        fixture.Camera.X = 384; fixture.View[12] = -384; fixture.Settle();
        fixture.Camera.X = 0; fixture.View[12] = 0; fixture.Settle();
        var returned = fixture.EmptyKeys.First(key => !unloaded.Contains(key) &&
            Math.Abs(key.X) < 3 && Math.Abs(key.Y) < 3 && Math.Abs(key.Z) < 3);
        fixture.UnloadEmpty(returned); seen.Clear();
        for (int i = 0; i < 100; i++) {
            fixture.ResetCounts(); fixture.Update();
            if (fixture.IdentityReads > 17 || !NoDiscovery(fixture))
                throw new Exception("Neighborhood rebuild corrupted the bounded identity checks");
            seen.UnionWith(fixture.Sources.ChangedChunks);
        }
        check(seen.Contains(returned) && fixture.Sources.Sources.Count == 1,
            "neighborhood return rebuilds caster-only validation without stale tracking");

        fixture.NewWorld(); fixture.Settle(); fixture.ResetCounts(); fixture.Update();
        check(fixture.Sources.CachedChunks <= 1331 && fixture.IdentityReads <= 17 && NoDiscovery(fixture),
            "world reset discards cold identity tracking and resumes bounded validation");
    }
}
