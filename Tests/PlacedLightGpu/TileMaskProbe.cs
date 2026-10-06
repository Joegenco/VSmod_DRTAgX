using System;
using System.IO;
using OpenTK.Graphics.OpenGL4;

internal static class TileMaskProbe
{
    internal static void Run(string path)
    {
        using var owner = new DRTAgX.StaticLightTileCompute();
        var api = ProbeAssets.Api(File.ReadAllBytes(path));
        int rectanglesBuffer = GL.GenBuffer(), masksBuffer = GL.GenBuffer();
        const int width = 3, tiles = 6, sources = 129;
        int[] rectangles = new int[sources * 4];
        for (int i = 0; i < sources; i++)
        {
            rectangles[i * 4] = rectangles[i * 4 + 1] = 1;
            rectangles[i * 4 + 2] = rectangles[i * 4 + 3] = 0;
        }
        foreach (int i in new[] { 0, 31, 32, 63, 64, 95, 96, 127, 128 })
        {
            rectangles[i * 4] = i % 3;
            rectangles[i * 4 + 1] = i % 2;
            rectangles[i * 4 + 2] = 2;
            rectangles[i * 4 + 3] = 1;
        }
        GL.GetInteger(GetIndexedPName.ShaderStorageBufferBinding, 5, out int previous5);
        GL.GetInteger(GetIndexedPName.ShaderStorageBufferBinding, 6, out int previous6);
        int previous = GL.GetInteger(GetPName.CurrentProgram), generic = GL.GetInteger(GetPName.ShaderStorageBufferBinding);
        int alignment = GL.GetInteger(GetPName.ShaderStorageBufferOffsetAlignment);
        try
        {
            GL.BindBuffer(BufferTarget.ShaderStorageBuffer, rectanglesBuffer);
            GL.BufferData(BufferTarget.ShaderStorageBuffer, alignment + rectangles.Length * sizeof(int), IntPtr.Zero, BufferUsageHint.StaticDraw);
            GL.BindBufferRange(BufferRangeTarget.ShaderStorageBuffer, 6, rectanglesBuffer, (IntPtr)alignment, (IntPtr)(rectangles.Length * sizeof(int)));
            GL.BindBuffer(BufferTarget.ShaderStorageBuffer, masksBuffer);
            // Poison output to prove every invocation overwrites empty words.
            uint[] actual = new uint[tiles * 5];
            Array.Fill(actual, uint.MaxValue);
            GL.BufferData(BufferTarget.ShaderStorageBuffer, actual.Length * sizeof(uint), actual, BufferUsageHint.DynamicDraw);
            GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 5, masksBuffer);
            owner.Dispatch(api, rectangles, sources, width, tiles);
            GL.GetInteger(GetIndexedPName.ShaderStorageBufferBinding, 6, out int restored6);
            GL.GetInteger64(GetIndexedPName.ShaderStorageBufferStart, 6, out long restoredOffset);
            GL.GetInteger64(GetIndexedPName.ShaderStorageBufferSize, 6, out long restoredSize);
            if (restored6 != rectanglesBuffer || GL.GetInteger(GetPName.ShaderStorageBufferBinding) != masksBuffer ||
                restoredOffset != alignment || restoredSize != rectangles.Length * sizeof(int) ||
                GL.GetInteger(GetPName.CurrentProgram) != previous) throw new Exception("Compute owner leaked program/binding 6/generic buffer");
            // Readback is confined to this correctness probe, never runtime culling.
            GL.MemoryBarrier(MemoryBarrierFlags.BufferUpdateBarrierBit | MemoryBarrierFlags.ShaderStorageBarrierBit);
            GL.GetBufferSubData(BufferTarget.ShaderStorageBuffer, IntPtr.Zero, actual.Length * sizeof(uint), actual);
            for (int tile = 0; tile < tiles; tile++)
                for (int word = 0; word < 5; word++)
                {
                    uint expected = 0;
                    for (int i = word * 32; i < Math.Min(sources, word * 32 + 32); i++)
                        if (tile % width >= rectangles[i * 4] && tile / width >= rectangles[i * 4 + 1] &&
                            tile % width <= rectangles[i * 4 + 2] && tile / width <= rectangles[i * 4 + 3])
                            expected |= 1u << (i & 31);
                    if (actual[tile * 5 + word] != expected) throw new Exception($"Tile mask mismatch: {tile}, word {word}");
                }
            owner.Dispatch(api, rectangles, 0, width, tiles);
            GL.MemoryBarrier(MemoryBarrierFlags.BufferUpdateBarrierBit | MemoryBarrierFlags.ShaderStorageBarrierBit);
            GL.GetBufferSubData(BufferTarget.ShaderStorageBuffer, IntPtr.Zero, actual.Length * sizeof(uint), actual);
            foreach (uint mask in actual) if (mask != 0) throw new Exception("Empty dispatch retained old masks");
            owner.Reset();
            owner.Dispatch(api, rectangles, sources, width, tiles);
            Console.WriteLine("PASS production compute owner: binding restoration and reload");
            Console.WriteLine("PASS compute masks: word boundaries, record 128, empty rectangles and stale-word clearing");
        }
        finally
        {
            GL.UseProgram(previous);
            GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 5, previous5);
            GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 6, previous6);
            GL.BindBuffer(BufferTarget.ShaderStorageBuffer, generic);
            GL.DeleteBuffer(rectanglesBuffer);
            GL.DeleteBuffer(masksBuffer);
        }
    }
}
