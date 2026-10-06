using System.Diagnostics;
using Vintagestory.API.Client;

namespace DRTAgX;

// One native client/GL context owns these diagnostics. Alt+8 explicitly opts in;
// shipping frames skip timers and logging entirely until the user enables them.
internal static class PlacedLightGpuProfile
{
    internal static readonly GpuPassTimer Tiles = new(), Relight = new();
    private static readonly GpuTileOccupancy Occupancy = new();
    private static readonly PlacedLightCpuProfile Cpu = new();
    private static StaticLightSources? _sources;
    private static StaticTerrainShadowMaps? _maps;
    internal static bool Enabled { get; private set; }
    private static long _nextReport;
    private static int _faces;
    private static int _sampleFrame;
    private static int _maskDispatches;
    private static bool _resetOccupancy;

    private static void ResetOccupancy()
    {
        // Input callbacks only request reset; GL deletion runs on the render
        // thread at the next native light preparation/report boundary.
        if (!_resetOccupancy) return;
        Occupancy.Dispose(); _resetOccupancy = false;
    }

    internal static void SampleMasks(ICoreClientAPI api, int tiles)
    {
        ResetOccupancy();
        if (Enabled && (_sampleFrame++ & 63) == 0) Occupancy.Sample(api, tiles);
    }

    internal static void Toggle(ICoreClientAPI api)
    {
        if (!Vintagestory.Client.NoObf.ClientSettings.DeveloperMode) return;
        Enabled = !Enabled;
        Tiles.ClearSamples(); Relight.ClearSamples(); Cpu.Reset(); _resetOccupancy = true;
        _faces = _sampleFrame = _maskDispatches = 0; _nextReport = 0;
        api.Logger.Notification("[DRT AgX] Placed-light CPU/GPU profiling {0}; Alt+8 toggles. Placed-only timings/counters are written to client-main.log.", Enabled ? "enabled" : "disabled");
    }

    // Revoke profiling without GL deletion from input callbacks; the pending
    // occupancy cleanup runs at the existing render-thread boundary.
    internal static void Disable()
    {
        if (!Enabled) return;
        Enabled = false;
        Reload();
    }

    internal static void Reload()
    {
        _resetOccupancy = true;
        Tiles.ClearSamples(); Relight.ClearSamples(); Cpu.Reset(); _sampleFrame = _faces = _maskDispatches = 0; _nextReport = 0;
    }

    internal static void CacheFrame(StaticLightSources? sources, StaticTerrainShadowMaps maps,
        StaticLightTileBindings tiles, bool active)
    {
        if (!Enabled) return; // No sample writes/logging or new allocations in shipping frames.
        _sources = sources; _maps = maps;
        Cpu.Frame(sources, maps, tiles, active);
        _maskDispatches += tiles.GeneratedMasksThisFrame ? 1 : 0;
    }

    internal static void Frame(ICoreClientAPI api, int sourceCount, double rectangleCandidates,
        bool depthCull, int bakedFaces, double lastBake)
    {
        ResetOccupancy();
        if (!Enabled) return;
        _faces += bakedFaces;
        long now = Stopwatch.GetTimestamp();
        if (now < _nextReport) return;
        _nextReport = now + 3 * Stopwatch.Frequency;
        Tiles.Poll(); Occupancy.Poll();
        var tile = _maskDispatches > 0 ? Tiles.Statistics() : (0.0, 0.0);
        // Bake samples are tagged with actual face activity in this reporting
        // window. A persisted last-bake result is explicitly zero while idle.
        api.Logger.Notification("[DRT AgX Placed GPU] {0}x{1}, sources={2}, coarse candidates/tile={3:F2}, depthCull={4}; active tiles median/p95={5:F3}/{6:F3} ms; maskDispatches={7}; bakedFaces={8}, lastActiveBake={9:F3} ms; sampled masks avg/max={10:F2}/{11}",
            api.Render.FrameWidth, api.Render.FrameHeight, sourceCount, rectangleCandidates, depthCull,
            tile.Item1, tile.Item2, _maskDispatches, _faces, _faces > 0 ? lastBake : 0,
            sourceCount > 0 ? Occupancy.Average : 0, sourceCount > 0 ? Occupancy.Maximum : 0);
        if (_maps != null) Cpu.Report(api, _sources, _maps);
        _faces = _maskDispatches = 0;
    }

    internal static void Dispose()
    {
        Enabled = false; Tiles.Dispose(); Relight.Dispose(); Occupancy.Dispose(); Cpu.Reset();
        _sources = null; _maps = null; _faces = _maskDispatches = 0;
    }
}
