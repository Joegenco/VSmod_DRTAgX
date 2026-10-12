using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace DRTAgX;

/// <summary>
/// Client-side spatial index for placed light emitters across loaded chunks within player reach.
/// Manages incremental chunk scanning (16 chunks/frame budget), listens for BlockChanged/ChunkDirty events,
/// and aggregates candidate emitting voxels for the static shadow atlas and deferred lighting shaders.
/// </summary>
internal sealed partial class StaticLightSources : IDisposable
{
    internal readonly record struct Source(int X, int Y, int Z, byte Hue, byte Saturation, byte Level);

    internal readonly record struct ChunkKey(int X, int Y, int Z);

    // Level zero marks an absent endpoint. Camera rebucketing and depth-slot
    // relocation never produce these semantic source-content notifications.
    internal readonly record struct SourceChange(Source Previous, Source Current);

    private readonly List<SourceChange> _sourceChanges = new(256);
    internal IReadOnlyList<SourceChange> SourceChanges => _sourceChanges;
    internal int SourceContentRevision { get; private set; }

    private const int RadiusInChunks = 5; // 128-block receiver region plus the 22-block source halo.
    private const int ScanBudgetPerFrame = 16;
    private const double ScanWorkBudgetMilliseconds = 0.25;
    private long _scanWorkStarted;
    private int _scanLane;

    private bool ContinueScanWork => ChunkScansThisFrame == 0 ||
                                     Stopwatch.GetElapsedTime(_scanWorkStarted).TotalMilliseconds <
                                     ScanWorkBudgetMilliseconds;

    private readonly ICoreClientAPI _api;
    private const int CachedChunkLimit = 4096;
    private readonly PlacedLightNotifications _notifications = new();
    private readonly Dictionary<ChunkKey, CachedChunk> _byChunk = new();
    private readonly ConditionalWeakTable<IWorldChunk, ChunkCoordinates> _chunkCoordinates = new();
    private readonly LinkedList<ChunkKey> _cacheLru = new();
    private readonly HashSet<ChunkKey> _activeChunks = new(1331);
    private readonly HashSet<ChunkKey> _missingChunks = new();
    private readonly HashSet<ChunkKey> _dirty = new();
    private readonly HashSet<ChunkKey> _retryNextFrame = new();
    private readonly HashSet<ChunkKey> _scannedThisFrame = new();
    private readonly HashSet<ChunkKey> _residentChunks = new(128);
    private readonly List<Source> _sources = new();
    private readonly List<ChunkKey> _sourceChunks = new();
    private readonly List<ChunkKey> _changedChunks = new();
    private readonly List<Position> _blockEdits = new(64);
    private readonly HashSet<ChunkKey> _changedSet = new();
    private readonly List<ChunkKey> _processedDirty = new();
    private readonly List<ChunkKey> _staleChunks = new();
    private readonly List<Source> _scanSources = new();
    private readonly List<Source> _newEmitters = new(64);
    private readonly HashSet<Position> _scanPositions = new(64);
    private readonly Dictionary<Position, Source> _readEmitters = new(64);
    private readonly HashSet<Position> _failedQueries = new(64);
    private readonly List<ChunkKey> _scanOrder = new();
    private readonly BlockPos _scanOrigin = new(0);
    private readonly BlockPos _scanPos = new(0);
    private int[] _candidateBuffer = new int[64];

    private static readonly Comparison<Source> SourceOrderComparison = CompareSources;

    // Deduplicated view-tier queues replace repeated sorting of the entire halo.
    private readonly Queue<ChunkKey>[] _discovery = { new(1331), new(1331), new(1331) };
    private readonly HashSet<ChunkKey> _queued = new(1331);
    private readonly Dictionary<Position, Source> _byPosition = new(256);
    private int _outstanding;
    private bool _rebucket;
    private long _nextDiscoveryRebucket;

    internal readonly record struct Position(int X, int Y, int Z);

    internal static Position Identity(Source source) => new(source.X, source.Y, source.Z);

