using System;
using DRTAgX;

/// <summary>Live light-table calibration and allocation-free optional provider access.</summary>
internal static class FogEnvironmentProbe
{
    // Public members deliberately match the verified optional-mod contracts.
    public sealed class Provider { public Client Client { get; set; } }
    public sealed class Client { public Config Config { get; set; } }
    public sealed class Config { public bool Enabled; public int MaxViewDistance, FarViewDistance; }

    internal static float[] LightTable()
    {
        float[] table = new float[32];
        // A nonlinear table with a nonzero floor catches accidental raw-level
        // fractions, gamma conversion and failure to subtract native darkness.
        for (int level = 0; level < table.Length; level++) table[level] = .04f + .9f * MathF.Pow(level / 31f, 2.2f);
        return table;
    }

    internal static void Run()
    {
        void Check(bool valid, string label) { if (!valid) throw new Exception(label); }
        float[] table = LightTable();
        float threshold = AtmosphereFogEnvironment.NormalizeSunlight(table, 31, AtmosphereFogEnvironment.FullSunlightLevel);
        Check(AtmosphereFogEnvironment.FullSunlightLevel == 1 && threshold > 0 && threshold < .04f, "sun1 uses live nonlinear radiance");
        Check(AtmosphereFogEnvironment.NormalizeSunlight(table, 31, 0) == 0, "native light-table darkness is exactly zero");
        Check(AtmosphereFogEnvironment.Exposure(threshold, threshold) == 1, "sunlight one restores full fog");
        Check(AtmosphereFogEnvironment.NormalizeSunlight(table, 18, 18) == 1, "custom maximum sunlight is respected");
        Check(AtmosphereFogEnvironment.NormalizeSunlight([], 31, 18) == 0, "empty startup table is finite");
        float last = -1;
        for (int level = 0; level < 32; level++) {
            float e = AtmosphereFogEnvironment.Exposure(AtmosphereFogEnvironment.NormalizeSunlight(table, 31, level), threshold);
            Check(float.IsFinite(e) && e >= last && e >= 0 && e <= 1, "shelter exposure is monotonic/bounded");
            last = e;
        }

        var provider = new Provider();
        Func<float> chunkRange = LodFogRange.CreateGetter(provider, "MaxViewDistance");
        Func<float> radius = LodFogRange.CreateGetter(provider, "FarViewDistance");
        Check(chunkRange() == 0 && radius() == 0, "null optional client is ignored");
        provider.Client = new Client();
        Check(chunkRange() == 0, "null optional config is ignored");
        provider.Client.Config = new Config { Enabled = true, MaxViewDistance = 4097, FarViewDistance = 4097 };
        Check(chunkRange() == 4097 && radius() == 4097, "ChunkLOD uses full terrain coverage rather than its legacy half-range fog uniform");
        provider.Client.Config.Enabled = false;
        Check(chunkRange() == 0, "disabled LOD keeps native endpoint");
        provider.Client.Config = new Config { Enabled = true, MaxViewDistance = 8192, FarViewDistance = 8192 };
        Check(chunkRange() == 8192 && radius() == 8192, "cached getter observes replaced live config");
        Check(LodFogRange.SelectEndpoint(2048, 8192, 4096) == 4096, "Farseer server cap is honored");
        Check(LodFogRange.SelectEndpoint(2048, 0, 256) == 2048, "Farseer cap does not cap ChunkLOD");
        Check(LodFogRange.SelectEndpoint(float.NaN, float.PositiveInfinity, 0) == 0, "invalid provider ranges are ignored");
        Check(LodFogRange.SelectEndpoint(-1, -2, 0) == 0, "negative provider ranges are ignored");
        for (int i = 0; i < 10000; i++) chunkRange(); // Warm expression/JIT before measuring.
        long bytes = GC.GetAllocatedBytesForCurrentThread();
        float observed = 0;
        for (int i = 0; i < 10000; i++) observed += chunkRange() + radius();
        Check(GC.GetAllocatedBytesForCurrentThread() == bytes && observed > 0, "live range getters allocate no render-loop garbage");
        Console.WriteLine("PASS live sunlight table, level-1 threshold, optional LOD null/disabled/replaced config, rendered radius/server cap, zero getter allocations");
    }
}
