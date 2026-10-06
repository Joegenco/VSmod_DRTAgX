using System;
using System.IO;
using DRTAgX;
using OpenTK.Graphics.OpenGL4;

internal static class GpuTimerProbe
{
    internal static void Run(string statsPath)
    {
        using var timer = new GpuPassTimer();
        if (timer.Begin(false) != -1) throw new Exception("Disabled timer issued queries");
        // Timestamp queries don't own a TimeElapsed target or alter draw state.
        int program = GL.GetInteger(GetPName.CurrentProgram);
        for (int i = 0; i < 24; ++i) { int sample = timer.Begin(true); timer.End(sample); }
        // Blocking completion is confined to this probe. Production polls only
        // QUERY_RESULT_AVAILABLE and drops samples rather than waiting for GPU.
        GL.Finish(); timer.Poll();
        var result = timer.Statistics();
        if (result.Median < 0 || result.P95 < result.Median || result.P95 > 1000 ||
            GL.GetInteger(GetPName.CurrentProgram) != program || GL.GetError() != ErrorCode.NoError)
            throw new Exception("GPU timestamp ring/state/statistics");
        timer.ClearSamples();
        if (timer.Statistics() != (0, 0)) throw new Exception("Timer retained old reporting samples");
        timer.Dispose(); timer.End(timer.Begin(false));
        Console.WriteLine("PASS opt-in GPU timestamps: ring saturation, completion, statistics, clear, disposal, draw state");
        using var occupancy = new GpuTileOccupancy();
        int masks = GL.GenBuffer(), generic = GL.GetInteger(GetPName.ShaderStorageBufferBinding);
        GL.GetInteger(GetIndexedPName.ShaderStorageBufferBinding, 5, out int old5);
        GL.GetInteger(GetIndexedPName.ShaderStorageBufferBinding, 7, out int old7);
        try
        {
            uint[] data = new uint[6 * 5];
            int[] counts = { 0, 1, 32, 64, 128, 129 };
            for (int t = 0; t < counts.Length; ++t)
                for (int b = 0; b < counts[t]; ++b) data[t * 5 + b / 32] |= 1u << (b & 31);
            GL.BindBuffer(BufferTarget.ShaderStorageBuffer, masks);
            GL.BufferData(BufferTarget.ShaderStorageBuffer, data.Length * 4, data, BufferUsageHint.StaticDraw);
            GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 5, masks);
            var api = ProbeAssets.Api(File.ReadAllBytes(statsPath));
            using (var borrowed = new BorrowedBufferRanges(BufferRangeTarget.ShaderStorageBuffer, 7))
            {
                for (int i = 0; i < 4; ++i) occupancy.Sample(api, 6);
                GL.Finish(); occupancy.Poll(); // Only the probe waits for completion.
                borrowed.AssertRestored("GPU mask occupancy");
            }
            GL.GetInteger(GetIndexedPName.ShaderStorageBufferBinding, 7, out int after7);
            if (Math.Abs(occupancy.Average - 59) > 1e-9 || occupancy.Maximum != 129 || after7 != old7 ||
                GL.GetInteger(GetPName.CurrentProgram) != program || GL.GetInteger(GetPName.ShaderStorageBufferBinding) != masks)
                throw new Exception("Asynchronous mask occupancy/state");
            if (GL.GetError() != ErrorCode.NoError) throw new Exception("Occupancy probe GL error");
            Console.WriteLine("PASS asynchronous bounded GPU mask diagnostics: actual counts, record 128, fence ring, GL state");
        }
        finally
        {
            GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 5, old5);
            GL.BindBuffer(BufferTarget.ShaderStorageBuffer, generic); GL.DeleteBuffer(masks);
        }
    }
}
