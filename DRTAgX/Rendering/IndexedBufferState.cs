using System;
using OpenTK.Graphics.OpenGL4;

namespace DRTAgX;

// An indexed binding can be a subrange. BindBufferBase alone cannot restore its
// offset/size; keep the full binding contract while borrowing UBO/SSBO indices.
internal readonly struct IndexedBufferState
{
    private readonly int _name, _index;
    private readonly long _offset, _size;
    private readonly BufferRangeTarget _target;

    private IndexedBufferState(BufferRangeTarget target, int index, int name, long offset, long size)
    {
        _target = target;
        _index = index;
        _name = name;
        _offset = offset;
        _size = size;
    }

    // Render-thread callers capture before borrowing an indexed UBO/SSBO slot.
    // Offsets and sizes are 64-bit GL values, even when buffer names are 32-bit.
    internal static IndexedBufferState Capture(BufferRangeTarget target, int index)
    {
        bool uniform = target == BufferRangeTarget.UniformBuffer;
        GL.GetInteger(uniform ? GetIndexedPName.UniformBufferBinding : GetIndexedPName.ShaderStorageBufferBinding,
            index, out int name);
        GL.GetInteger64(uniform ? GetIndexedPName.UniformBufferStart : GetIndexedPName.ShaderStorageBufferStart,
            index, out long offset);
        GL.GetInteger64(uniform ? GetIndexedPName.UniformBufferSize : GetIndexedPName.ShaderStorageBufferSize,
            index, out long size);
        return new(target, index, name, offset, size);
    }

    internal void Restore()
    {
        // Both calls also change the generic binding for this target. The owner
        // restores its saved generic binding after all indexed slots are restored.
        if (_name != 0 && _size > 0)
            GL.BindBufferRange(_target, _index, _name, (IntPtr)_offset, (IntPtr)_size);
        else
            GL.BindBufferBase(_target, _index, _name);
    }
}
