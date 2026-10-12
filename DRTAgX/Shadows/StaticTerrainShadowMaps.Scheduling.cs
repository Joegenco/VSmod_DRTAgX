using System;

namespace DRTAgX;

internal sealed partial class StaticTerrainShadowMaps
{
    internal int SourcesInspectedThisFrame => UpdatedThisFrame ? _pending.InspectedThisFrame : 0;

    // Unfinished admissions can occupy slots before 128 maps are valid. Until
    // the actual hardware-limited cap is complete, keep valid depth within 32
    // blocks of the character, regardless of visibility or placement tier.
    // An unloaded source retains cold depth but has no live nearby ownership;
    // its last player distance must never permanently prevent reclamation.
    private bool ProtectNearbyResident(Slot slot) => slot.Valid && slot.KnownLoaded &&
                                                     ValidCount < Capacity && slot.PlayerDistance < 32 * 32;

    private int WeakestResidentPriority()
    {
        if (_free.Count > 0) return int.MaxValue;
        int weakest = -1;
        for (int i = 0; i < Capacity; i++)
            if (_slots[i].Occupied && _slots[i].Pins == 0 && _time >= _slots[i].FailedUntil &&
                !ProtectNearbyResident(_slots[i]))
                weakest = Math.Max(weakest, _slots[i].Priority);
        return weakest;
    }

    private void ScheduleNextWake()
    {
        _nextSchedulerWake = double.PositiveInfinity;
        if (_bake.Active || _pending.DiscoveryPending || _lastResidencyRevision != _residencyRevision)
        {
            _nextSchedulerWake = _time;
            return;
        }

        for (int i = 0; i < Capacity; i++)
        {
            Slot slot = _slots[i];
            if (!slot.Occupied) continue;
            // A failed replacement protects its old, still-valid victim. Wake
            // when that protection ends even if the victim itself is not dirty.
            if (_pending.Count > 0 && slot.FailedUntil > _time)
                _nextSchedulerWake = Math.Min(_nextSchedulerWake, slot.FailedUntil);
            if (slot.Valid && (_time < slot.FadeAt + PlacedLightPolicy.FadeSeconds ||
                               slot.ExitAt >= 0 && _time < slot.ExitAt + PlacedLightPolicy.ExitSeconds))
                _nextSchedulerWake = _time;
            bool usefulWork = slot.KnownLoaded && (slot.Priority < PlacedLightPolicy.Offscreen && slot.ExitAt < 0 &&
                (slot.Dirty || !slot.Valid) || !slot.Valid && (slot.Immediate || _free.Count > 0));
            if (usefulWork)
                _nextSchedulerWake = Math.Min(_nextSchedulerWake,
                    Math.Max(_time, Math.Max(slot.RecheckAt, slot.FailedUntil)));
        }

        int weakest = WeakestResidentPriority();
        for (int i = 0; i < MaxSources; i++)
        {
            ref var entry = ref _pending.At(i);
            if (!entry.Occupied) continue;
            bool visible = entry.Priority < PlacedLightPolicy.Offscreen;
            bool canReplace = weakest >= entry.Priority;
            if (_free.Count == 0 && (!visible || !canReplace))
            {
                // Capacity/protection can change without a camera revision.
                if (visible)
                    _nextSchedulerWake = Math.Min(_nextSchedulerWake, _time + PlacedLightPolicy.FullRetrySeconds);
                continue;
            }

            double eligibleAt = !entry.Immediate && _free.Count == 0 &&
                                entry.Distance > PlacedLightPolicy.NearDistanceSquared
                ? entry.Since + PlacedLightPolicy.FarDelay
                : _time;
            // Full queues retry on one finite deadline; do not rescan every
            // resident for every pending entry merely to predict a wake.
            if (_free.Count == 0)
                eligibleAt = Math.Max(eligibleAt, _time + PlacedLightPolicy.FullRetrySeconds);
            _nextSchedulerWake = Math.Min(_nextSchedulerWake, Math.Max(_time, Math.Max(eligibleAt, entry.RecheckAt)));
        }
    }
}