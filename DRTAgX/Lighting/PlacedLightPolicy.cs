using System;

namespace DRTAgX;

// Pure, deterministic cache rules. GL ownership remains with the shadow renderer.
internal static class PlacedLightPolicy
{
    internal const double NearDistanceSquared = 64 * 64;
    internal const double FarDelay = 2, UnusedSeconds = 10, ExitSeconds = 2;
    internal const double FadeSeconds = 0.20, RefreshSeconds = 0.15;
    internal const int Offscreen = 3;
    internal readonly record struct Resident(int Priority, double LastHit, bool Protected,
        int X, int Y, int Z);

    internal static bool Visible(PlacedLightView.Bounds bounds) => bounds.Class <= 1 &&
        bounds.MaxX >= -1 && bounds.MinX <= 1 && bounds.MaxY >= -1 && bounds.MinY <= 1;

    internal static int Priority(PlacedLightView.Bounds bounds, double distanceSquared) =>
        bounds.Class == 1 ? 2 : !Visible(bounds) ? Offscreen : PlacedLightView.Priority(bounds) == 0 ? 0 :
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

    internal static int ChooseVictim(ReadOnlySpan<Resident> residents, int incomingPriority)
    {
        int chosen = -1;
        for (int i = 0; i < residents.Length; i++)
        {
            Resident next = residents[i];
            if (next.Protected || next.Priority <= incomingPriority) continue;
            if (chosen < 0 || next.Priority > residents[chosen].Priority ||
                next.Priority == residents[chosen].Priority &&
                (next.LastHit < residents[chosen].LastHit || next.LastHit == residents[chosen].LastHit &&
                    Coordinates(next, residents[chosen]) < 0)) chosen = i;
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
    internal StaticLightSources.Source Source;
    internal bool Active, Background, Immediate;
    internal bool Complete => Active && NextFace == 6;
    internal int Budget => Background ? 1 : 2;

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
