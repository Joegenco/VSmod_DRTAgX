using System;
using System.Text;
using OpenTK.Graphics.OpenGL4;
using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace DRTAgX;

// Render-thread-only, opt-in bounded mask sampling. A fence guards each small
// readback; a busy GPU drops samples rather than blocking the native renderer.
internal sealed class GpuTileOccupancy : IDisposable
{
    private readonly int[] _buffers = new int[3], _counts = new int[3];
    private readonly IntPtr[] _fences = new IntPtr[3];
    private readonly uint[] _scratch = new uint[128];
    private int _program, _next, _offset;
    private bool _failed;
    internal double Average { get; private set; }
    internal int Maximum { get; private set; }

    internal void Sample(ICoreClientAPI api, int tiles)
    {
        if (tiles <= 0 || _failed) return;
        Poll();
        int index = _next;
        if (_fences[index] != IntPtr.Zero) return;
        if (_program == 0)
        {
            int shader = GL.CreateShader(ShaderType.ComputeShader), program = 0;
            try
            {
                GL.ShaderSource(shader, Encoding.UTF8.GetString(api.Assets.Get(new AssetLocation("drtagx", "shaders/staticlighttilestats.csh")).Data));
                GL.CompileShader(shader); GL.GetShader(shader, ShaderParameter.CompileStatus, out int compiled);
                if (compiled == 0) throw new InvalidOperationException(GL.GetShaderInfoLog(shader));
                program = GL.CreateProgram(); GL.AttachShader(program, shader); GL.LinkProgram(program);
                GL.GetProgram(program, GetProgramParameterName.LinkStatus, out int linked);
                if (linked == 0) throw new InvalidOperationException(GL.GetProgramInfoLog(program));
                _program = program; program = 0;
            }
            catch (Exception ex) { _failed = true; api.Logger.Warning("[DRT AgX] GPU mask diagnostics unavailable: {0}", ex.Message); return; }
            finally { GL.DeleteShader(shader); if (program != 0) GL.DeleteProgram(program); }
        }
        int previous = GL.GetInteger(GetPName.CurrentProgram), generic = GL.GetInteger(GetPName.ShaderStorageBufferBinding);
        var previous7 = IndexedBufferState.Capture(BufferRangeTarget.ShaderStorageBuffer, 7);
        try
        {
            if (_buffers[index] == 0)
            {
                _buffers[index] = GL.GenBuffer(); GL.BindBuffer(BufferTarget.ShaderStorageBuffer, _buffers[index]);
                GL.BufferData(BufferTarget.ShaderStorageBuffer, _scratch.Length * sizeof(uint), IntPtr.Zero, BufferUsageHint.StreamRead);
            }
            int count = Math.Min(tiles, _scratch.Length); _counts[index] = count;
            GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 7, _buffers[index]);
            GL.UseProgram(_program);
            GL.Uniform1(GL.GetUniformLocation(_program, "tileCount"), tiles);
            GL.Uniform1(GL.GetUniformLocation(_program, "sampleCount"), count);
            GL.Uniform1(GL.GetUniformLocation(_program, "sampleOffset"), _offset % tiles);
            GL.MemoryBarrier(MemoryBarrierFlags.ShaderStorageBarrierBit);
            GL.DispatchCompute((count + 63) / 64, 1, 1);
            GL.MemoryBarrier(MemoryBarrierFlags.BufferUpdateBarrierBit);
            _fences[index] = GL.FenceSync(SyncCondition.SyncGpuCommandsComplete, WaitSyncFlags.None);
            _next = (index + 1) % _buffers.Length; _offset = (_offset + 17) % tiles;
        }
        finally
        {
            previous7.Restore();
            GL.BindBuffer(BufferTarget.ShaderStorageBuffer, generic); GL.UseProgram(previous);
        }
    }

    internal void Poll()
    {
        for (int i = 0; i < _fences.Length; ++i)
        {
            if (_fences[i] == IntPtr.Zero) continue;
            var status = GL.ClientWaitSync(_fences[i], ClientWaitSyncFlags.None, 0);
            if (status != WaitSyncStatus.AlreadySignaled && status != WaitSyncStatus.ConditionSatisfied) continue;
            int generic = GL.GetInteger(GetPName.ShaderStorageBufferBinding);
            try
            {
                GL.BindBuffer(BufferTarget.ShaderStorageBuffer, _buffers[i]);
                GL.GetBufferSubData(BufferTarget.ShaderStorageBuffer, IntPtr.Zero, _counts[i] * sizeof(uint), _scratch);
                uint sum = 0, maximum = 0;
                for (int j = 0; j < _counts[i]; ++j) { sum += _scratch[j]; maximum = Math.Max(maximum, _scratch[j]); }
                Average = (double)sum / _counts[i]; Maximum = (int)maximum;
            }
            finally { GL.BindBuffer(BufferTarget.ShaderStorageBuffer, generic); GL.DeleteSync(_fences[i]); _fences[i] = IntPtr.Zero; }
        }
    }

    public void Dispose()
    {
        for (int i = 0; i < _buffers.Length; ++i)
        {
            if (_fences[i] != IntPtr.Zero) GL.DeleteSync(_fences[i]);
            if (_buffers[i] != 0) GL.DeleteBuffer(_buffers[i]);
            _fences[i] = IntPtr.Zero; _buffers[i] = 0;
        }
        if (_program != 0) GL.DeleteProgram(_program);
        _program = _next = _offset = 0; _failed = false; Average = 0; Maximum = 0;
    }
}
