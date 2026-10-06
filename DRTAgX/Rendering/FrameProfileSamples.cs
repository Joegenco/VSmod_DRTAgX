using System;
using System.Diagnostics;
using OpenTK.Graphics.OpenGL4;

namespace DRTAgX;

/// <summary>Opt-in render-thread samples. Pending queries are never read synchronously.</summary>
internal sealed class FrameProfileSamples : IDisposable
{
    private const int Capacity = 32768, Ring = 16;
    private readonly int[] _queries = new int[Ring * 2];
    private readonly bool[] _pending = new bool[Ring];
    private readonly double[] _gpu = new double[Capacity], _cpu = new double[Capacity];
    private readonly double[] _scratch = new double[Capacity];
    private readonly long[] _ticks = new long[Ring];
    private readonly int[] _frames = new int[Ring];
    private readonly double[] _frameGpu = new double[Capacity], _frameCpu = new double[Capacity];
    private readonly bool[] _gpuSeen = new bool[Capacity], _cpuSeen = new bool[Capacity];
    private int _next, _gpuCount, _cpuCount;
    internal int Dropped { get; private set; }
    internal int Calls { get; private set; }
    internal FrameProfileSamples() => GL.GenQueries(_queries.Length, _queries);

    internal int Begin(int frame = 0)
    {
        Poll();
        int slot = _next;
        if (_pending[slot]) { ++Dropped; return -1; }
        // Allocate query names before collection starts; normal rendering never constructs this owner.
        if (_queries[slot * 2] == 0)
        {
            _queries[slot * 2] = GL.GenQuery();
            _queries[slot * 2 + 1] = GL.GenQuery();
        }
        _ticks[slot] = Stopwatch.GetTimestamp();
        _frames[slot] = frame;
        GL.QueryCounter(_queries[slot * 2], QueryCounterTarget.Timestamp);
        _next = (slot + 1) % Ring;
        return slot;
    }

    internal void End(int slot)
    {
        if (slot < 0) return;
        GL.QueryCounter(_queries[slot * 2 + 1], QueryCounterTarget.Timestamp);
        _pending[slot] = true;
        ++Calls;
        RecordCpu((Stopwatch.GetTimestamp() - _ticks[slot]) * 1000.0 / Stopwatch.Frequency, _frames[slot]);
    }

    internal void AddCpu(double milliseconds, int frame = 0)
    {
        ++Calls;
        RecordCpu(milliseconds, frame);
    }

    private void RecordCpu(double milliseconds, int frame)
    {
        if (_cpuCount < Capacity) _cpu[_cpuCount++] = milliseconds;
        if ((uint)frame < Capacity) { _frameCpu[frame] += milliseconds; _cpuSeen[frame] = true; }
    }

    internal void Poll()
    {
        for (int i = 0; i < Ring; ++i)
        {
            if (!_pending[i]) continue;
            GL.GetQueryObject(_queries[i * 2 + 1], GetQueryObjectParam.QueryResultAvailable, out int available);
            if (available == 0) continue;
            GL.GetQueryObject(_queries[i * 2], GetQueryObjectParam.QueryResult, out long start);
            GL.GetQueryObject(_queries[i * 2 + 1], GetQueryObjectParam.QueryResult, out long end);
            double milliseconds = Math.Max(0, end - start) / 1_000_000.0;
            if (_gpuCount < Capacity) _gpu[_gpuCount++] = milliseconds;
            int frame = _frames[i];
            if ((uint)frame < Capacity) { _frameGpu[frame] += milliseconds; _gpuSeen[frame] = true; }
            _pending[i] = false;
        }
    }

    // Repeated calls, including no-op relight callbacks, are summed within each frame.
    // These are inclusive totals; nested scopes still must never be added together.
    internal object Summary() => new { Calls, Dropped, Gpu = Percentiles(_gpu, _gpuCount), Cpu = Percentiles(_cpu, _cpuCount),
        FrameGpu = FramePercentiles(_frameGpu, _gpuSeen), FrameCpu = FramePercentiles(_frameCpu, _cpuSeen) };

    private object FramePercentiles(double[] values, bool[] seen)
    {
        int count = 0;
        for (int i = 0; i < Capacity; ++i) if (seen[i]) _scratch[count++] = values[i];
        return Percentiles(_scratch, count);
    }

    private object Percentiles(double[] values, int count)
    {
        if (count == 0) return new { Count = 0, P50 = 0d, P95 = 0d, P99 = 0d, P999 = 0d };
        Array.Copy(values, _scratch, count);
        Array.Sort(_scratch, 0, count);
        double At(double fraction) => _scratch[Math.Clamp((int)Math.Ceiling(count * fraction) - 1, 0, count - 1)];
        return new { Count = count, P50 = At(.5), P95 = At(.95), P99 = At(.99), P999 = At(.999) };
    }

    internal void Clear()
    {
        _gpuCount = _cpuCount = Calls = Dropped = 0;
        Array.Clear(_frameGpu); Array.Clear(_frameCpu); Array.Clear(_gpuSeen); Array.Clear(_cpuSeen);
    }

    public void Dispose()
    {
        for (int i = 0; i < _queries.Length; ++i)
        {
            if (_queries[i] != 0) GL.DeleteQuery(_queries[i]);
            _queries[i] = 0;
        }
        Array.Clear(_pending);
        Clear();
    }
}
