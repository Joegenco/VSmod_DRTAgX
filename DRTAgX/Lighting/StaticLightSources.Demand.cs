using System.Collections.Generic;

namespace DRTAgX;

internal sealed partial class StaticLightSources
{
    private PlacedLightReceiverDemand? _receiverDemand;
    private int _observedDemandRevision = -1;
    private const int DemandCacheLimit = 4096;
    private readonly Dictionary<Source, bool> _sourceDemand = new(256);
    private PlacedLightReceiverDemand? _cachedDemandOwner;
    private int _cachedDemandRevision = -1;
    internal int DemandRevision { get; private set; }

    internal void SetReceiverDemand(PlacedLightReceiverDemand demand) => _receiverDemand = demand;

    // Compact source truth survives camera turns; only discovery/admission and
    // GPU publication follow actual receiving geometry. Standalone fixtures
    // without a demand owner retain their original complete-source contract.
    internal bool IsDemanded(Source source)
    {
        var demand = _receiverDemand;
        if (demand == null) return true;
        // All receiver bounds, readiness and the discrete near-camera guard
        // advance Revision. Use the actual owner revision here because callers
        // can query before Source.Update observes a newly accepted GPU result.
        if (!ReferenceEquals(_cachedDemandOwner, demand) || _cachedDemandRevision != demand.Revision)
        {
            _sourceDemand.Clear();
            _cachedDemandOwner = demand;
            _cachedDemandRevision = demand.Revision;
        }

        if (_sourceDemand.TryGetValue(source, out var allowed)) return allowed;
        allowed = demand.Allows(source);
        // Full source values include reach changes; membership/color edits can
        // never reuse a stale decision. Exceptional dense worlds retain exact
        // tests beyond this bound instead of growing the cache without limit.
        if (_sourceDemand.Count < DemandCacheLimit) _sourceDemand.Add(source, allowed);
        return allowed;
    }

    private bool IsChunkDemanded(ChunkKey key) =>
        _receiverDemand?.AllowsChunk(key, StaticTerrainShadowMaps.Range) ?? true;

    private bool DemandChanged()
    {
        int revision = _receiverDemand?.Revision ?? 0;
        if (revision == _observedDemandRevision) return false;
        _observedDemandRevision = revision;
        DemandRevision++;
        _needsFlatten = _rebucket = true;
        // Demand discovery must not wait for the old quarter-second rebucket.
        _nextDiscoveryRebucket = 0;
        return true;
    }
}