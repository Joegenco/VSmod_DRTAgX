using System;

namespace DRTAgX;

// Pure, deterministic cache rules. GL ownership remains with the shadow renderer.
internal static class PlacedLightPolicy
{
    internal const double NearDistanceSquared = 64 * 64;
    internal const double FarDelay = 2, UnusedSeconds = 10, ExitSeconds = 2;
    internal const double FadeSeconds = 0.20, RefreshSeconds = 0.15;
    internal const double EqualTierDwellSeconds = 2, FairAdmissionSeconds = 8, FullRetrySeconds = 0.5;
    internal const int Offscreen = 3;

    internal readonly record struct Resident(
        int Priority,
        double LastHit,
        bool Protected,
        int X,
        int Y,
        int Z,
        double DistanceSquared = double.PositiveInfinity,
        double CompletedAt = double.NegativeInfinity);

    internal static bool Visible(PlacedLightView.Bounds bounds) => bounds.Class <= 1 &&
                                                                   bounds.MaxX >= -1 && bounds.MinX <= 1 &&
                                                                   bounds.MaxY >= -1 && bounds.MinY <= 1;

    internal static int Priority(PlacedLightView.Bounds bounds, double distanceSquared) =>
        bounds.Class == 1 ? 2 :
        !Visible(bounds) ? Offscreen :
        PlacedLightView.Priority(bounds) == 0 ? 0 :
        distanceSquared <= NearDistanceSquared ? 1 : 2;

    internal static bool CanAdmit(double distanceSquared, bool full, double since, double now) =>
        !full || distanceSquared <= NearDistanceSquared || now - since >= FarDelay;

    internal static bool Expired(double lastHit, double completedAt, double now) =>
        now - Math.Max(lastHit, completedAt) >= UnusedSeconds;

    internal static float Smooth(double value)
    {
        float t = (float)Math.Clamp(value, 0, 1);
        return t * t * (3 - 2 * t);
    }

    internal static int Capacity(int arrayLayers) => Math.Clamp(arrayLayers / 6 - 1, 0, 128);

    internal static int DepthCapacity(int arrayLayers, int faceSize)
    {
        // Budget D24 conservatively at four bytes, including the staging cube.
        // Doubling both face dimensions needs four times the previous 256 MiB
        // allowance to retain the same light capacity during the manual experiment.
        // One existing array is enough; actual layer limits can lower residency.
        long cubeBytes = (long)faceSize * faceSize * 6 * 4;
        return cubeBytes <= 0
            ? 0
            : (int)Math.Clamp(Math.Min(arrayLayers / 6L - 1,
                (1024L * 1024 * 1024) / cubeBytes - 1), 0, StaticTerrainShadowMaps.MaxDepthSources);
    }

    internal static int ChooseVictim(ReadOnlySpan<Resident> residents, int incomingPriority)
        => ChooseVictim(residents, incomingPriority, double.PositiveInfinity, double.PositiveInfinity, 0);

    internal static bool EqualTierEligible(Resident resident, double incomingDistance, double since, double now) =>
        now - resident.CompletedAt >= EqualTierDwellSeconds && now - since >= EqualTierDwellSeconds &&
        (incomingDistance * 1.25 < resident.DistanceSquared || now - since >= FairAdmissionSeconds);

    internal static int ChooseVictim(ReadOnlySpan<Resident> residents, int incomingPriority,
        double incomingDistance, double since, double now)
    {
        int chosen = -1;
        for (int i = 0; i < residents.Length; i++)
        {
            Resident next = residents[i];
            if (next.Protected || next.Priority < incomingPriority ||
                next.Priority == incomingPriority && !EqualTierEligible(next, incomingDistance, since, now)) continue;
            if (chosen < 0 || next.Priority > residents[chosen].Priority ||
                next.Priority == residents[chosen].Priority &&
                // Actual reuse outranks bake age: a long-lived lamp just seen
                // behind the camera must survive ahead of a newer cold lamp.
                (next.LastHit < residents[chosen].LastHit ||
                 next.LastHit == residents[chosen].LastHit &&
                 (next.CompletedAt < residents[chosen].CompletedAt ||
                  next.CompletedAt == residents[chosen].CompletedAt &&
                  Coordinates(next, residents[chosen]) < 0))) chosen = i;
        }

        return chosen;
    }

    private static int Coordinates(Resident a, Resident b)
    {
        int order = a.X.CompareTo(b.X);
        if (order != 0) return order;
        order = a.Y.CompareTo(b.Y);
        return order != 0 ? order : a.Z.CompareTo(b.Z);
    }

    internal static int ChoosePlacementVictim(ReadOnlySpan<Resident> residents, int incomingPriority)
    {
        int chosen = ChooseVictim(residents, incomingPriority);
        if (chosen >= 0) return chosen;
        // A newly edited emitter gets one admission even when its tier is full.
        // Ordinary camera discovery still cannot displace equal-tier residents.
        for (int i = 0; i < residents.Length; i++)
        {
            var next = residents[i];
            if (next.Protected || next.Priority != incomingPriority) continue;
            if (chosen < 0 || next.LastHit < residents[chosen].LastHit ||
                next.LastHit == residents[chosen].LastHit && Coordinates(next, residents[chosen]) < 0) chosen = i;
        }

        return chosen;
    }
}

// A six-face transaction publishes only a complete snapshot of one owner/revision.
internal struct PlacedLightBake
{
    internal int Slot, Generation, Revision, NextFace;
    internal int FaceBudget;
    internal StaticLightSources.Source Source;
    internal bool Active, Background, Immediate;
    internal bool Complete => Active && NextFace == 6;
    internal int Budget => FaceBudget > 0 ? Math.Clamp(FaceBudget, 1, 6) : Background ? 1 : 2;

    internal bool Matches(int slot, int generation, int revision, StaticLightSources.Source source) =>
        Active && Slot == slot && Generation == generation && Revision == revision &&
        StaticLightSources.Identity(Source) == StaticLightSources.Identity(source);

    internal void Advance(int faces)
    {
        if (faces < 0 || faces > Budget || NextFace + faces > 6)
            throw new ArgumentOutOfRangeException(nameof(faces));
        NextFace += faces;
    }
}