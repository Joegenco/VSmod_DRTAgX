using System.Collections.Generic;

namespace DRTAgX;

internal sealed partial class StaticTerrainShadowMaps
{
    // Render-thread handles describe immutable completed content, not a source
    // list index. Callers release only after their referencing GPU work ends.
    internal readonly record struct CompletedDepthHandle(
        int Slot,
        int OwnerGeneration,
        int ContentGeneration,
        StaticLightSources.Position SourceIdentity);

    internal bool TryAcquireDepth(StaticLightSources.Source source, out CompletedDepthHandle handle)
    {
        handle = default;
        if (!_residents.TryGetValue(StaticLightSources.Identity(source), out int index)) return false;
        Slot slot = _slots[index];
        if (!slot.Occupied || !slot.Valid || !slot.KnownLoaded || slot.Dirty) return false;
        slot.Pins++;
        handle = new CompletedDepthHandle(index, slot.Generation, slot.ContentGeneration,
            StaticLightSources.Identity(slot.Source));
        return true;
    }

    internal bool ReleaseDepth(CompletedDepthHandle handle)
    {
        if ((uint)handle.Slot >= (uint)Capacity) return false;
        Slot slot = _slots[handle.Slot];
        if (slot.Generation != handle.OwnerGeneration || slot.ContentGeneration != handle.ContentGeneration ||
            StaticLightSources.Identity(slot.Source) != handle.SourceIdentity || slot.Pins <= 0) return false;
        slot.Pins--;
        if (slot.Pins == 0) _nextSchedulerWake = 0;
        return true;
    }

    internal bool IsDepthCurrent(StaticLightSources.Source source) =>
        _residents.TryGetValue(StaticLightSources.Identity(source), out int index) &&
        _slots[index].Occupied && _slots[index].KnownLoaded && _slots[index].Valid && !_slots[index].Dirty;

    private static int CompareCompleted(ActiveLight a, ActiveLight b)
    {
        int x = a.Source.X.CompareTo(b.Source.X);
        if (x != 0) return x;
        int y = a.Source.Y.CompareTo(b.Source.Y);
        return y != 0 ? y : a.Source.Z.CompareTo(b.Source.Z);
    }

    private sealed class PublicationComparer : IComparer<ActiveLight>
    {
        private readonly Slot[] _slots;
        internal PublicationComparer(Slot[] slots) => _slots = slots;

        public int Compare(ActiveLight a, ActiveLight b)
        {
            Slot aa = _slots[a.Slot], bb = _slots[b.Slot];
            int priority = aa.Priority.CompareTo(bb.Priority);
            if (priority != 0) return priority;
            int distance = aa.CameraDistance.CompareTo(bb.CameraDistance);
            return distance != 0 ? distance : CompareCompleted(a, b);
        }
    }
}