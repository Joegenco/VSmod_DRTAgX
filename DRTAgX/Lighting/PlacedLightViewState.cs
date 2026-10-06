using System;

namespace DRTAgX;

// Native matrices are reused in place. Snapshot their values, rather than
// comparing array identities or treating every render frame as camera movement.
internal sealed class PlacedLightViewState
{
    private readonly double[] _view = new double[16];
    private readonly float[] _projection = new float[16];
    private bool _valid;

    internal bool Update(double[] view, float[] projection)
    {
        bool changed = !_valid;
        for (int i = 0; i < 16 && !changed; i++)
            changed = _view[i] != view[i] || _projection[i] != projection[i];
        if (!changed) return false;
        Array.Copy(view, _view, 16); Array.Copy(projection, _projection, 16);
        _valid = true;
        return true;
    }

    internal void Reset() => _valid = false;
}