    internal bool TryGet(Source source, out Source current)
    {
        // A retained coordinate is not proof its native chunk is still loaded.
        ChunkKey key = new(source.X >> 5, source.Y >> 5, source.Z >> 5);
        current = default;
        return _byChunk.TryGetValue(key, out var cached) && cached.Chunk is { Disposed: false } &&
               _byPosition.TryGetValue(Identity(source), out current);
    }

    private object? _world;
    private ChunkKey _center;
    private bool _hasCenter, _needsFlatten, _hasInitialCoverage;
    private bool _hasOrderCamera;
    private double _orderCameraX, _orderCameraY, _orderCameraZ;
    internal int WorldGeneration { get; private set; }
    internal int Revision { get; private set; }
    internal int ViewRevision { get; private set; }
    internal int ChunkScansThisFrame { get; private set; }
    internal int EmitterQueriesThisFrame { get; private set; }
    internal int NotificationsThisFrame { get; private set; }
    internal int CachedChunks => _byChunk.Count;
    internal bool NativeGeometryNotifications { get; set; }
    private readonly PlacedLightViewState _viewState = new();
    private double[] _view = Array.Empty<double>();
    private float[] _projection = Array.Empty<float>();
    private Vec3d _camera = new();

    // Keep only compact positions/HSV and a weak native identity. Cold entries
    // must not extend the lifetime of native chunk blocks or mesh resources.
    private sealed class ChunkCoordinates
    {
        internal readonly ChunkKey Key;
        internal ChunkCoordinates(ChunkKey key) => Key = key;
    }

    private sealed class CachedChunk
    {
        internal readonly List<Source> Sources = new();
        private WeakReference<IWorldChunk>? _identity;

        internal IWorldChunk? Chunk
        {
            get => _identity != null && _identity.TryGetTarget(out var chunk) ? chunk : null;
            set
            {
                if (_identity == null && value != null) _identity = new(value);
                else _identity?.SetTarget(value!);
            }
        }

        internal LinkedListNode<ChunkKey> Node = null!;
        internal bool Indexed;
        internal int EmptyIndex = -1;
    }

    private static int CompareSources(Source a, Source b)
    {
        int x = a.X.CompareTo(b.X), y = a.Y.CompareTo(b.Y);
        return x != 0 ? x : y != 0 ? y : a.Z.CompareTo(b.Z);
    }

    internal IReadOnlyList<Source> Sources => _sources;
    internal IReadOnlyList<Source> NewEmitters => _newEmitters;
    internal IReadOnlyList<ChunkKey> ChangedChunks => _changedChunks;
    internal IReadOnlyList<Position> BlockEdits => _blockEdits;
    internal bool IsScanComplete => _hasInitialCoverage;

    internal float ScanProgress => _scanOrder.Count == 0
        ? 0f
        : Math.Clamp((_scanOrder.Count - _outstanding) / (float)_scanOrder.Count, 0f, 1f);

    internal void SetResidentSources(IReadOnlyList<StaticTerrainShadowMaps.ActiveLight> lights)
    {
        _residentChunks.Clear();
        // Index the interface directly; foreach would box List<T>'s enumerator.
        for (int i = 0; i < lights.Count; i++)
        {
            var light = lights[i];
            if (light.Slot >= 0 && !light.ReplacementPair)
                _residentChunks.Add(new ChunkKey(light.Source.X >> 5, light.Source.Y >> 5, light.Source.Z >> 5));
        }
    }

    internal StaticLightSources(ICoreClientAPI api)
    {
        _api = api;
        api.Event.BlockChanged += OnBlockChanged;
        api.Event.ChunkDirty += OnChunkDirty;
    }

    private void OnBlockChanged(BlockPos position, Block oldBlock)
    {
        // Callbacks only copy coordinates; block and per-instance light queries
        // remain on the render thread, including edits received from other players.
        Position edit = new(position.X, position.InternalY, position.Z);
        _receiverDemand?.RequestRefresh();
        RememberPendingGeometryEdit(edit);
        _notifications.Position(edit);
    }

