using System;

namespace DRTAgX;

internal sealed partial class PlacedLightReceiverDemand
{
    private const double BoundsGuard = 1.5;
    private ushort[] _boundsIndices = new ushort[Axis * Axis * Axis],
        _nextBoundsIndices = new ushort[Axis * Axis * Axis];
    private uint[] _bounds = new uint[MaxBounds * 6], _nextBounds = new uint[MaxBounds * 6];
    private double _boundsMinX, _boundsMinY, _boundsMinZ, _boundsMaxX, _boundsMaxY, _boundsMaxZ;
    internal int CompactedRegions { get; private set; }

    private bool AcceptBounds()
    {
        Array.Clear(_nextBoundsIndices);
        bool changed = false;
        CompactedRegions = 0;
        int count = (int)Math.Min(_readback[3], MaxBounds);
        for (int i = 0; i < count; i++)
        {
            int at = HeaderWords + WordCount + i * 8;
            uint cell = _readback[at];
            if (cell >= Axis * Axis * Axis || (_bitmap[cell >> 5] & (1u << ((int)cell & 31))) == 0) continue;
            bool valid = true;
            for (int axis = 0; axis < 3; axis++)
                if (_readback[at + 1 + axis] > _readback[at + 4 + axis] || _readback[at + 4 + axis] > 128) valid = false;
            if (!valid) continue; // A malformed record keeps safe coarse demand.
            int destination = CompactedRegions * 6, old = (_boundsIndices[cell] - 1) * 6;
            for (int axis = 0; axis < 6; axis++)
            {
                uint value = _readback[at + 1 + axis];
                _nextBounds[destination + axis] = value;
                if (old < 0 || _bounds[old + axis] != value) changed = true;
            }
            _nextBoundsIndices[cell] = checked((ushort)(++CompactedRegions));
        }
        // Atomic compaction order is intentionally irrelevant to revisions.
        // Missing precise records remain represented by the complete bitmap.
        for (int word = 0; word < WordCount; word++)
        {
            uint bits = _bitmap[word];
            while (bits != 0)
            {
                int cell = word * 32 + System.Numerics.BitOperations.TrailingZeroCount(bits);
                if (_boundsIndices[cell] != 0 && _nextBoundsIndices[cell] == 0) changed = true;
                bits &= bits - 1;
            }
        }
        (_boundsIndices, _nextBoundsIndices) = (_nextBoundsIndices, _boundsIndices);
        (_bounds, _nextBounds) = (_nextBounds, _bounds);
        return changed;
    }

    private void CellBounds(int cell, out double x0, out double y0, out double z0,
        out double x1, out double y1, out double z1)
    {
        double x = (_x + (cell & 63)) * 8.0,
            y = (_y + ((cell >> 6) & 63)) * 8.0, z = (_z + (cell >> 12)) * 8.0;
        int at = (_boundsIndices[cell] - 1) * 6;
        if (at < 0)
        {
            // Overflow never drops a receiver: retain the original entire-cell
            // plus one-cell conservative guard for uncompacted cell identities.
            x0 = x - 8; y0 = y - 8; z0 = z - 8;
            x1 = x + 16; y1 = y + 16; z1 = z + 16;
            return;
        }
        // Quantized minima round down and maxima round up at 1/16 block.
        // The small extra guard covers asynchronous motion and D24 rounding.
        x0 = x + _bounds[at] / 16.0 - BoundsGuard;
        y0 = y + _bounds[at + 1] / 16.0 - BoundsGuard;
        z0 = z + _bounds[at + 2] / 16.0 - BoundsGuard;
        x1 = x + _bounds[at + 3] / 16.0 + BoundsGuard;
        y1 = y + _bounds[at + 4] / 16.0 + BoundsGuard;
        z1 = z + _bounds[at + 5] / 16.0 + BoundsGuard;
    }
}
