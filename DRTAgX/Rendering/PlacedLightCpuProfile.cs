using System;
using Vintagestory.API.Client;

namespace DRTAgX;

// Alt+8 only. Record placed-source indexing, cache work and tile preparation;
// simulation, moving lights and native deferred/HDR work are outside this scope.
internal sealed class PlacedLightCpuProfile
{
    private readonly double[][] _samples = { new double[128], new double[128], new double[128], new double[128] };
    private readonly double[] _sorted = new double[128];
    private int _sample, _count, _frames, _scans, _queries, _inspections, _updates, _uploads, _masks, _notifications;

    internal void Reset()
    {
        _sample = _count = _frames = _scans = _queries = _inspections = _updates = _uploads = _masks = _notifications = 0;
    }

    internal void Frame(StaticLightSources? sources, StaticTerrainShadowMaps maps, StaticLightTileBindings tiles, bool active)
    {
        double index = sources?.LastScanCpuMilliseconds ?? 0;
        double cache = active ? maps.LastTotalCpuMilliseconds : 0;
        double prepare = tiles.LastPrepareCpuMilliseconds;
        _samples[0][_sample] = index; _samples[1][_sample] = cache;
        _samples[2][_sample] = prepare; _samples[3][_sample] = index + cache + prepare;
        _sample = (_sample + 1) % _sorted.Length;
        _count = Math.Min(_count + 1, _sorted.Length); _frames++;
        _scans += sources?.ChunkScansThisFrame ?? 0; _queries += sources?.EmitterQueriesThisFrame ?? 0;
        _notifications += sources?.NotificationsThisFrame ?? 0;
        if (active) { _inspections += maps.SourcesInspectedThisFrame; _updates += maps.UpdatedThisFrame ? 1 : 0; }
        _uploads += tiles.UploadedRecordsThisFrame ? 1 : 0; _masks += tiles.GeneratedMasksThisFrame ? 1 : 0;
    }

    private (double Median, double P95) Statistics(int owner)
    {
        if (_count == 0) return (0, 0);
        Array.Copy(_samples[owner], _sorted, _count); Array.Sort(_sorted, 0, _count);
        return (_sorted[_count / 2], _sorted[Math.Min(_count - 1, (int)Math.Ceiling(_count * .95) - 1)]);
    }

    internal void Report(ICoreClientAPI api, StaticLightSources? sources, StaticTerrainShadowMaps maps)
    {
        var index = Statistics(0); var cache = Statistics(1); var tiles = Statistics(2); var total = Statistics(3);
        api.Logger.Notification("[DRT AgX Placed CPU] index/cache/tile/owner-total median,p95 ms={0:F4},{1:F4} / {2:F4},{3:F4} / {4:F4},{5:F4} / {6:F4},{7:F4}; frames={8}, scans={9}, emitterQueries={10}, pendingInspections={11}, schedulerUpdates={12}, recordUploads={13}, maskDispatches={14}, notifications={15}, cachedChunks={16}, residentMaps={17}/{18}, queue={19}, nativeUploadHook={20}",
            index.Median, index.P95, cache.Median, cache.P95, tiles.Median, tiles.P95, total.Median, total.P95,
            _frames, _scans, _queries, _inspections, _updates, _uploads, _masks, _notifications,
            sources?.CachedChunks ?? 0, maps.ValidCount, maps.Capacity, maps.QueueDepth, sources?.NativeGeometryNotifications ?? false);
        Reset();
    }
}
