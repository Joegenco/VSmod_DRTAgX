using System;
using System.Numerics;
using System.Threading;
using Vintagestory.API.Client;
using Vintagestory.API.MathTools;

namespace DRTAgX;

/// <summary>Conservative visible-receiver demand, independent of lamp count or
/// depth-atlas readiness. GPU feedback contains world regions, never source IDs.</summary>
internal sealed partial class PlacedLightReceiverDemand : IDisposable
{
    internal const int Axis = 64, CellBlocks = 8, WordCount = Axis * Axis * Axis / 32;
    internal const int HeaderWords = 4, MaxBounds = 16384;
    internal const int BufferBytes = (HeaderWords + WordCount + MaxBounds * 8) * sizeof(uint);
    internal const int ScratchBytes = (WordCount + Axis * Axis * Axis * 6) * sizeof(uint);

    private const double ForwardGuard = 2;

    // A one-block cell centre is at most sqrt(3)/2 from the real camera.
    private const double ForwardGuardPadding = .8660254037844386;
    private readonly uint[] _bitmap = new uint[WordCount];
    private readonly uint[] _readback = new uint[HeaderWords + WordCount + MaxBounds * 8];
    private readonly float[] _inverseProjection = new float[16], _rotation = new float[16];
    private readonly double[] _inverseView = new double[16];
    private readonly PlacedLightViewState _submittedView = new();
    private readonly PlacedLightDemandCadence _cadence = new();
    private object? _world;
    private int _refresh = 1, _x, _y, _z, _minX, _minY, _minZ, _maxX, _maxY, _maxZ;
    private double _cameraX, _cameraY, _cameraZ;
    private int _nearX, _nearY, _nearZ;
    private bool _nearValid;
    private ulong _serial, _acceptedSerial;
    private bool _failed;
    internal bool SnapshotReady { get; private set; }
    internal int Revision { get; private set; }
    internal int OccupiedRegions { get; private set; }
    internal int Readbacks { get; private set; }
    internal int Submissions { get; private set; }
    internal int MotionSubmissions { get; private set; }
    internal int ImmediateSubmissions { get; private set; }
    internal int DeferredMotionFrames { get; private set; }
    internal uint InvalidPixels { get; private set; }
    internal uint OutsidePixels { get; private set; }
    internal uint ReceiverPixels { get; private set; }

    // Native block/chunk notifications may arrive away from the render thread.
    // They request a later dispatch; no GL object is touched by the callback.
    internal void RequestRefresh() => Interlocked.Exchange(ref _refresh, 1);
    internal void ForceRefresh() => RequestRefresh();

    private void SetNotReady()
    {
        if (!SnapshotReady) return;
        SnapshotReady = false;
        // Invalid feedback must retract old admission even when the camera and
        // old bitmap have not changed. Near-forward admission remains available.
        Revision++;
    }

    private void UpdateNearCamera(double x, double y, double z)
    {
        int cx = checked((int)Math.Floor(x)), cy = checked((int)Math.Floor(y)), cz = checked((int)Math.Floor(z));
        if (_nearValid && cx == _nearX && cy == _nearY && cz == _nearZ) return;
        _nearX = cx;
        _nearY = cy;
        _nearZ = cz;
        _nearValid = true;
        _cameraX = cx + .5;
        _cameraY = cy + .5;
        _cameraZ = cz + .5;
        // Source admission is constant within this cell. Re-publish only when
        // its discrete near region changes, even if the depth bitmap is empty.
        Revision++;
    }

    internal bool Allows(StaticLightSources.Source source)
    {
        double sx = source.X + .5,
            sy = source.Y + StaticTerrainShadowMaps.SourceHeight,
            sz = source.Z + .5,
            radius = PlacedLightView.Reach(source) + .125;
        // Late forward receivers are absent from opaque depth. Retain a small,
        // explicit near-camera sphere; this is not a global hidden-lamp preload.
        double dx = sx - _cameraX, dy = sy - _cameraY, dz = sz - _cameraZ;
        double nearRadius = radius + ForwardGuard + ForwardGuardPadding;
        if (_nearValid && dx * dx + dy * dy + dz * dz <= nearRadius * nearRadius) return true;
        if (!SnapshotReady || OccupiedRegions == 0) return false;
        return Intersects(sx - radius, sy - radius, sz - radius,
            sx + radius, sy + radius, sz + radius, sx, sy, sz, radius);
    }

    internal bool AllowsChunk(StaticLightSources.ChunkKey chunk, float maxReach)
    {
        double loX = chunk.X * 32.0, loY = chunk.Y * 32.0, loZ = chunk.Z * 32.0;
        double radius = Math.Max(0, maxReach) + .125;
        double dx = Math.Max(Math.Max(loX - _cameraX, _cameraX - loX - 32), 0),
            dy = Math.Max(Math.Max(loY - _cameraY, _cameraY - loY - 32), 0),
            dz = Math.Max(Math.Max(loZ - _cameraZ, _cameraZ - loZ - 32), 0);
        double nearRadius = radius + ForwardGuard + ForwardGuardPadding;
        if (_nearValid && dx * dx + dy * dy + dz * dz <= nearRadius * nearRadius) return true;
        if (!SnapshotReady || OccupiedRegions == 0) return false;
        // A chunk is only a discovery broad phase. Its entire source halo must
        // overlap demand; individual source spheres are checked after discovery.
        return Intersects(loX - radius, loY - radius, loZ - radius,
            loX + 32 + radius, loY + 32 + radius, loZ + 32 + radius,
            0, 0, 0, -1);
    }