    private void OnChunkDirty(Vec3i coordinate, IWorldChunk chunk, EnumChunkDirtyReason reason)
    {
        // Lighting-only updates still change candidate metadata, including an
        // initially empty LightPositions set. They do not imply changed depth.
        byte flags = PlacedLightNotifications.Metadata;
        if (reason is EnumChunkDirtyReason.NewlyCreated or EnumChunkDirtyReason.NewlyLoaded)
            flags |= PlacedLightNotifications.Loaded;
        _notifications.Chunk(new ChunkKey(coordinate.X, coordinate.Y, coordinate.Z), flags);
    }

    internal void OnGeometryUploaded(IWorldChunk chunk)
    {
        // A block callback can precede tessellation. Refresh demand again when
        // changed geometry is actually uploaded, avoiding the settled-view
        // polling interval before receivers on the new mesh become eligible.
        _receiverDemand?.RequestRefresh();
        _notifications.Uploaded(chunk);
    }

    // Called only on the render thread, before preparing static-light GPU data.
    internal void Update(Vec3d cameraWorld, double[] view, float[] projection)
    {
        long startTicks = Stopwatch.GetTimestamp();
        ChunkScansThisFrame = EmitterQueriesThisFrame = NotificationsThisFrame = 0;
        _view = view;
        _projection = projection;
        _camera = cameraWorld;
        bool demandChanged = DemandChanged();
        _changedChunks.Clear();
        _blockEdits.Clear();
        _newEmitters.Clear();
        _sourceChanges.Clear();
        _scannedThisFrame.Clear();
        _changedSet.Clear();
        _readEmitters.Clear();
        _failedQueries.Clear();
        ChunkKey center = new((int)Math.Floor(cameraWorld.X) >> 5,
            (int)Math.Floor(cameraWorld.Y) >> 5, (int)Math.Floor(cameraWorld.Z) >> 5);
        var world = _api.World;
        // Native ordinary dimensions occupy distinct 32768-block internal-Y
        // bands. Coordinates already identify them; a vertical teleport is not
        // proof the world changed and must not destroy completed residency.
        if (!ReferenceEquals(_world, world))
        {
            ResetWorld(world);
            ClearPendingGeometryEdits();
        }

        if (_viewState.Update(view, projection))
        {
            ViewRevision++;
            if (_outstanding > 0) _rebucket = true;
        }

        double moveX = cameraWorld.X - _orderCameraX,
            moveY = cameraWorld.Y - _orderCameraY,
            moveZ = cameraWorld.Z - _orderCameraZ;
        if (demandChanged || !_hasCenter || center != _center || !_hasOrderCamera ||
            moveX * moveX + moveY * moveY + moveZ * moveZ >= 16.0)
            ReconcileNeighborhood(center, cameraWorld);

        DrainNotifications();
        ValidateResidentChunks();
        ValidateKnownSourceChunks();
        ValidateEmptyChunks();
        // While an initial pass is unfinished, frequent camera turns may reorder
        // it at most four times/second. Settled areas do not rebucket at all.
        if (_rebucket && startTicks >= _nextDiscoveryRebucket)
        {
            RebucketDiscovery();
            _nextDiscoveryRebucket = startTicks + Stopwatch.Frequency / 4;
        }

        int remaining = ScanBudgetPerFrame;
        _scanWorkStarted = Stopwatch.GetTimestamp();
        // Rotate the first work unit: one slow native lookup/lock must not
        // permanently starve edits, first discovery or missing-data recovery.
        int lane = _scanLane;
        _scanLane = (_scanLane + 1) % 3;
        if (lane == 2) remaining -= RetryActiveMissing(remaining);
        if (lane == 0 && ScanFirstUnknown()) remaining--;
        _processedDirty.Clear();
        foreach (ChunkKey dirty in _dirty)
        {
            // Keep dormant metadata notifications for revisits, but never
            // spend a visible edit's scan budget on unrequested cave chunks.
            if (!IsChunkDemanded(dirty)) continue;
            if (remaining == 0 || _processedDirty.Count >= 8 || !ContinueScanWork) break;
            if (!_scannedThisFrame.Contains(dirty))
            {
                Scan(dirty);
                remaining--;
            }

            _processedDirty.Add(dirty);
        }

        // Do not mutate the set during enumeration above.
        foreach (ChunkKey processed in _processedDirty) _dirty.Remove(processed);

        if (lane != 0 && ContinueScanWork && ScanFirstUnknown()) remaining--;
        if (lane != 2) remaining -= RetryActiveMissing(remaining);

        while (remaining > 0 && ContinueScanWork && ScanFirstUnknown()) remaining--;
        _hasInitialCoverage = _outstanding == 0;
        // A concurrent LightPositions edit must retry without replacing the
        // previous source list or changing a set during its enumeration.
        _dirty.UnionWith(_retryNextFrame);
        _retryNextFrame.Clear();
        TrimCache();
        RefreshKnownSources();
        if (!_needsFlatten)
        {
            return;
        }

        _sources.Clear();
        _sourceChunks.Clear();
        foreach (ChunkKey key in _scanOrder)
        {
            if (!_byChunk.TryGetValue(key, out var cached) || !cached.Indexed || cached.Sources.Count == 0) continue;
            _sourceChunks.Add(key);
            foreach (Source source in cached.Sources)
                if (IsDemanded(source))
                    _sources.Add(source);
        }

        Revision++;
        _needsFlatten = false;
    }

