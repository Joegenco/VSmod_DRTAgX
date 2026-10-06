using System;
using DRTAgX;
using OpenTK.Graphics.OpenGL4;

/// <summary>Foreign subranges plus a different generic binding, checked through real GL queries.</summary>
internal sealed class BorrowedBufferRanges : IDisposable
{
    private readonly BufferRangeTarget _target;
    private readonly BufferTarget _genericTarget;
    private readonly int[] _indices;
    private readonly IndexedBufferState[] _before;
    private readonly int _previousGeneric, _buffer, _generic, _alignment;
    private const int Size = 512;

    internal BorrowedBufferRanges(BufferRangeTarget target, params int[] indices)
    {
        _target = target; _indices = indices;
        bool uniform = target == BufferRangeTarget.UniformBuffer;
        _genericTarget = uniform ? BufferTarget.UniformBuffer : BufferTarget.ShaderStorageBuffer;
        _previousGeneric = GL.GetInteger(uniform ? GetPName.UniformBufferBinding : GetPName.ShaderStorageBufferBinding);
        _alignment = GL.GetInteger(uniform ? GetPName.UniformBufferOffsetAlignment : GetPName.ShaderStorageBufferOffsetAlignment);
        _before = new IndexedBufferState[indices.Length];
        _buffer = GL.GenBuffer();
        GL.BindBuffer(_genericTarget, _buffer);
        GL.BufferData(_genericTarget, _alignment * (indices.Length + 1) + Size, IntPtr.Zero, BufferUsageHint.StaticDraw);
        for (int i = 0; i < indices.Length; i++)
        {
            _before[i] = IndexedBufferState.Capture(target, indices[i]);
            GL.BindBufferRange(target, indices[i], _buffer, (IntPtr)(_alignment * (i + 1)), (IntPtr)Size);
        }
        _generic = GL.GenBuffer();
        GL.BindBuffer(_genericTarget, _generic);
        GL.BufferData(_genericTarget, Size, IntPtr.Zero, BufferUsageHint.StaticDraw);
    }

    internal void AssertRestored(string operation)
    {
        bool uniform = _target == BufferRangeTarget.UniformBuffer;
        for (int i = 0; i < _indices.Length; i++)
        {
            GL.GetInteger(uniform ? GetIndexedPName.UniformBufferBinding : GetIndexedPName.ShaderStorageBufferBinding, _indices[i], out int name);
            GL.GetInteger64(uniform ? GetIndexedPName.UniformBufferStart : GetIndexedPName.ShaderStorageBufferStart, _indices[i], out long offset);
            GL.GetInteger64(uniform ? GetIndexedPName.UniformBufferSize : GetIndexedPName.ShaderStorageBufferSize, _indices[i], out long size);
            if (name != _buffer || offset != _alignment * (i + 1) || size != Size)
                throw new Exception(operation + " widened or replaced borrowed range " + _indices[i]);
        }
        if (GL.GetInteger(uniform ? GetPName.UniformBufferBinding : GetPName.ShaderStorageBufferBinding) != _generic)
            throw new Exception(operation + " changed the incoming generic binding");
    }

    public void Dispose()
    {
        foreach (var state in _before) state.Restore();
        GL.BindBuffer(_genericTarget, _previousGeneric);
        GL.DeleteBuffer(_buffer); GL.DeleteBuffer(_generic);
    }
}
