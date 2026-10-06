using System;
using System.Diagnostics;
using DRTAgX;
using Vintagestory.API.MathTools;
using static StaticCacheProbeFixture;

// Exercise settled scheduling, retained depth and real wake deadlines. Synthetic
// completed maps isolate the cache from native terrain rendering and other mods.
internal static class SchedulerCacheProbe
{
    internal static void Run()
    {
        using var fixture = new StaticCacheProbeFixture();
        var source = new StaticLightSources.Source(0, 0, -10, 0, 0, 20);
        fixture.Source(source); fixture.Resident(source); fixture.Settle();
        int publication = fixture.Maps.PublicationRevision, admissions = fixture.Maps.Admissions;
        Check(!fixture.Maps.UpdatedThisFrame && fixture.Maps.Active.Count == 1,
            "settled resident scheduler sleeps with reusable publication");
        for (int reload = 0; reload < 12; ++reload)
        {
            fixture.Maps.RetainQualityDepth(); fixture.Settle();
            Check(fixture.Maps.Ready && fixture.Maps.ValidCount == 1 && fixture.Maps.Active.Count == 1 &&
                fixture.Maps.FacesBakedThisFrame == 0 && fixture.Maps.Admissions == admissions,
                "receiver-only quality reload retains complete caster depth without rebaking");
        }
        // Toggle contribution repeatedly without rebuilding completed depth.
        for (int toggle = 0; toggle < 8; toggle++) {
            fixture.Maps.Pause(fixture.Sources, true); fixture.Update();
            Check(fixture.Maps.ValidCount == 1 && fixture.Maps.Active.Count == 1 &&
                fixture.Maps.FacesBakedThisFrame == 0,
                "toggle retains completed nearby depth without rebaking");
        }
        fixture.Edits.Add(new(1, 0, -10));
        fixture.Maps.Pause(fixture.Sources, false);
        Check((bool)Get(fixture.Slot(), "Dirty"), "disabled cache retains exact block edit invalidation");
        fixture.Edits.Clear(); Set(fixture.Slot(), "Dirty", false); fixture.Settle();
        const int samples = 20000;
        var times = new double[samples];
        for (int i = 0; i < 1000; i++) fixture.Update();
        for (int i = 0; i < samples; i++) {
            long start = Stopwatch.GetTimestamp(); fixture.Update();
            times[i] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            if (fixture.Maps.UpdatedThisFrame || fixture.Maps.FacesBakedThisFrame != 0 || fixture.Maps.SourcesInspectedThisFrame != 0)
                throw new Exception("Warm scheduler repeated discovery/bake work");
        }
        Array.Sort(times);
        // Measure the owned Update path separately after timing/assertion helpers
        // are primed; reflection/proxy/JIT fixture setup is not a render allocation.
        _ = GC.GetAllocatedBytesForCurrentThread();
        long bytes = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < samples; i++) fixture.Update();
        bytes = GC.GetAllocatedBytesForCurrentThread() - bytes;
        Console.WriteLine($"CPU isolated settled scheduler: median/p95={times[samples/2]:F6}/{times[(int)(samples*.95)]:F6} ms; allocated={bytes} B/{samples} frames");
        Check(bytes == 0 && fixture.Maps.PublicationRevision == publication,
            "settled production scheduler allocates zero bytes and leaves publication unchanged");

        fixture.Camera.X = 320; fixture.View[12] = -320; fixture.ViewChanged(); fixture.Update();
        Set(fixture.Slot(), "ExitAt", Now - 3); fixture.Wake(); fixture.Settle();
        Check(fixture.Maps.ValidCount == 1 && fixture.Maps.Active.Count == 0 && !fixture.Maps.UpdatedThisFrame,
            "unused/out-of-range completed map survives and its exit fade settles");
        fixture.Camera.X = 0; fixture.View[12] = 0; fixture.ViewChanged(); fixture.Settle();
        Check(fixture.Maps.Active.Count == 1 && fixture.Maps.Active[0].Fade == 1 &&
            fixture.Maps.Admissions == admissions && fixture.Maps.FacesBakedThisFrame == 0,
            "return restores retained completed depth immediately without admission or rebake");

