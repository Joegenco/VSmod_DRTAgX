using System;

namespace DRTAgX;

/// <summary>Event-only exact density identity, independent of source order,
/// atlas slots, RGB and readiness. Scratch storage is reused after construction.</summary>
internal sealed class PlacedSourceGeometryRevision
{
    private readonly record struct Geometry(float X, float Y, float Z, float Reach) : IComparable<Geometry>
    {
        public int CompareTo(Geometry b)
        {
            int result = X.CompareTo(b.X);
            if (result == 0) result = Y.CompareTo(b.Y);
            if (result == 0) result = Z.CompareTo(b.Z);
            return result == 0 ? Reach.CompareTo(b.Reach) : result;
        }
    }

    private readonly Geometry[] _previous = new Geometry[StaticTerrainShadowMaps.MaxDepthSources];
    private readonly Geometry[] _scratch = new Geometry[StaticTerrainShadowMaps.MaxDepthSources];
    private int _count = -1;
    internal uint Revision { get; private set; } = 1;

    // Compare precisely the first vec4 consumed by the worker's density test.
    // Retain duplicates: candidate counting also counts records, not identities.
    internal bool Update(float[] records, int count, out bool wrapped)
    {
        if ((uint)count > (uint)_scratch.Length || records.Length < count * StaticLightGpuRecord.FloatCount)
            throw new ArgumentOutOfRangeException(nameof(count));
        for (int i = 0; i < count; ++i)
        {
            int at = i * StaticLightGpuRecord.FloatCount;
            _scratch[i] = new Geometry(records[at], records[at + 1], records[at + 2], records[at + 3]);
        }

        // The value-type comparable sort avoids creating a comparer delegate
        // on every event, while retaining the exact lexicographic ordering.
        Array.Sort(_scratch, 0, count);
        wrapped = false;
        if (_count == count && _scratch.AsSpan(0, count).SequenceEqual(_previous.AsSpan(0, count))) return false;
        Array.Copy(_scratch, _previous, count);
        _count = count;
        wrapped = Revision == uint.MaxValue;
        Revision = wrapped ? 1u : Revision + 1;
        return true;
    }
}