using System;
using System.Collections.Generic;
using Vintagestory.API.MathTools;

namespace DRTAgX;

// Bounded render-thread shortlist: discovery can contain thousands of emitters,
// but only 64 unassigned sources are inspected and 128 queued entries refreshed.
internal sealed class PlacedLightPendingQueue
{
    internal struct Entry
    {
        internal StaticLightSources.Source Source;
        internal PlacedLightView.Bounds Bounds;
        internal double Distance, Since, RecheckAt;
        internal int Priority;
        internal bool Occupied, Immediate;
    }

    private readonly Entry[] _entries = new Entry[128];
    private readonly Dictionary<StaticLightSources.Position, int> _indices = new(128);
    private int _cursor, _remaining, _sourceRevision = -1, _residentCount = -1, _residentRevision = -1;
    private readonly PlacedLightViewState _viewState = new();
    internal int ResidencyRevision { get; set; }
    internal int WeakestResidentPriority { get; set; } = int.MaxValue;
    internal bool ViewCullingEnabled { get; set; } = true;
    private bool _lastViewCulling = true;
    private double _now;
    internal int InspectedThisFrame { get; private set; }
    internal bool DiscoveryPending => _remaining > 0;
    internal int Count => _indices.Count;
    internal int VisibleCount
    {
        get { int count = 0; foreach (var entry in _entries)
            if (entry.Occupied && entry.Priority < PlacedLightPolicy.Offscreen) count++; return count; }
    }
    internal ref Entry At(int index) => ref _entries[index];

    internal void Clear()
    {
        Array.Clear(_entries);
        _indices.Clear();
        _cursor = 0;
        _remaining = 0; _sourceRevision = _residentCount = _residentRevision = -1;
        _viewState.Reset();
    }

    internal void Remove(int index)
    {
        _indices.Remove(StaticLightSources.Identity(_entries[index].Source));
        _entries[index] = default;
    }

    internal void Update(StaticLightSources sources, Vec3d camera, double[] view, float[] projection,
        Dictionary<StaticLightSources.Position, int> residents, double now)
    {
        InspectedThisFrame = 0;
        _now = now;
        bool geometryChanged = sources.BlockEdits.Count > 0 || sources.ChangedChunks.Count > 0;
        bool viewChanged = _viewState.Update(view, projection);
        // A style-panel toggle wakes the bounded discovery pass even with a stationary camera.
        viewChanged |= _lastViewCulling != ViewCullingEnabled;
        _lastViewCulling = ViewCullingEnabled;
        bool sourceChanged = _sourceRevision != sources.Revision;
        bool residentChanged = _residentRevision != ResidencyRevision || _residentCount != residents.Count;
        if (!viewChanged && !sourceChanged && !residentChanged && !geometryChanged &&
            _remaining == 0 && sources.NewEmitters.Count == 0)
            return;
        // Never rewind a bounded pass on camera motion: it would inspect the
        // same first 64 sources forever. A list revision restarts the amount
        // of work, while the rotating cursor wraps against the current count.
        if (sourceChanged || viewChanged)
            _remaining = sources.Sources.Count;
        else if (residentChanged && residents.Count <= _residentCount)
            _remaining = Math.Max(_remaining, sources.Sources.Count);
        _sourceRevision = sources.Revision;
        _residentRevision = ResidencyRevision; _residentCount = residents.Count;
        for (int i = 0; i < _entries.Length; i++)
        {
            ref Entry entry = ref _entries[i];
            if (!entry.Occupied) continue;
            if (residents.ContainsKey(StaticLightSources.Identity(entry.Source)) ||
                !sources.TryGet(entry.Source, out var current)) { Remove(i); continue; }
            entry.Source = current;
            if (viewChanged || sourceChanged)
            {
                entry.Distance = Distance(current, camera);
                if (!InRange(current, entry.Distance)) { Remove(i); continue; }
                entry.Bounds = PlacedLightView.ForShadowCulling(
                    PlacedLightView.Project(current, view, projection), ViewCullingEnabled);
                entry.Priority = PlacedLightPolicy.Priority(entry.Bounds, entry.Distance);
            }
        }
        // Placement events bypass the rotating discovery cursor. Only the source
        // index's bounded notification batch can add this one-time priority.
        for (int i = 0; i < sources.NewEmitters.Count; i++)
        {
            var added = sources.NewEmitters[i];
            if (sources.TryGet(added, out var current))
                Enqueue(current, camera, view, projection, residents, now, true);
        }
        int count = Math.Min(64, Math.Min(_remaining, sources.Sources.Count));
        for (int n = 0; n < count; n++)
        {
            if (_cursor >= sources.Sources.Count) _cursor = 0;
            var source = sources.Sources[_cursor++];
            InspectedThisFrame++; _remaining--;
            Enqueue(source, camera, view, projection, residents, now, false);
        }
        if (geometryChanged)
            for (int i = 0; i < _entries.Length; i++)
            {
                ref Entry entry = ref _entries[i];
                if (!entry.Occupied) continue;
                // A cancelled replacement retains its candidate but waits for
                // usable caster geometry before another six-face transaction.
                bool uploaded = PlacedLightGeometry.ChunksTouch(entry.Source, sources.ChangedChunks);
                if (uploaded || PlacedLightGeometry.EditsTouch(entry.Source, sources.BlockEdits, entry.Immediate))
                    entry.RecheckAt = uploaded && sources.NativeGeometryNotifications ? 0 :
                        now + PlacedLightGeometry.EditFallbackSeconds;
            }
    }