        int depthRevision = (int)Get(fixture.Slot(), "Revision");
        fixture.Source(source with { Hue = 12 }, false); fixture.Update();
        Check((int)Get(fixture.Slot(), "Revision") == depthRevision && !((bool)Get(fixture.Slot(), "Dirty")) &&
            fixture.Maps.Active[0].Source.Hue == 12 && fixture.Maps.PublicationRevision > publication,
            "HSV-only change republishes records without invalidating shadow depth");
        fixture.Sources.NativeGeometryNotifications = false;
        fixture.Changes.Add(new(0, 0, -1)); fixture.Changes.Add(new(0, 0, 0)); fixture.Update();
        fixture.Changes.Clear();
        Check((int)Get(fixture.Slot(), "Revision") == depthRevision + 1 && fixture.Maps.FacesBakedThisFrame == 0,
            "one geometry batch produces one depth revision and one debounced refresh");
        double wake = (double)Get(fixture.Maps, "_nextSchedulerWake");
        fixture.Update();
        Check(!fixture.Maps.UpdatedThisFrame && wake > Now,
            "fallback refresh sleeps until its real debounce deadline");
        fixture.Remove(source); fixture.Update();
        Check(fixture.Maps.ValidCount == 0 && fixture.Maps.Active.Count == 0,
            "removed source cannot keep a retained map published");

