using System;
using DRTAgX;

internal static class SurfaceCalibrationProbe
{
    internal static void Run()
    {
        float[] table = new float[32], calibrated = new float[64];
        for (int i = 0; i < table.Length; ++i) table[i] = i / 31f;
        void Check(bool ok, string name) {
            if (!ok) throw new Exception(name);
            Console.WriteLine("PASS " + name);
        }
        SurfaceLightBindings.Calibrate(table, calibrated);
        Check(calibrated[0] == 0 && calibrated[1] == 0, "level zero cannot emit calibrated direct/self light");
        foreach (int level in new[] { 1, 2, 8, 14, 15, 18, 20, 31 }) {
            float radius = Math.Min(1.4f*level, 22f), d = Math.Min(5.5f, radius/2);
            float falloff = (1-d/radius)/(1+0.25f*d*d);
            float native = 0.5f * (level-d)/31;
            Check(Math.Abs(calibrated[level*2]*falloff-native) < 1e-6 &&
                Math.Abs(calibrated[level*2+1]-0.5f*table[level]) < 1e-6,
                $"level {level}: calibrated direct reference and bounded self-light");
        }
        foreach (int level in new[] { 14, 15, 18, 20 }) foreach (float d in new[] { 5f, 6f }) {
            float radius = Math.Min(1.4f*level, 22f);
            float direct = calibrated[level*2]*(1-d/radius)/(1+0.25f*d*d);
            float voxel = 0.5f*(level-d)/31;
            Check(Math.Abs(direct/voxel-1) < 0.25, $"level {level}: approximate native parity at {d} blocks");
        }
        float original = calibrated[28];
        for (int i = 0; i < table.Length; ++i) table[i] *= 0.3f;
        SurfaceLightBindings.Calibrate(table, calibrated);
        Check(Math.Abs(calibrated[28] - original*0.3f) < 1e-6, "calibration follows supplied world table instead of installed defaults");
        SurfaceLightBindings.Calibrate([], calibrated);
        Check(Array.TrueForAll(calibrated, value => value == 0), "empty world block table has finite zero fallback");
        SurfaceLightBindings.Calibrate([float.NaN, float.PositiveInfinity, -1], calibrated);
        Check(Array.TrueForAll(calibrated, value => float.IsFinite(value) && value >= 0), "invalid world block values cannot publish NaN/negative radiance");
        SurfaceLightBindings.Calibrate([0.1f, 0.2f], calibrated);
        Check(float.IsFinite(calibrated[62]) && calibrated[63] == 0.1f, "short world table clamps calibration indices safely");
    }
}