    private void Enqueue(StaticLightSources.Source source, Vec3d camera, double[] view, float[] projection,
        Dictionary<StaticLightSources.Position, int> residents, double now, bool immediate)
    {
        var key = StaticLightSources.Identity(source);
        if (residents.ContainsKey(key)) return;
        if (_indices.TryGetValue(key, out int existing))
        { _entries[existing].Immediate |= immediate; return; }
        double distance = Distance(source, camera);
        if (!InRange(source, distance)) return;
        var bounds = PlacedLightView.ForShadowCulling(
            PlacedLightView.Project(source, view, projection), ViewCullingEnabled);
        int free = -1, worst = -1;
        for (int i = 0; i < _entries.Length; i++)
        {
            if (!_entries[i].Occupied) { free = i; break; }
            if (worst < 0 || Worse(_entries[i], _entries[worst])) worst = i;
        }
        var incoming = new Entry { Source = source, Distance = distance, Bounds = bounds,
            Priority = PlacedLightPolicy.Priority(bounds, distance), Since = now,
            Occupied = true, Immediate = immediate };
        if (free < 0)
        {
            if (!Worse(_entries[worst], incoming)) return;
            free = worst;
            Remove(free);
        }
        _entries[free] = incoming;
        _indices.Add(key, free);
    }

    internal int ChoosePlacement()
    {
        int chosen = -1;
        for (int i = 0; i < _entries.Length; i++)
            if (_entries[i].Occupied && _entries[i].Immediate && _now >= _entries[i].RecheckAt &&
                WeakestResidentPriority >= _entries[i].Priority &&
                (chosen < 0 || BakeEarlier(_entries[i], _entries[chosen]))) chosen = i;
        return chosen;
    }

    internal int Choose(bool visible, bool full, double now, HashSet<StaticLightSources.Position> suppressed)
    {
        int chosen = -1;
        for (int i = 0; i < _entries.Length; i++)
        {
            ref Entry entry = ref _entries[i];
            if (!entry.Occupied || now < entry.RecheckAt) continue;
            if (full && (entry.Immediate ? WeakestResidentPriority < entry.Priority :
                WeakestResidentPriority <= entry.Priority)) continue;
            bool onScreen = entry.Priority < PlacedLightPolicy.Offscreen;
            if (onScreen) suppressed.Remove(StaticLightSources.Identity(entry.Source));
            if (onScreen != visible || !visible && suppressed.Contains(StaticLightSources.Identity(entry.Source)) ||
                !entry.Immediate && !PlacedLightPolicy.CanAdmit(entry.Distance, full, entry.Since, now)) continue;
            if (chosen < 0 || BakeEarlier(entry, _entries[chosen])) chosen = i;
        }
        return chosen;
    }

    internal static bool InRange(StaticLightSources.Source source, double distanceSquared)
    {
        double reach = PlacedLightView.ReceiverRange + PlacedLightView.Reach(source);
        return distanceSquared <= reach * reach;
    }

    internal static double Distance(StaticLightSources.Source source, Vec3d camera)
    {
        double x = source.X + 0.5 - camera.X, y = source.Y + StaticTerrainShadowMaps.SourceHeight - camera.Y,
            z = source.Z + 0.5 - camera.Z;
        return x * x + y * y + z * z;
    }

    private static bool Worse(Entry a, Entry b) => a.Immediate != b.Immediate ? !a.Immediate : a.Priority > b.Priority ||
        a.Priority == b.Priority && (a.Distance > b.Distance || a.Distance == b.Distance &&
            (a.Source.X > b.Source.X || a.Source.X == b.Source.X &&
                (a.Source.Y > b.Source.Y || a.Source.Y == b.Source.Y && a.Source.Z > b.Source.Z)));

    private static bool BakeEarlier(Entry a, Entry b)
    {
        if (a.Immediate != b.Immediate) return a.Immediate;
        bool visibleA = a.Priority < PlacedLightPolicy.Offscreen, visibleB = b.Priority < PlacedLightPolicy.Offscreen;
        if (visibleA != visibleB) return visibleA;
        if (a.Distance != b.Distance) return a.Distance < b.Distance;
        return Worse(b, a); // Stable priority/coordinate tie-break for equal distance.
    }
}