    private bool InRange(ChunkKey key)
    {
        // Reject boxes outside the spherical 150-block discovery halo.
        double x = Math.Max(key.X * 32 - _camera.X, _camera.X - (key.X + 1) * 32);
        double y = Math.Max(key.Y * 32 - _camera.Y, _camera.Y - (key.Y + 1) * 32);
        double z = Math.Max(key.Z * 32 - _camera.Z, _camera.Z - (key.Z + 1) * 32);
        x = Math.Max(x, 0);
        y = Math.Max(y, 0);
        z = Math.Max(z, 0);
        return Math.Abs(key.X - _center.X) <= RadiusInChunks &&
               Math.Abs(key.Y - _center.Y) <= RadiusInChunks &&
               Math.Abs(key.Z - _center.Z) <= RadiusInChunks &&
               x * x + y * y + z * z <= PlacedLightView.DiscoveryRange * PlacedLightView.DiscoveryRange;
    }

    private double ChunkDistance(ChunkKey key)
    {
        double x = key.X * 32 + 16 - _camera.X,
            y = key.Y * 32 + 16 - _camera.Y,
            z = key.Z * 32 + 16 - _camera.Z;
        return x * x + y * y + z * z;
    }

    private int ChunkPriority(ChunkKey key, double distance)
    {
        if (distance < 48 * 48) return 0;
        int viewClass = PlacedLightView.Project(key.X * 32 + 16, key.Y * 32 + 16,
            key.Z * 32 + 16, 28f, _view, _projection).Class;
        return viewClass == 0 ? 0 : viewClass == 1 ? 1 : 2;
    }

    private bool ScanFirstUnknown()
    {
        foreach (var queue in _discovery)
            while (queue.TryDequeue(out ChunkKey key))
            {
                if (!IsChunkDemanded(key) || Indexed(key) || _missingChunks.Contains(key) ||
                    _scannedThisFrame.Contains(key)) continue;
                Scan(key);
                return true;
            }

        return false;
    }