        FailureDeadline(); ReplacementCancellation(); ResetWorld(); NearbyProtection();
    }

    private static void NearbyProtection()
    {
        using var fixture = new StaticCacheProbeFixture(128);
        // Fill every slot, but leave one distant admission unfinished and in
        // cooldown. Occupied capacity must not masquerade as 128 valid maps.
        for (int i = 0; i < 128; i++)
        {
            var source = new StaticLightSources.Source(i % 8 - 4, i / 8 % 8 - 4,
                24 + i / 64, 0, 0, 7);
            fixture.Source(source); fixture.Resident(source, i);
        }
        Set(fixture.Slot(127), "Valid", false);
        Set(fixture.Slot(127), "FailedUntil", Now + 60);
        var incoming = new StaticLightSources.Source(0, 0, -10, 0, 0, 7);
        fixture.Source(incoming); fixture.Settle();
        var entry = new PlacedLightPendingQueue.Entry { Source = incoming, Priority = 0 };
        Check(fixture.Maps.ValidCount == 127 && fixture.Maps.BakeFailures == 0 &&
            (int)Call(fixture.Maps, "ChoosePendingVictim", entry) == -1 &&
            (int)Call(fixture.Maps, "WeakestResidentPriority") == -1,
            "127 valid maps protect nearby offscreen residents despite 128 occupied slots");
        entry.Immediate = true;
        Check((int)Call(fixture.Maps, "ChoosePendingVictim", entry) == -1,
            "new placements also preserve nearby valid maps below the cap");
        Check(!fixture.Maps.UpdatedThisFrame && fixture.Maps.FacesBakedThisFrame == 0,
            "protected nearby victims do not trigger recurring scheduler or bake work");
        Set(fixture.Slot(), "PlayerDistance", 32.0 * 32.0);
        Check((int)Call(fixture.Maps, "ChoosePendingVictim", entry) == 0,
            "32-block retention boundary preserves normal replacement outside the protected radius");
        Set(fixture.Slot(), "PlayerDistance", 25.0 * 25.0);
        Set(fixture.Slot(), "CameraDistance", 100.0 * 100.0);
        Check((int)Call(fixture.Maps, "ChoosePendingVictim", entry) == -1,
            "near-player protection remains independent of third-person camera distance");
        Set(fixture.Slot(127), "Valid", true);
        Call(fixture.Maps, "Publish");
        Check(fixture.Maps.ValidCount == 128 && (int)Call(fixture.Maps, "ChoosePendingVictim", entry) == 0,
            "full valid-map capacity retains normal priority-based replacement");
        var kind = typeof(StaticTerrainShadowMaps).GetNestedType("StageKind", System.Reflection.BindingFlags.NonPublic);
        Call(fixture.Maps, "BeginBake", 0, incoming, Enum.Parse(kind, "Replacement"), false);
        Set(fixture.Slot(127), "Valid", false); Call(fixture.Maps, "Publish");
        Call(fixture.Maps, "ValidateStage", fixture.Sources, fixture.Camera, fixture.View, fixture.Projection);
        Check(!((PlacedLightBake)Get(fixture.Maps, "_bake")).Active && (bool)Get(fixture.Slot(), "Valid") &&
            fixture.Pending.Count == 1,
            "valid-count drop cancels staged nearby eviction while retaining depth and its queued candidate");
    }

    private static void FailureDeadline()
    {
        using var fixture = new StaticCacheProbeFixture();
        var old = new StaticLightSources.Source(0, 0, 50, 0, 0, 20);
        var incoming = old with { Z = -10 };
        fixture.Source(old); fixture.Resident(old); fixture.Source(incoming); fixture.Update();
        // This fixture deliberately has no native terrain renderer. The single
        // replacement fails once and must retry only after its cooldown.
        Check(fixture.Maps.BakeFailures == 1 && fixture.Pending.Count == 1,
            "failed replacement preserves its pending candidate");
        double failedUntil = (double)Get(fixture.Slot(), "FailedUntil");
        Check(Math.Abs((double)Get(fixture.Maps, "_nextSchedulerWake") - failedUntil) < 0.001,
            "failed replacement schedules a wake when its protected victim cooldown ends");
        fixture.Update();
        Check(!fixture.Maps.UpdatedThisFrame && fixture.Maps.BakeFailures == 1,
            "failed replacement does no work before its cooldown");
        Set(fixture.Slot(), "FailedUntil", Now - 0.01); fixture.Wake(); fixture.Update();
        Check(fixture.Maps.BakeFailures == 2, "replacement becomes eligible again after cooldown");
    }

    private static void ReplacementCancellation()
    {
        using var fixture = new StaticCacheProbeFixture();
        var old = new StaticLightSources.Source(0, 0, 50, 0, 0, 20);
        var incoming = old with { Z = -10 };
        fixture.Source(old); fixture.Resident(old); fixture.Source(incoming);
        var residents = (System.Collections.Generic.Dictionary<StaticLightSources.Position, int>)Get(fixture.Maps, "_residents");
        fixture.Pending.Update(fixture.Sources, new Vec3d(), fixture.View, fixture.Projection, residents, Now);
        int candidate = fixture.Pending.Choose(true, true, Now, new());
        Set(fixture.Slot(), "Priority", PlacedLightPolicy.Offscreen);
        Call(fixture.Maps, "AdmitPending", candidate, false);
        // A transaction cancellation retains completed depth and its candidate.
        // Shader reload is stronger: it invalidates wind-dependent generations.
        Call(fixture.Maps, "CancelStage");
        Check(fixture.Pending.Count == 1 && fixture.Pending.Choose(true, true, Now, new()) >= 0,
            "ordinary replacement survives cancellation after its bounded discovery pass");
        Check((bool)Get(fixture.Slot(), "Valid"), "cancelled staging preserves old completed depth");
        fixture.Maps.ResetShaderLocations();
        Check(fixture.Pending.Count == 0 && !(bool)Get(fixture.Slot(), "Valid"),
            "shader reload drops obsolete completed depth and pending generations");
        fixture.Pending.Update(fixture.Sources, new Vec3d(), fixture.View, fixture.Projection, residents, Now);
        Check(fixture.Pending.Count == 2 && !fixture.Pending.DiscoveryPending,
            "shader reload restarts bounded discovery for retained source metadata");
    }

    private static void ResetWorld()
    {
        using var fixture = new StaticCacheProbeFixture();
        var source = new StaticLightSources.Source(0, 0, -10, 0, 0, 20);
        fixture.Source(source); fixture.Resident(source); fixture.Settle();
        fixture.Candidates.Clear(); Bump(fixture.Sources, "WorldGeneration"); fixture.Update();
        Check(fixture.Maps.ValidCount == 0 && fixture.Maps.Active.Count == 0,
            "world generation reset invalidates retained depth before publication");
    }
}
