using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using DRTAgX;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

// Exercise production indexing against chunk lifecycle/events, independently of
// scene rendering. Counts distinguish real discovery from cheap cached checks.
internal static class SourceCacheProbe
{
    internal static void Run(bool requireFixed)
    {
        AssemblyLoadContext.Default.Resolving += (context, name) => {
            string install = Environment.GetEnvironmentVariable("VINTAGE_STORY")!;
            string path = Path.Combine(install, "Lib", name.Name + ".dll");
            if (!File.Exists(path)) path = Path.Combine(install, name.Name + ".dll");
            return File.Exists(path) ? context.LoadFromAssemblyPath(path) : null;
        };
        void Check(bool ok, string name) {
            Console.WriteLine((ok ? "PASS " : "OBSERVED FAILURE ") + name);
            if (!ok && requireFixed) throw new Exception(name);
        }
        using var fixture = new SourceCacheFixture(128);
        fixture.Settle();
        Check(fixture.Sources.Sources.Count == 128, "initial loaded chunk indexes all 128 emitters");
        fixture.ResetCounts();
        // Expire the old timer once, then allow its entire bounded batch to run.
        // This is outside timing and has no effect after timed maintenance is removed.
        typeof(StaticLightSources).GetField("_nextMissingRetry", BindingFlags.Instance | BindingFlags.NonPublic)
            ?.SetValue(fixture.Sources, 0L);
        for (int i = 0; i < 150; i++) fixture.Update();
        Console.WriteLine($"COUNTERS idle: chunks={fixture.ChunkQueries}, indexes={fixture.IndexReads}, blocks={fixture.BlockQueries}");
        Check(fixture.ChunkQueries == 0 && fixture.IndexReads == 0 && fixture.BlockQueries == 0,
            "unchanged indexed area performs no chunk lookups or emitter rescans");

        fixture.Camera.X = 320; fixture.View[12] = -320; fixture.Settle();
        fixture.ResetCounts();
        fixture.Camera.X = 0; fixture.View[12] = 0; fixture.Settle();
        Console.WriteLine($"COUNTERS return: indexes={fixture.IndexReads}, blocks={fixture.BlockQueries}");
        Check(fixture.Sources.Sources.Count == 128 && fixture.IndexReads == 0 && fixture.BlockQueries == 0,
            "return to the same loaded chunk reuses its emitter positions");

        var queue = new PlacedLightPendingQueue();
        var residents = new Dictionary<StaticLightSources.Position, int>();
        for (int i = 0; i < fixture.Sources.Sources.Count; i++)
            residents[StaticLightSources.Identity(fixture.Sources.Sources[i])] = i;
        for (int i = 0; i < 1000; i++) {
            fixture.Update(); queue.Update(fixture.Sources, fixture.Camera, fixture.View, fixture.Projection, residents, 100);
        }
        const int samples = 20000;
        var indexTimes = new double[samples]; var queueTimes = new double[samples];
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < samples; i++) {
            long start = Stopwatch.GetTimestamp(); fixture.Update();
            indexTimes[i] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            start = Stopwatch.GetTimestamp();
            queue.Update(fixture.Sources, fixture.Camera, fixture.View, fixture.Projection, residents, 100);
            queueTimes[i] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        }
        allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
        // DispatchProxy allocates argument arrays and boxes native bool getters.
        // Subtract an identical API-call control before judging owned cache work.
        for (int i = 0; i < 1000; i++) fixture.AdapterControl();
        long control = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < samples; i++) fixture.AdapterControl();
        control = GC.GetAllocatedBytesForCurrentThread() - control;
        Array.Sort(indexTimes); Array.Sort(queueTimes);
        Console.WriteLine($"CPU isolated warm fixture/128: index median/p95={indexTimes[samples/2]:F6}/{indexTimes[(int)(samples*.95)]:F6} ms; queue={queueTimes[samples/2]:F6}/{queueTimes[(int)(samples*.95)]:F6} ms; allocated={allocated} B/{samples} frames");
        Console.WriteLine($"ALLOCATION adapter control={control} B/{samples} frames; owned excess={allocated-control} B");
        if (requireFixed) Check(allocated == control, "warm index/queue allocate no bytes beyond the API adapter control");

        using var delayed = new SourceCacheFixture(0);
        delayed.Settle(); delayed.Add(31, 1, 1);
        delayed.Dirty(EnumChunkDirtyReason.MarkedDirty); delayed.Update();
        Check(delayed.Sources.Sources.Count == 1,
            "chunk metadata update discovers an emitter after an empty first scan");
        delayed.ResetCounts();
        for (int i = 0; i < 1000; i++) delayed.Dirty(EnumChunkDirtyReason.MarkedDirty);
        for (int i = 0; i < 20; i++) delayed.Update();
        Console.WriteLine($"COUNTERS duplicate notifications: indexes={delayed.IndexReads}, blocks={delayed.BlockQueries}");
        Check(delayed.IndexReads == 1, "duplicate chunk notifications coalesce into one metadata scan");
        if (requireFixed) { Lifecycle(Check); SourceCacheLifecycleProbe.Run(Check); EmptyChunkLifecycleProbe.Run(Check); SourceEditProbe.Run(Check); }
    }

    private static void Lifecycle(Action<bool, string> check)
    {
        using var fixture = new SourceCacheFixture(1);
        fixture.Settle();
        var original = fixture.Sources.Sources[0];
        int viewRevision = fixture.Sources.ViewRevision;
        fixture.View[12] = 0.01; fixture.Update();
        check(fixture.Sources.ViewRevision > viewRevision && fixture.IndexReads == 1,
            "in-place native camera changes update view revision without rescanning emitters");
        var queue = new PlacedLightPendingQueue();
        var residents = new Dictionary<StaticLightSources.Position, int>();
        queue.Update(fixture.Sources, fixture.Camera, fixture.View, fixture.Projection, residents, 100);
        queue.Update(fixture.Sources, fixture.Camera, fixture.View, fixture.Projection, residents, 100);
        check(queue.InspectedThisFrame == 0 && !queue.DiscoveryPending,
            "completed pending discovery does not cycle its source cursor");
        residents[StaticLightSources.Identity(original)] = 0;
        queue.Update(fixture.Sources, fixture.Camera, fixture.View, fixture.Projection, residents, 100);
        check(queue.Count == 0, "new residency removes a pending entry without rediscovery");
        residents.Clear(); queue.ResidencyRevision++;
        queue.Update(fixture.Sources, fixture.Camera, fixture.View, fixture.Projection, residents, 101);
        check(queue.Count == 1, "released residency restarts a bounded source pass");

        fixture.Sources.NativeGeometryNotifications = true;
        fixture.SetLight(0, 0, 0, new byte[] { 12, 4, 18 });
        fixture.Dirty(EnumChunkDirtyReason.MarkedDirty); fixture.Update();
        check(fixture.Sources.TryGet(original, out var recolored) && recolored.Hue == 12 &&
            fixture.Sources.ChangedChunks.Count == 0, "HSV-only metadata updates do not invalidate depth");
        fixture.ResetCounts();
        for (int i = 0; i < 1000; i++) fixture.Sources.OnGeometryUploaded(fixture.Chunk);
        fixture.Update();
        check(fixture.Sources.ChangedChunks.Count == 1 && fixture.IndexReads == 0 && fixture.BlockQueries == 0,
            "native geometry uploads coalesce without rescanning source metadata");
        fixture.ResetCounts();
        fixture.Changed(0, 0, 0); fixture.Dirty(EnumChunkDirtyReason.MarkedDirty); fixture.Update();
        check(fixture.BlockQueries == 1, "exact-position and chunk notifications query an emitter only once per frame");
        fixture.Remove(0, 0, 0); fixture.Changed(0, 0, 0); fixture.Update();
        check(!fixture.Sources.TryGet(original, out _), "source removal clears the global cached position immediately");

        fixture.Add(0, 0, 0); fixture.Changed(0, 0, 0); fixture.Update();
        fixture.Unload(); fixture.Update();
        check(fixture.Sources.Sources.Count == 0 && !fixture.Sources.TryGet(original, out _),
            "disposed chunk cannot retain active or global source identities");
        fixture.Reload(); fixture.Dirty(EnumChunkDirtyReason.NewlyLoaded); fixture.Update();
        check(fixture.Sources.Sources.Count == 1 && fixture.Sources.ChangedChunks.Count == 1,
            "new chunk identity at cached coordinates reindexes and invalidates geometry");
        int generation = fixture.Sources.WorldGeneration;
        fixture.Camera.Y = 2000; fixture.View[13] = -2000; fixture.Settle();
        check(fixture.Sources.WorldGeneration > generation && !fixture.Sources.TryGet(original, out _),
            "dimension-band change clears old source identities");
    }
}