    private bool TryReadEmitter(Position position, out Source source)
    {
        // A placement and a metadata scan can touch the same emitter this frame.
        // Read its current instance once, including a cached non-emitting result.
        if (_readEmitters.TryGetValue(position, out source)) return source.Level > 0;
        EmitterQueriesThisFrame++;
        source = default;
        _scanPos.SetAndCorrectDimension(position.X, position.Y, position.Z);
        try
        {
            var accessor = _api.World.BlockAccessor;
            byte[]? hsv = accessor.GetBlock(_scanPos).GetLightHsv(accessor, _scanPos);
            if (hsv is not { Length: >= 3 } || hsv[2] == 0)
            {
                _readEmitters[position] = default;
                return false;
            }

            source = new Source(position.X, position.Y, position.Z,
                hsv[0], hsv[1], (byte)Math.Min((int)hsv[2], 31));
            _readEmitters[position] = source;
            return true;
        }
        catch (Exception)
        {
            // A transient/modded query failure is not proof of source removal.
            _failedQueries.Add(position);
            _readEmitters[position] = default;
            return false;
        }
    }

    private void UpdateChangedEmitter(ChunkKey key, Position position)
    {
        _unconfirmedRemovals.Remove(position);
        _byChunk.TryGetValue(key, out var cached);
        var known = cached?.Sources;
        int index = -1;
        if (known != null)
            for (int i = 0; i < known.Count; i++)
                if (Identity(known[i]) == position)
                {
                    index = i;
                    break;
                }

        Source previousSource = index >= 0 ? known![index] : default;
        if (TryReadEmitter(position, out var source))
        {
            if (index >= 0)
            {
                if (known![index] == source) return;
                known[index] = source;
            }
            else
            {
                if (known == null) known = Cached(key).Sources;
                // Position-only order is stable through HSV changes. Insert one
                // edited source instead of sorting the chunk after every event.
                InsertSource(known, source);
                _newEmitters.Add(source);
            }
        }
        else if (index >= 0 && !_failedQueries.Contains(position)) known!.RemoveAt(index);
        else
        {
            if (_failedQueries.Contains(position)) RetryScan(key);
            return;
        }

        _byPosition.Remove(position);
        if (source.Level > 0) _byPosition[position] = source;
        Revision++;
        SourceChanged(previousSource, source);
        _needsFlatten = true;
        if (_byChunk.TryGetValue(key, out cached)) TrackEmptyChunk(key, cached);
    }

