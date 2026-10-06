using System;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace DRTAgX;

/// <summary>Render-thread sunlight calibration and camera shelter; no gameplay light changes.</summary>
internal sealed class AtmosphereFogEnvironment
{
    internal const int FrameOffset = 116; // Append one std140 vec4; all earlier offsets stay intact.
    internal const int FullSunlightLevel = 1; // Local weather fog reaches full strength at native sunlight level 1.
    private readonly BlockPos _cameraBlock = new(0);

    internal void Capture(ICoreClientAPI api, float[] frame, float lodEnd)
    {
        var world = api.World;
        float[] sunlight = world.SunLightLevels;
        int brightness = world.SunBrightness;
        float full = Math.Max(0.000001f, NormalizeSunlight(sunlight, brightness, Math.Min(FullSunlightLevel, brightness)));
        var camera = world.Player.Entity.CameraPos;
        _cameraBlock.X = (int)Math.Floor(camera.X);
        _cameraBlock.Z = (int)Math.Floor(camera.Z);
        // Native camera Y is dimension-encoded. The public InternalY setter
        // separates dimension/local Y for the dimension-aware light accessor.
        _cameraBlock.InternalY = (int)Math.Floor(camera.Y);
        int level = world.BlockAccessor.GetLightLevel(_cameraBlock, EnumLightLevelType.OnlySunLight);
        frame[FrameOffset] = full;
        frame[FrameOffset + 1] = Exposure(NormalizeSunlight(sunlight, brightness, level), full);
        frame[FrameOffset + 2] = lodEnd;
        frame[FrameOffset + 3] = 1f;
    }

    internal static float NormalizeSunlight(float[] table, int brightness, int level)
    {
        if (table.Length == 0) return 0f;
        float zero = Sample(table, 0), full = Sample(table, brightness);
        // Light-table values are radiance metadata; never apply texture gamma.
        return Math.Clamp((Sample(table, level) - zero) / Math.Max(full - zero, 0.000001f), 0f, 1f);
    }

    private static float Sample(float[] table, int level)
    {
        float value = table[Math.Clamp(level, 0, table.Length - 1)];
        return float.IsFinite(value) ? Math.Max(0f, value) : 0f;
    }

    internal static float Exposure(float sunlight, float full)
    {
        float x = Math.Clamp(sunlight / Math.Max(full, 0.000001f), 0f, 1f);
        return x * x * (3f - 2f * x); // Same smoothstep as the receiving-surface GLSL.
    }
}
