using System;
using OpenTK.Graphics.OpenGL4;

namespace DRTAgX;

// Render-thread-only timestamp ring. Disabled profiling performs no GL queries;
// enabled collection reads results only after the end timestamp is available.
internal sealed class GpuPassTimer : IDisposable
{
    private readonly int[] _start = new int[8], _end = new int[8];
    private readonly bool[] _pending = new bool[8];
    private readonly double[] _samples = new double[128], _sorted = new double[128];
    private int _next, _sample, _count;
    internal double LastMilliseconds { get; private set; }

    internal int Begin(bool enabled)
    {
        if (!enabled) return -1;
        Poll();
        int index = _next;
        if (_pending[index]) return -1; // Never stall the native render thread.
        if (_start[index] == 0) { _start[index] = GL.GenQuery(); _end[index] = GL.GenQuery(); }
        GL.QueryCounter(_start[index], QueryCounterTarget.Timestamp);
        _next = (index + 1) % _start.Length;
        return index;
    }

    internal void End(int index)
    {
        if (index < 0) return;
        GL.QueryCounter(_end[index], QueryCounterTarget.Timestamp);
        _pending[index] = true;
    }

    internal void Poll()
    {
        for (int i = 0; i < _pending.Length; ++i)
        {
            if (!_pending[i]) continue;
            GL.GetQueryObject(_end[i], GetQueryObjectParam.QueryResultAvailable, out int available);
            if (available == 0) continue;
            GL.GetQueryObject(_start[i], GetQueryObjectParam.QueryResult, out long start);
            GL.GetQueryObject(_end[i], GetQueryObjectParam.QueryResult, out long end);
            LastMilliseconds = Math.Max(0, end - start) / 1_000_000.0;
            _samples[_sample] = LastMilliseconds;
            _sample = (_sample + 1) % _samples.Length;
            _count = Math.Min(_count + 1, _samples.Length);
            _pending[i] = false;
        }
    }

    internal (double Median, double P95) Statistics()
    {
        if (_count == 0) return (0, 0);
        Array.Copy(_samples, _sorted, _count);
        Array.Sort(_sorted, 0, _count);
        return (_sorted[_count / 2], _sorted[Math.Min(_count - 1, (int)Math.Ceiling(_count * 0.95) - 1)]);
    }

    internal void ClearSamples() { _count = _sample = 0; LastMilliseconds = 0; }

    public void Dispose()
    {
        for (int i = 0; i < _start.Length; ++i)
        {
            if (_start[i] != 0) GL.DeleteQuery(_start[i]);
            if (_end[i] != 0) GL.DeleteQuery(_end[i]);
            _start[i] = _end[i] = 0; _pending[i] = false;
        }
        _next = 0; ClearSamples();
    }
}
