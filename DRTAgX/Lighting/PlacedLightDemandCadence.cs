using System.Diagnostics;

namespace DRTAgX;

/// <summary>Three camera regions per second with atomic whole-cycle publication.</summary>
internal sealed class PlacedLightDemandCadence
{
    internal const int RegionHertz = 3, RegionsPerCycle = 9, CompleteCycleSeconds = 3;
    internal const int SettledMilliseconds = 100;
    internal static readonly long IntervalTicks = Stopwatch.Frequency / RegionHertz;
    internal static readonly long SettledTicks = Stopwatch.Frequency / 10;
    private long _next, _lastMotion;
    private ulong _observed, _covered;
    private bool _active;
    internal bool Pending => _observed != _covered;
    internal bool CycleActive { get; private set; }
    internal bool FullView { get; private set; }
    internal int Region { get; private set; }

    internal void Observe(bool changed, long now)
    {
        if (changed)
        {
            ++_observed;
            _lastMotion = now;
            // First motion gets complete coverage; later changes never postpone
            // an already scheduled region tick.
            if (!_active)
            {
                _active = true;
                FullView = true;
                _next = now;
            }
        }
        else if ((Pending || CycleActive) && now - _lastMotion >= SettledTicks)
        {
            // Earlier tiles may belong to earlier views. One final full sample
            // after motion settles prevents an early unsampled tile from hiding
            // a light forever; unfinished mixed-view output is discarded.
            FullView = true;
            _next = now;
        }
        else if (!Pending && !CycleActive && now - _lastMotion >= SettledTicks) _active = false;
    }

    internal bool Due(long now) => (Pending || CycleActive || FullView) && now >= _next;

    internal void Submitted(long now, bool full)
    {
        if (full)
        {
            _covered = _observed;
            FullView = CycleActive = false;
            Region = 0;
            if (now - _lastMotion >= SettledTicks) _active = false;
        }
        else if (Region == RegionsPerCycle - 1)
        {
            // Continuous motion starts another cycle while the prior complete
            // feedback remains readable. A stopped camera takes the full path.
            CycleActive = false;
            Region = 0;
        }
        else
        {
            CycleActive = true;
            ++Region;
        }

        _next = now + IntervalTicks;
    }

    internal void Reset()
    {
        _next = _lastMotion = 0;
        _observed = _covered = 0;
        _active = FullView = CycleActive = false;
        Region = 0;
    }
}