    private bool Intersects(double loX, double loY, double loZ, double hiX, double hiY, double hiZ,
        double sx, double sy, double sz, double radius)
    {
        // Precise cell bounds come from every real receiver pixel. Only cells
        // omitted by bounded compaction retain the original coarse fallback.
        if (hiX < _boundsMinX || loX > _boundsMaxX || hiY < _boundsMinY || loY > _boundsMaxY ||
            hiZ < _boundsMinZ || loZ > _boundsMaxZ) return false;
        int x0 = Math.Clamp((int)Math.Floor(loX / 8) - _x - 1, _minX, _maxX),
            y0 = Math.Clamp((int)Math.Floor(loY / 8) - _y - 1, _minY, _maxY),
            z0 = Math.Clamp((int)Math.Floor(loZ / 8) - _z - 1, _minZ, _maxZ),
            x1 = Math.Clamp((int)Math.Floor(hiX / 8) - _x + 1, _minX, _maxX),
            y1 = Math.Clamp((int)Math.Floor(hiY / 8) - _y + 1, _minY, _maxY),
            z1 = Math.Clamp((int)Math.Floor(hiZ / 8) - _z + 1, _minZ, _maxZ);
        for (int z = z0; z <= z1; z++)
        for (int y = y0; y <= y1; y++)
        for (int x = x0; x <= x1; x++)
        {
            int cell = (z * Axis + y) * Axis + x;
            if ((_bitmap[cell >> 5] & (1u << (cell & 31))) == 0) continue;
            CellBounds(cell, out double bx0, out double by0, out double bz0,
                out double bx1, out double by1, out double bz1);
            if (radius < 0)
            {
                if (loX <= bx1 && hiX >= bx0 && loY <= by1 && hiY >= by0 && loZ <= bz1 && hiZ >= bz0) return true;
                continue;
            }

            double dx = Math.Max(Math.Max(bx0 - sx, sx - bx1), 0),
                dy = Math.Max(Math.Max(by0 - sy, sy - by1), 0),
                dz = Math.Max(Math.Max(bz0 - sz, sz - bz1), 0);
            if (dx * dx + dy * dy + dz * dz <= radius * radius) return true;
        }

        return false;
    }

    private void Accept(Slot slot)
    {
        if (slot.Serial <= _acceptedSerial || !ReferenceEquals(slot.World, _world)) return;
        _acceptedSerial = slot.Serial;
        bool ready = _readback[0] == 0;
        bool changed = SnapshotReady != ready || _x != slot.X || _y != slot.Y || _z != slot.Z;
        for (int i = 0; i < WordCount; i++)
            if (_bitmap[i] != _readback[i + HeaderWords])
            {
                changed = true;
                break;
            }

        Array.Copy(_readback, HeaderWords, _bitmap, 0, WordCount);
        changed |= AcceptBounds();
        _x = slot.X;
        _y = slot.Y;
        _z = slot.Z;
        InvalidPixels = _readback[0];
        OutsidePixels = _readback[1];
        ReceiverPixels = _readback[2];
        OccupiedRegions = 0;
        _boundsMinX = _boundsMinY = _boundsMinZ = double.PositiveInfinity;
        _boundsMaxX = _boundsMaxY = _boundsMaxZ = double.NegativeInfinity;
        _minX = _minY = _minZ = Axis;
        _maxX = _maxY = _maxZ = -1;
        for (int word = 0; word < WordCount; word++)
        {
            uint bits = _bitmap[word];
            while (bits != 0)
            {
                int cell = word * 32 + BitOperations.TrailingZeroCount(bits);
                int x = cell & 63, y = (cell >> 6) & 63, z = cell >> 12;
                _minX = Math.Min(_minX, x);
                _minY = Math.Min(_minY, y);
                _minZ = Math.Min(_minZ, z);
                _maxX = Math.Max(_maxX, x);
                _maxY = Math.Max(_maxY, y);
                _maxZ = Math.Max(_maxZ, z);
                CellBounds(cell, out double loX, out double loY, out double loZ,
                    out double hiX, out double hiY, out double hiZ);
                _boundsMinX = Math.Min(_boundsMinX, loX);
                _boundsMinY = Math.Min(_boundsMinY, loY);
                _boundsMinZ = Math.Min(_boundsMinZ, loZ);
                _boundsMaxX = Math.Max(_boundsMaxX, hiX);
                _boundsMaxY = Math.Max(_boundsMaxY, hiY);
                _boundsMaxZ = Math.Max(_boundsMaxZ, hiZ);
                OccupiedRegions++;
                bits &= bits - 1;
            }
        }

        if (OccupiedRegions == 0)
        {
            _boundsMinX = _boundsMinY = _boundsMinZ = 0;
            _boundsMaxX = _boundsMaxY = _boundsMaxZ = 0;
        }

        SnapshotReady = ready;
        if (changed) Revision++;
    }

}