internal sealed class SourceCacheFixture : IDisposable
{
    internal readonly StaticLightSources Sources;
    internal readonly Vec3d Camera = new();
    internal readonly double[] View = { 1,0,0,0, 0,1,0,0, 0,0,1,0, 0,0,0,1 };
    internal readonly float[] Projection = { 1,0,0,0, 0,1,0,0, 0,0,-1,-1, 0,0,-1,0 };
    internal int ChunkQueries, IndexReads, BlockQueries, IdentityReads;
    private readonly Dictionary<StaticLightSources.Position, Block> _blocks = new();
    private readonly HashSet<int> _positions = new();
    private readonly Block _air = new() { LightHsv = new byte[] { 0, 0, 0 } };
    private readonly Block _emitter = new() { LightHsv = new byte[] { 0, 0, 20 } };
    private IWorldChunk _chunk;
    private IClientWorldAccessor _world;
    private readonly Func<IClientWorldAccessor> _makeWorld;
    private readonly ICoreClientAPI _api;
    private readonly Dictionary<StaticLightSources.ChunkKey, IWorldChunk> _emptyChunks = new();
    private readonly Dictionary<StaticLightSources.ChunkKey, ChunkState> _emptyStates = new();
    internal IEnumerable<StaticLightSources.ChunkKey> EmptyKeys => _emptyChunks.Keys;
    internal bool Available = true, LoadAllChunks;
    internal int ChunkReadFailures, IndexReadFailures, BlockReadFailures;
    private ChunkState _state = new();
    private sealed class ChunkState { internal bool Disposed; }
    internal IWorldChunk Chunk => _chunk;
    private ChunkDirtyDelegate? _dirty;
    private BlockChangedDelegate? _changed;

