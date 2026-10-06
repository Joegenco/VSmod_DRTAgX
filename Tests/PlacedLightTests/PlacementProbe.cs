using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using DRTAgX;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

// Simulate client notifications and delayed native metadata. Source discovery
// and the bounded pending queue are the production code, without a GL context.
internal static class PlacementProbe
{
    internal static void Run(Action<bool, string> check, double[] view, float[] projection)
    {
        AssemblyLoadContext.Default.Resolving += (context, name) =>
        {
            string? install = Environment.GetEnvironmentVariable("VINTAGE_STORY");
            if (string.IsNullOrEmpty(install)) return null;
            string path = Path.Combine(install, "Lib", name.Name + ".dll");
            if (!File.Exists(path)) path = Path.Combine(install, name.Name + ".dll");
            return File.Exists(path) ? context.LoadFromAssemblyPath(path) : null;
        };
        var blocks = new Dictionary<StaticLightSources.Position, Block>();
        var positions = new HashSet<int>();
        var air = new Block { LightHsv = new byte[] { 0, 0, 0 } };
        var emitter = new Block { LightHsv = new byte[] { 0, 0, 20 } };
        for (int x = 0; x < 16; x++)
            for (int z = 0; z < 16; z++)
            {
                blocks.Add(new(x, 0, z), emitter);
                positions.Add(x | z << 5);
            }
        var chunk = PlacementApiProxy.Make<IWorldChunk>((method, _) => method.Name switch
        {
            "get_Disposed" => false,
            "get_LightPositions" => positions,
            "AcquireBlockReadLock" or "ReleaseBlockReadLock" => null,
            _ => throw new NotSupportedException(method.Name)
        });
        var accessor = PlacementApiProxy.Make<IBlockAccessor>((method, args) =>
        {
            var pos = (BlockPos)args![0]!;
            if (method.Name == "GetChunkAtBlockPos")
                return pos.X >> 5 == 0 && pos.Y >> 5 == 0 && pos.Z >> 5 == 0 ? chunk : null;
            if (method.Name == "GetBlock") return blocks.GetValueOrDefault(new(pos.X, pos.Y, pos.Z), air);
            throw new NotSupportedException(method.Name);
        });
        var world = PlacementApiProxy.Make<IClientWorldAccessor>((method, _) => method.Name == "get_BlockAccessor"
            ? accessor : throw new NotSupportedException(method.Name));
        BlockChangedDelegate? blockChanged = null;
        ChunkDirtyDelegate? chunkDirty = null;
        var events = PlacementApiProxy.Make<IClientEventAPI>((method, args) =>
        {
            switch (method.Name)
            {
                case "add_BlockChanged": blockChanged += (BlockChangedDelegate)args![0]!; break;
                case "remove_BlockChanged": blockChanged -= (BlockChangedDelegate)args![0]!; break;
                case "add_ChunkDirty": chunkDirty += (ChunkDirtyDelegate)args![0]!; break;
                case "remove_ChunkDirty": chunkDirty -= (ChunkDirtyDelegate)args![0]!; break;
                default: throw new NotSupportedException(method.Name);
            }
            return null;
        });
        var api = PlacementApiProxy.Make<ICoreClientAPI>((method, _) => method.Name switch
        {
            "get_World" => world,
            "get_Event" => events,
            _ => throw new NotSupportedException(method.Name)
        });
        using var sources = new StaticLightSources(api);
        var camera = new Vec3d();
        sources.Update(camera, view, projection);
        chunkDirty!(new Vec3i(0, 0, 0), chunk, EnumChunkDirtyReason.NewlyLoaded);
        sources.Update(camera, view, projection);
        check(sources.Sources.Count == 256 && sources.NewEmitters.Count == 0,
            "chunk discovery does not receive placement urgency");

        var queue = new PlacedLightPendingQueue();
        var residents = new Dictionary<StaticLightSources.Position, int>();
        // Consume every inspected entry as a resident, so shortlist capacity
        // cannot conceal starvation of sources beyond the first 64 positions.
        for (int frame = 0; frame < 4; frame++)
        {
            var movingView = (double[])view.Clone();
            movingView[12] += frame * 0.01;
            queue.Update(sources, camera, movingView, projection, residents, 99);
            for (int i = 0; i < 128; i++)
                if (queue.At(i).Occupied)
                    residents.TryAdd(StaticLightSources.Identity(queue.At(i).Source), i);
        }
        check(residents.Count == 256, "continuous movement completes bounded discovery beyond first 64 sources");
        residents.Clear(); queue.Clear();
        queue.Update(sources, camera, view, projection, residents, 100);
        var added = new StaticLightSources.Source(31, 0, 0, 0, 0, 20);
        blocks[StaticLightSources.Identity(added)] = emitter;
        var changedPos = new BlockPos(31, 0, 0);
        blockChanged!(changedPos, air);
        changedPos.Set(1000, 1000, 1000); // The event must copy engine-owned coordinates.
        sources.Update(camera, view, projection);
        check(sources.TryGet(added, out _) && sources.NewEmitters.Count == 1,
            "placement discovered before native metadata using copied coordinates");
        queue.Update(sources, camera, view, projection, residents, 100);
        int chosen = queue.ChoosePlacement();
        check(chosen >= 0 && queue.At(chosen).Source == added && queue.At(chosen).Immediate,
            "placement bypasses a discovery cursor with hundreds of preceding sources");
        check(queue.Count <= 128, "urgent admission preserves bounded shortlist");
        var farView = (double[])view.Clone();
        farView[12] = -31.5; farView[13] = -0.5; farView[14] = -100;
        queue.Update(sources, new Vec3d(31.5, 0.5, 100), farView, projection, residents, 100);
        check(queue.Choose(true, true, 100, new HashSet<StaticLightSources.Position>()) == queue.ChoosePlacement(),
            "far placement bypasses the full-cache two-second delay");

        blockChanged!(new BlockPos(0, 1, 0), air);
        sources.Update(camera, view, projection);
        check(sources.TryGet(added, out _) && sources.NewEmitters.Count == 0,
            "dirty scan retains a placed emitter missing from native light positions");
        blocks[StaticLightSources.Identity(added)] = new Block { LightHsv = new byte[] { 85, 4, 18 } };
        blockChanged!(new BlockPos(31, 0, 0), emitter);
        sources.Update(camera, view, projection);
        check(sources.TryGet(added, out var updated) && updated.Hue == 85 && sources.NewEmitters.Count == 0,
            "existing emitter metadata edits do not get another placement priority");
        queue.Update(sources, camera, view, projection, residents, 101);
        check(queue.At(queue.ChoosePlacement()).Source == updated,
            "queued placement priority survives a metadata refresh");

        blocks.Remove(StaticLightSources.Identity(added));
        blockChanged!(new BlockPos(31, 0, 0), emitter);
        sources.Update(camera, view, projection);
        queue.Update(sources, camera, view, projection, residents, 102);
        check(!sources.TryGet(added, out _) && queue.ChoosePlacement() == -1,
            "removed placement is dropped before it can bake");

        // A late native index entry must not duplicate an event-discovered light.
        blocks[StaticLightSources.Identity(added)] = emitter;
        blockChanged!(new BlockPos(31, 0, 0), air);
        sources.Update(camera, view, projection);
        positions.Add(31);
        blockChanged!(new BlockPos(0, 1, 0), air);
        sources.Update(camera, view, projection);
        check(sources.Sources.Count == 257, "native metadata catches up without duplicate emitter records");

        var full = new PlacedLightPolicy.Resident[128];
        for (int i = 0; i < full.Length; i++) full[i] = new(1, i + 10, false, i, 0, 0);
        check(PlacedLightPolicy.ChoosePlacementVictim(full, 1) == 0 &&
            PlacedLightPolicy.ChooseVictim(full, 1) == -1,
            "placement can reclaim an equal tier once while ordinary discovery stays stable");
        full[0] = new(1, 0, true, 0, 0, 0);
        check(PlacedLightPolicy.ChoosePlacementVictim(full, 1) == 1 &&
            PlacedLightPolicy.ChoosePlacementVictim(full, 2) == -1,
            "placement never replaces a protected or stronger resident");

        // Remote replication may omit the old block; urgency must not depend on
        // an actor identity or on the nullable event argument.
        blocks[new(30, 0, 0)] = emitter;
        blockChanged!(new BlockPos(30, 0, 0), null!);
        sources.Update(camera, view, projection);
        check(sources.NewEmitters.Count == 1 && sources.NewEmitters[0].X == 30,
            "client block notification works with no old-block or player identity");
        for (int i = 0; i < 80; i++)
        {
            int x = 16 + i % 8, z = i / 8;
            blocks[new(x, 0, z)] = emitter;
            blockChanged!(new BlockPos(x, 0, z), air);
        }
        sources.Update(camera, view, projection);
        check(sources.NewEmitters.Count == 64, "placement bursts honor the existing notification budget");
        sources.Update(camera, view, projection);
        check(sources.NewEmitters.Count == 16, "remaining placement notifications progress next frame");
    }
}

public class PlacementApiProxy : DispatchProxy
{
    private System.Func<MethodInfo, object?[]?, object?> _invoke = null!;
    protected override object? Invoke(MethodInfo? method, object?[]? args) => _invoke(method!, args);
    internal static T Make<T>(System.Func<MethodInfo, object?[]?, object?> invoke) where T : class
    {
        T api = Create<T, PlacementApiProxy>();
        ((PlacementApiProxy)(object)api)._invoke = invoke;
        return api;
    }
}
