using System.Collections.Generic;
using DRTAgX;
using static StaticCacheProbeFixture;

// Calculation order is camera distance within visible work; residency/victim
// policy and one-time intentional placement priority are separate decisions.
internal static class CameraBakeProbe
{
    internal static void Run()
    {
        var far = new StaticLightSources.Source(0, 0, -100, 0, 0, 20);
        var near = far with { X = 15, Z = -20 };
        using var fixture = new StaticCacheProbeFixture(2);
        fixture.Source(far); fixture.Resident(far, 0);
        fixture.Source(near); fixture.Resident(near, 1); fixture.Settle();
        Check((int)Get(fixture.Slot(0), "Priority") < (int)Get(fixture.Slot(1), "Priority"),
            "camera-order fixture distinguishes distant centred and nearer peripheral lights");
        for (int i = 0; i < 2; i++) { Set(fixture.Slot(i), "Dirty", true); Set(fixture.Slot(i), "DirtySince", Now - 5); }
        Check((int)Call(fixture.Maps, "ChooseDirty") == 1, "nearest visible light refreshes before a distant centred light");
        for (int i = 0; i < 2; i++) Set(fixture.Slot(i), "Valid", false);
        Check((int)Call(fixture.Maps, "ChooseMissing", true) == 1, "missing resident maps bake outward from the camera");
        for (int i = 0; i < 2; i++) Set(fixture.Slot(i), "Immediate", true);
        Check((int)Call(fixture.Maps, "ChoosePlacementMissing") == 1, "multiple missing placements start nearest the camera");

        using var pendingFixture = new StaticCacheProbeFixture(2);
        pendingFixture.Source(far); pendingFixture.Source(near);
        var residents = new Dictionary<StaticLightSources.Position, int>();
        pendingFixture.Pending.Update(pendingFixture.Sources, pendingFixture.Camera, pendingFixture.View, pendingFixture.Projection, residents, Now);
        int chosen = pendingFixture.Pending.Choose(true, false, Now, new());
        Check(chosen >= 0 && pendingFixture.Pending.At(chosen).Source == near,
            "pending discovery calculates the nearest visible light first");
        Call(pendingFixture.Pending, "Enqueue", far, pendingFixture.Camera, pendingFixture.View, pendingFixture.Projection, residents, Now, true);
        chosen = pendingFixture.Pending.ChoosePlacement();
        Check(chosen >= 0 && pendingFixture.Pending.At(chosen).Source == far,
            "intentional new placement retains its immediate admission priority");
        MixedVisibleWork(near, far);
    }

    private static void MixedVisibleWork(StaticLightSources.Source near, StaticLightSources.Source far)
    {
        using var fixture = new StaticCacheProbeFixture(2);
        fixture.Source(near); fixture.Resident(near, 0);
        fixture.Source(far); fixture.Resident(far, 1); fixture.Settle();
        Set(fixture.Slot(0), "Dirty", true); Set(fixture.Slot(0), "DirtySince", Now);
        Set(fixture.Slot(1), "Valid", false); fixture.Wake(); fixture.Update();
        // This fixture has no native renderer, so the chosen transaction fails
        // once. Its cooldown identifies which owner the real scheduler chose.
        Check(fixture.Maps.BakeFailures == 1 && (double)Get(fixture.Slot(0), "FailedUntil") > Now &&
            (double)Get(fixture.Slot(1), "FailedUntil") == 0 && (bool)Get(fixture.Slot(0), "Valid"),
            "near visible refresh precedes a distant missing map without deleting completed depth");
    }
}
