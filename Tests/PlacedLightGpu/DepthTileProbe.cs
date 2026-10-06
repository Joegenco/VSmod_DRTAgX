using System;
using System.IO;
using DRTAgX;
using OpenTK.Graphics.OpenGL4;
using Vintagestory.API.MathTools;

internal static class DepthTileProbe
{
    // Compare the production compute masks against every visible receiver,
    // rather than a second implementation of its conservative AABB test.
    internal static void Run(string path)
    {
        using var state = new ShadowGlState();
        using var owner = new StaticLightTileCompute();
        var api = ProbeAssets.Api(File.ReadAllBytes(path));
        int sources = GL.GenBuffer(), masks = GL.GenBuffer(), depth = GL.GenTexture();
        int sentinelTexture = GL.GenTexture(), sentinelSampler = GL.GenSampler();
        int active = GL.GetInteger(GetPName.ActiveTexture);
        GL.ActiveTexture(TextureUnit.Texture13);
        int previousTexture = GL.GetInteger(GetPName.TextureBinding2D);
        int previousSampler = GL.GetInteger(GetPName.SamplerBinding);
        GL.BindTexture(TextureTarget.Texture2D, sentinelTexture);
        GL.BindSampler(13, sentinelSampler);
        GL.ActiveTexture(TextureUnit.Texture2);
        try
        {
            var random = new Random(81733);
            foreach (var dimensions in new[] { (1, 1), (35, 19), (63, 65), (192, 108) })
            foreach (bool offAxis in new[] { false, true })
            {
                int width = dimensions.Item1, height = dimensions.Item2;
                int tileWidth = (width + 15) / 16, tiles = tileWidth * ((height + 15) / 16);
                float[] projection = Mat4f.Create(), inverse = new float[16];
                Mat4f.Perspective(projection, 1.5f, (float)width / height, 0.1f, 256f);
                if (offAxis) { projection[8] = 0.43f; projection[9] = -0.27f; }
                Mat4f.Invert(inverse, projection);
                float[] depths = new float[width * height];
                float[] receivers = new float[depths.Length * 3];
                for (int p = 0; p < depths.Length; ++p)
                {
                    // A thin diagonal, clear pixels, and a foreground/background
                    // discontinuity force mixed ranges and partial-edge tiles.
                    float z = p % 7 == 0 ? 0.15f : p % 3 == 0 ? 150f : 2f + (float)random.NextDouble() * 35f;
                    depths[p] = p % 11 == 0 && depths.Length > 1 ? 1f : (-projection[10] + projection[14] / z) * 0.5f + 0.5f;
                    float x = ((p % width + 0.5f) / width) * 2f - 1f;
                    float y = ((p / width + 0.5f) / height) * 2f - 1f;
                    float d = depths[p] * 2f - 1f;
                    float w = inverse[3] * x + inverse[7] * y + inverse[11] * d + inverse[15];
                    for (int c = 0; c < 3; ++c)
                        receivers[p * 3 + c] = (inverse[c] * x + inverse[4 + c] * y + inverse[8 + c] * d + inverse[12 + c]) / w;
                }
                float[] records = new float[129 * StaticLightGpuRecord.FloatCount];
                int[] rectangles = new int[129 * 4];
                for (int i = 0; i < 129; ++i)
                {
                    int p = random.Next(depths.Length), o = i * 16;
                    float radius = i % 7 == 0 ? 22f : 0.2f + (float)random.NextDouble() * 6f;
                    for (int c = 0; c < 3; ++c)
                        records[o + c] = receivers[p * 3 + c] + ((float)random.NextDouble() - 0.5f) * radius;
                    records[o + 3] = radius;
                    // Include a camera-inside sphere and a definitely remote one.
                    if (i == 0) { records[o] = records[o + 1] = records[o + 2] = 0; records[o + 3] = 22; }
                    if (i == 127) records[o] = 1e6f;
                    rectangles[i * 4 + 2] = tileWidth - 1;
                    rectangles[i * 4 + 3] = (height + 15) / 16 - 1;
                }
                GL.BindBuffer(BufferTarget.ShaderStorageBuffer, sources);
                GL.BufferData(BufferTarget.ShaderStorageBuffer, records.Length * 4, records, BufferUsageHint.StaticDraw);
                GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 4, sources);
                GL.BindBuffer(BufferTarget.ShaderStorageBuffer, masks);
                uint[] output = new uint[tiles * 5];
                Array.Fill(output, uint.MaxValue);
                GL.BufferData(BufferTarget.ShaderStorageBuffer, output.Length * 4, output, BufferUsageHint.DynamicDraw);
                GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 5, masks);
                GL.ActiveTexture(TextureUnit.Texture2);
                GL.BindTexture(TextureTarget.Texture2D, depth);
                // R32F permits injecting invalid values to test conservative
                // fallback; valid samples have the same sampler2D depth contract.
                GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.R32f, width, height, 0,
                    PixelFormat.Red, PixelType.Float, depths);
                GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
                GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
                int program = GL.GetInteger(GetPName.CurrentProgram);
                GL.GetInteger(GetIndexedPName.ShaderStorageBufferBinding, 6, out int binding6);
                void Dispatch()
                {
                    if (!owner.DispatchDepth(api, rectangles, 129, tileWidth, tiles, depth, inverse))
                        throw new Exception("Depth variant failed to compile");
                    if (GL.GetInteger(GetPName.CurrentProgram) != program || GL.GetInteger(GetPName.ActiveTexture) != (int)TextureUnit.Texture2 ||
                        GL.GetInteger(GetPName.ShaderStorageBufferBinding) != masks) throw new Exception("Depth compute leaked draw state");
                    GL.GetInteger(GetIndexedPName.ShaderStorageBufferBinding, 6, out int after6);
                    GL.ActiveTexture(TextureUnit.Texture13);
                    if (after6 != binding6 || GL.GetInteger(GetPName.TextureBinding2D) != sentinelTexture ||
                        GL.GetInteger(GetPName.SamplerBinding) != sentinelSampler) throw new Exception("Depth compute leaked unit 13/binding 6");
                    GL.ActiveTexture(TextureUnit.Texture2);
                    GL.MemoryBarrier(MemoryBarrierFlags.BufferUpdateBarrierBit | MemoryBarrierFlags.ShaderStorageBarrierBit);
                    GL.GetBufferSubData(BufferTarget.ShaderStorageBuffer, IntPtr.Zero, output.Length * 4, output);
                }
                Dispatch();
                int reached = 0;
                for (int p = 0; p < depths.Length; ++p)
                {
                    if (depths[p] == 1) continue;
                    int tile = (p / width / 16) * tileWidth + p % width / 16;
                    for (int i = 0; i < 129; ++i)
                    {
                        float d2 = 0;
                        for (int c = 0; c < 3; ++c) { float d = records[i * 16 + c] - receivers[p * 3 + c]; d2 += d * d; }
                        if (d2 > records[i * 16 + 3] * records[i * 16 + 3]) continue;
                        reached++;
                        if ((output[tile * 5 + (i >> 5)] & (1u << (i & 31))) == 0)
                            throw new Exception($"Lost visible receiver {width}x{height}, pixel {p}, record {i}");
                    }
                }
                for (int t = 0; t < tiles; ++t)
                    if ((output[t * 5 + 3] & 0x80000000u) != 0) throw new Exception("Remote sphere wasn't culled");
                if (width > 1 && reached == 0) throw new Exception("Reference didn't exercise any receivers");
                // Clear the same frame's texture; every stale mask must disappear.
                Array.Fill(depths, 1f);
                GL.TexSubImage2D(TextureTarget.Texture2D, 0, 0, 0, width, height, PixelFormat.Red, PixelType.Float, depths);
                Dispatch();
                foreach (uint word in output) if (word != 0) throw new Exception("Empty depth retained stale candidates");
                foreach (float invalid in new[] { float.NaN, float.PositiveInfinity, -0.1f, 1.1f })
                {
                    depths[0] = invalid;
                    GL.TexSubImage2D(TextureTarget.Texture2D, 0, 0, 0, width, height, PixelFormat.Red, PixelType.Float, depths);
                    Dispatch();
                    for (int w = 0; w < 5; ++w)
                        if (output[w] != (w == 4 ? 1u : uint.MaxValue)) throw new Exception("Invalid depth lost conservative fallback");
                }
                owner.Reset(); // Shader reload between differently sized grids.
                Console.WriteLine($"PASS depth cull: {width}x{height}, offAxis={offAxis}, {reached} receiver/light pairs; empty, invalid, resize, state");
            }
            // A depth-only compile error must leave the coarse path operational
            // and log once until reload. The actual production owner is exercised.
            int warnings = 0;
            var badApi = ProbeAssets.Api(System.Text.Encoding.UTF8.GetBytes(
                File.ReadAllText(path) + "\n#ifdef DRT_DEPTH_CULL\ninvalid_glsl_token\n#endif"), () => warnings++);
            using var fallback = new StaticLightTileCompute();
            if (fallback.DispatchDepth(badApi, new int[4], 0, 1, 1, depth, Mat4f.Create()) ||
                fallback.DispatchDepth(badApi, new int[4], 0, 1, 1, depth, Mat4f.Create()) || warnings != 1 || fallback.Failed)
                throw new Exception("Depth compile failure didn't retain coarse fallback");
            fallback.Dispatch(badApi, new int[4], 0, 1, 1);
            fallback.Reset();
            if (!fallback.DispatchDepth(api, new int[4], 0, 1, 1, depth, Mat4f.Create())) throw new Exception("Depth reload didn't recover");
            Console.WriteLine("PASS depth-only compile failure: one warning, coarse lighting retained, reload recovers");
            // Exercise the second workgroup dimension without allocating an 8K
            // texture. Zero sources must overwrite every tile, including >65535.
            uint[] largeGrid = new uint[65536 * 5]; Array.Fill(largeGrid, uint.MaxValue);
            GL.BindBuffer(BufferTarget.ShaderStorageBuffer, masks);
            GL.BufferData(BufferTarget.ShaderStorageBuffer, largeGrid.Length * 4, largeGrid, BufferUsageHint.DynamicDraw);
            owner.DispatchDepth(api, new int[4], 0, 65536, 65536, depth, Mat4f.Create());
            GL.MemoryBarrier(MemoryBarrierFlags.BufferUpdateBarrierBit | MemoryBarrierFlags.ShaderStorageBarrierBit);
            GL.GetBufferSubData(BufferTarget.ShaderStorageBuffer, IntPtr.Zero, largeGrid.Length * 4, largeGrid);
            foreach (uint mask in largeGrid) if (mask != 0) throw new Exception("Large-grid dispatch lost tail tiles");
            Console.WriteLine("PASS depth compute covers more than 65535 tiles using a 2D workgroup grid");
            if (GL.GetError() != ErrorCode.NoError) throw new Exception("Depth probe GL error");
        }
        finally
        {
            GL.ActiveTexture(TextureUnit.Texture13);
            GL.BindTexture(TextureTarget.Texture2D, previousTexture); GL.BindSampler(13, previousSampler);
            GL.ActiveTexture((TextureUnit)active);
            GL.DeleteSampler(sentinelSampler); GL.DeleteTexture(sentinelTexture); GL.DeleteTexture(depth);
            GL.DeleteBuffer(sources); GL.DeleteBuffer(masks);
        }
    }
}
