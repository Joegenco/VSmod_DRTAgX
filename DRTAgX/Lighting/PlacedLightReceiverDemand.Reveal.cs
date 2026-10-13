using System;
using System.Diagnostics;
using System.Numerics;
using Vintagestory.API.MathTools;

namespace DRTAgX;

internal sealed partial class PlacedLightReceiverDemand
{
    private bool RevealDue(bool changed, double[] view, Vec3d camera, long now)
    {
        if (!changed || !_submittedCameraValid || !SnapshotReady ||
            now - _lastReveal < Stopwatch.Frequency / 10) return false;
        double x = camera.X - _revealCameraX, y = camera.Y - _revealCameraY, z = camera.Z - _revealCameraZ;
        double facing = view[2] * _forwardX + view[6] * _forwardY + view[10] * _forwardZ;
        return x * x + y * y + z * z >= 1 || facing < .9781476007338057; // cos(12 degrees)
    }

    private void RememberRevealPose(double[] view, Vec3d camera, long now)
    {
        // Only whole-view (exact or sparse) observations advance this anchor.
        // Region maintenance must not erase cumulative slow walking/turning.
        _lastReveal = now;
        _revealCameraX = camera.X;
        _revealCameraY = camera.Y;
        _revealCameraZ = camera.Z;
        _forwardX = view[2];
        _forwardY = view[6];
        _forwardZ = view[10];
    }

    // Merge only on CPU publication, never back into the GPU cycle. Its next
    // exact completion therefore ages out all older history, even during an
    // uninterrupted walk. Fixed record/bitmap capacities bound storage.
    private void MergeAcceptedDemand(int originX, int originY, int originZ)
    {
        Array.Clear(_nextBoundsIndices);
        int count = (int)Math.Min(_readback[3], MaxBounds);
        for (int i = 0; i < count; ++i)
        {
            int at = HeaderWords + WordCount + i * 8;
            uint cell = _readback[at];
            if (cell < Axis * Axis * Axis) _nextBoundsIndices[cell] = (ushort)(i + 1);
        }

        for (int word = 0; word < WordCount; ++word)
        {
            uint bits = _bitmap[word];
            while (bits != 0)
            {
                int oldCell = word * 32 + BitOperations.TrailingZeroCount(bits);
                bits &= bits - 1;
                int x = (oldCell & 63) + _x - originX;
                int y = ((oldCell >> 6) & 63) + _y - originY;
                int z = (oldCell >> 12) + _z - originZ;
                if ((uint)x >= Axis || (uint)y >= Axis || (uint)z >= Axis) continue;
                int cell = (z * Axis + y) * Axis + x, bitmapAt = HeaderWords + (cell >> 5);
                uint bit = 1u << (cell & 31);
                bool alreadyOccupied = (_readback[bitmapAt] & bit) != 0;
                _readback[bitmapAt] |= bit;
                int previous = (_boundsIndices[oldCell] - 1) * 6;
                int record = _nextBoundsIndices[cell] - 1;
                if (previous < 0)
                {
                    // An earlier compaction overflow owns conservative whole-
                    // cell demand. A precise hint cannot narrow that coverage.
                    if (record >= 0) _readback[HeaderWords + WordCount + record * 8] = uint.MaxValue;
                    _nextBoundsIndices[cell] = 0;
                    continue;
                }

                if (record < 0)
                {
                    if (alreadyOccupied || count >= MaxBounds) continue;
                    record = count++;
                    int added = HeaderWords + WordCount + record * 8;
                    _readback[added] = (uint)cell;
                    for (int axis = 0; axis < 6; ++axis) _readback[added + 1 + axis] = _bounds[previous + axis];
                    _readback[added + 7] = 0;
                    _nextBoundsIndices[cell] = (ushort)(record + 1);
                    continue;
                }

                int destination = HeaderWords + WordCount + record * 8;
                for (int axis = 0; axis < 3; ++axis)
                {
                    _readback[destination + 1 + axis] =
                        Math.Min(_readback[destination + 1 + axis], _bounds[previous + axis]);
                    _readback[destination + 4 + axis] =
                        Math.Max(_readback[destination + 4 + axis], _bounds[previous + 3 + axis]);
                }
            }
        }

        _readback[3] = (uint)count;
    }
}