    internal SourceCacheFixture(int lights)
    {
        for (int i = 0; i < lights; i++) Add(i % 16, 0, i / 16);
        _chunk = MakeChunk(_state);
        IWorldChunk MakeChunk(ChunkState state) => PlacementApiProxy.Make<IWorldChunk>((method, _) => {
            switch (method.Name) {
                case "get_Disposed": IdentityReads++; return state.Disposed;
                case "get_LightPositions":
                    IndexReads++;
                    if (IndexReadFailures > 0) { IndexReadFailures--; throw new InvalidOperationException("Concurrent index fixture"); }
                    return _positions;
                case "AcquireBlockReadLock": case "ReleaseBlockReadLock": case "Unpack_ReadOnly": return null;
                default: throw new NotSupportedException(method.Name);
            }
        });
        var accessor = PlacementApiProxy.Make<IBlockAccessor>((method, args) => {
            var pos = (BlockPos)args![0]!;
            if (method.Name == "GetChunkAtBlockPos") {
                ChunkQueries++;
                if (ChunkReadFailures > 0) { ChunkReadFailures--; throw new InvalidOperationException("Transient accessor fixture"); }
                var key = new StaticLightSources.ChunkKey(pos.X >> 5, pos.Y >> 5, pos.Z >> 5);
                if (key == new StaticLightSources.ChunkKey()) return Available ? _chunk : null;
                if (!LoadAllChunks) return null;
                if (!_emptyChunks.TryGetValue(key, out var empty)) {
                    var state = new ChunkState();
                    _emptyStates.Add(key, state);
                    empty = MakeEmptyChunk(state);
                    _emptyChunks.Add(key, empty);
                }
                return empty;
            }
            if (method.Name == "GetBlock") {
                BlockQueries++;
                if (BlockReadFailures > 0) { BlockReadFailures--; throw new InvalidOperationException("Emitter query fixture"); }
                return _blocks.GetValueOrDefault(new(pos.X, pos.Y, pos.Z), _air);
            }
            throw new NotSupportedException(method.Name);
        });
        IClientWorldAccessor MakeWorld() => PlacementApiProxy.Make<IClientWorldAccessor>((method, _) => method.Name == "get_BlockAccessor"
            ? accessor : throw new NotSupportedException(method.Name));
        _makeWorld = MakeWorld; _world = MakeWorld();
        var events = PlacementApiProxy.Make<IClientEventAPI>((method, args) => {
            switch (method.Name) {
                case "add_BlockChanged": _changed += (BlockChangedDelegate)args![0]!; break;
                case "remove_BlockChanged": _changed -= (BlockChangedDelegate)args![0]!; break;
                case "add_ChunkDirty": _dirty += (ChunkDirtyDelegate)args![0]!; break;
                case "remove_ChunkDirty": _dirty -= (ChunkDirtyDelegate)args![0]!; break;
                default: throw new NotSupportedException(method.Name);
            }
            return null;
        });
        _api = PlacementApiProxy.Make<ICoreClientAPI>((method, _) => method.Name switch {
            "get_World" => _world, "get_Event" => events, _ => throw new NotSupportedException(method.Name)
        });
        Sources = new StaticLightSources(_api);
        _makeChunk = MakeChunk;
    }
    private readonly System.Func<ChunkState, IWorldChunk> _makeChunk;
    private IWorldChunk MakeEmptyChunk(ChunkState state) => PlacementApiProxy.Make<IWorldChunk>((method, _) => {
        switch (method.Name) {
            case "get_Disposed": IdentityReads++; return state.Disposed;
            case "get_LightPositions": return ReadEmptyIndex();
            case "AcquireBlockReadLock": case "ReleaseBlockReadLock": case "Unpack_ReadOnly": return null;
            default: throw new NotSupportedException(method.Name);
        }
    });
    private object? ReadEmptyIndex() { IndexReads++; return null; }
    internal void AdapterControl() { _ = _api.World; _ = _chunk.Disposed; }
    internal void NewWorld() => _world = _makeWorld();
    internal void Add(int x, int y, int z) {
        _blocks[new(x, y, z)] = _emitter;
        _positions.Add(x | z << 5 | y << 10); // 32-cube ABI: X bits 0..4, Z 5..9, Y 10..14.
    }
    internal void Update() => Sources.Update(Camera, View, Projection);
    internal void Settle() {
        Update();
        for (int i = 0; i < 400 && !Sources.IsScanComplete; i++) Update();
        if (!Sources.IsScanComplete) throw new Exception("Source discovery did not settle within its bounded probe frames");
    }
    internal void Dirty(EnumChunkDirtyReason reason) => _dirty!(new Vec3i(), _chunk, reason);
    internal void Changed(int x, int y, int z) => _changed!(new BlockPos(x, y, z), _air);
    internal void SetLight(int x, int y, int z, byte[] hsv) => _blocks[new(x, y, z)] = new Block { LightHsv = hsv };
    internal void Remove(int x, int y, int z) {
        _blocks.Remove(new(x, y, z)); _positions.Remove(x | z << 5 | y << 10);
    }
    internal void Unload() => _state.Disposed = true;
    internal void Reload() { _state = new ChunkState(); _chunk = _makeChunk(_state); }
    internal void UnloadEmpty(StaticLightSources.ChunkKey key) => _emptyStates[key].Disposed = true;
    internal void ResetCounts() => ChunkQueries = IndexReads = BlockQueries = IdentityReads = 0;
    public void Dispose() => Sources.Dispose();
}