    private void Scan(ChunkKey key)
    {
        ChunkScansThisFrame++;
        _scannedThisFrame.Add(key);
        if (_queued.Remove(key)) _outstanding--;
        _scanSources.Clear();
        _scanPositions.Clear();
        _scanOrigin.SetAndCorrectDimension(key.X << 5, key.Y << 5, key.Z << 5);
        IWorldChunk? chunk;
        try
        {
            chunk = _api.World.BlockAccessor.GetChunkAtBlockPos(_scanOrigin);
        }
        catch (Exception)
        {
            // An accessor failure is not an unload. Preserve known positions
            // and retry the snapshot within the existing discovery budget.
            RetryScan(key);
            return;
        }

        bool wasMissing = _missingChunks.Contains(key);
        bool wasKnown = Indexed(key);
        if (chunk is { Disposed: false })
        {
            _missingChunks.Remove(key);
            int candidateCount = 0;
            chunk.AcquireBlockReadLock();
            try
            {
                HashSet<int>? positions = chunk.LightPositions;
                if (positions != null)
                {
                    // CopyTo does not validate HashSet's mutation version. An
                    // asynchronous add could otherwise silently truncate a new
                    // emitter to a previously read Count. Enumerate the concrete
                    // set without boxing, retaining the old snapshot on mutation.
                    if (_candidateBuffer.Length < positions.Count)
                        _candidateBuffer = new int[Math.Max(positions.Count, _candidateBuffer.Length * 2)];
                    foreach (int position in positions)
                    {
                        if (candidateCount == _candidateBuffer.Length)
                            Array.Resize(ref _candidateBuffer, _candidateBuffer.Length * 2);
                        _candidateBuffer[candidateCount++] = position;
                    }

                    // Modern .NET permits removal during enumeration. Count
                    // drift still requires a fresh snapshot on the next frame.
                    if (candidateCount != positions.Count)
                    {
                        RetryScan(key);
                        return;
                    }
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
            {
                // The block read lock does not protect LightPositions from
                // asynchronous lighting updates. Keep the old snapshot.
                RetryScan(key);
                return;
            }
            finally
            {
                chunk.ReleaseBlockReadLock();
            }

            for (int c = 0; c < candidateCount; c++)
            {
                int local = _candidateBuffer[c];
                if ((uint)local >= 32768u) continue;
                Position position = new(_scanOrigin.X + (local & 31),
                    _scanOrigin.InternalY + (local >> 10), _scanOrigin.Z + ((local >> 5) & 31));
                _scanPositions.Add(position);
                if (TryReadIndexedEmitter(key, position, out var source)) _scanSources.Add(source);
            }

            // The asynchronous native light index can lag a placement. Revalidate
            // known emitters absent from that index rather than erasing a new light.
            if (_byChunk.TryGetValue(key, out var known))
                foreach (var previous in known.Sources)
                {
                    Position position = Identity(previous);
                    if (!_scanPositions.Contains(position) && TryReadIndexedEmitter(key, position, out var source))
                        _scanSources.Add(source);
                }
        }
        else
        {
            _missingChunks.Add(key);
            RemoveCached(key);
            if (wasKnown) Changed(key); // Unloaded caster-only chunks also invalidate nearby depth.
            return;
        }

        _scanSources.Sort(SourceOrderComparison);
        CachedChunk entry = Cached(key);
        var oldSources = entry.Sources;
        bool sourceChanged = oldSources.Count != _scanSources.Count;
        if (!sourceChanged)
            for (int i = 0; i < _scanSources.Count; i++)
                if (_scanSources[i] != oldSources[i])
                {
                    sourceChanged = true;
                    break;
                }

        if ((wasMissing || !wasKnown) && chunk is { Disposed: false })
            Changed(key); // Newly indexed terrain may already occlude a cached light.
        IWorldChunk? previousChunk = entry.Chunk;
        if (!ReferenceEquals(previousChunk, chunk))
        {
            Changed(key); // The same coordinates can now belong to a new loaded chunk.
            if (previousChunk != null) _chunkCoordinates.Remove(previousChunk);
            entry.Chunk = chunk;
            if (chunk != null)
            {
                _chunkCoordinates.Remove(chunk);
                _chunkCoordinates.Add(chunk, new ChunkCoordinates(key));
            }
        }

        entry.Indexed = true;
        if (sourceChanged)
        {
            // Coordinate-sorted lists merge in linear time without closures,
            // temporary maps or a camera-dependent ordering.
            int oldIndex = 0, newIndex = 0;
            while (oldIndex < oldSources.Count || newIndex < _scanSources.Count)
            {
                Source previous = oldIndex < oldSources.Count ? oldSources[oldIndex] : default;
                Source next = newIndex < _scanSources.Count ? _scanSources[newIndex] : default;
                int order = oldIndex == oldSources.Count ? 1 :
                    newIndex == _scanSources.Count ? -1 : CompareSources(previous, next);
                if (order <= 0) oldIndex++;
                if (order >= 0) newIndex++;
                SourceChanged(order <= 0 ? previous : default, order >= 0 ? next : default);
            }

            foreach (Source previous in oldSources) _byPosition.Remove(Identity(previous));
            oldSources.Clear();
            oldSources.AddRange(_scanSources);
            foreach (Source source in oldSources) _byPosition[Identity(source)] = source;
            Revision++;
            _needsFlatten = true;
        }

        TrackEmptyChunk(key, entry);
    }

    public void Dispose()
    {
        _api.Event.BlockChanged -= OnBlockChanged;
        _api.Event.ChunkDirty -= OnChunkDirty;
        ClearPendingGeometryEdits();
        _byChunk.Clear();
        _chunkCoordinates.Clear();
        _cacheLru.Clear();
        _byPosition.Clear();
        _sourceDemand.Clear();
        _notifications.Clear();
        _sources.Clear();
        _sourceChunks.Clear();
        _emptyChunks.Clear();
        _emptyCursor = 0;
        _blockEdits.Clear();
        _unconfirmedRemovals.Clear();
    }
}