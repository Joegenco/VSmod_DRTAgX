using System;

namespace DRTAgX;

/// <summary>Bounded physical variation keyed by native daily SunsetMod and current cloud density.</summary>
internal sealed class AtmosphereSkyProfile
{
    internal const int Offset = 108;
    private bool _initialized;

    private static float Unit(uint seed)
    {
        // A deterministic hash of the native daily value: no private clock, random calls or saved state.
        seed ^= seed >> 16; seed *= 0x7feb352d; seed ^= seed >> 15; seed *= 0x846ca68b; seed ^= seed >> 16;
        return (seed & 0xffffff) / 16777215f;
    }

    internal void Reset() => _initialized = false;

    internal void Capture(float sunsetMod, float solarY, float cloudDensity, float deltaTime, float[] frame)
    {
        uint seed = unchecked((uint)BitConverter.SingleToInt32Bits(float.IsFinite(sunsetMod) ? sunsetMod : 0f));
        float cloud = float.IsFinite(cloudDensity) ? Math.Clamp(cloudDensity, 0f, 1f) : 0f;
        if (!float.IsFinite(solarY)) solarY = 1f;
        if (!float.IsFinite(deltaTime)) deltaTime = 0f;
        float t = Math.Clamp(Math.Abs(solarY) / 0.35f, 0f, 1f);
        float twilight = 1f - t * t * (3f - 2f * t);
        float aerosol = 0.35f + 1.5f * Unit(seed ^ 0x9e3779b9) + 0.35f * cloud;
        float height = 0.8f + 1.6f * Unit(seed ^ 0x85ebca6b) + 0.3f * cloud;
        float ozone = 0.65f + 1.95f * Unit(seed ^ 0xc2b2ae35);
        float spread = 0.15f + 0.6f * Unit(seed ^ 0x27d4eb2f);
        // Fade physical changes away from twilight; the accepted daytime transport stays the reference.
        float alpha = _initialized ? 1f - MathF.Exp(-Math.Clamp(deltaTime, 0f, 1f) / 10f) : 1f;
        Set(0, 1f + twilight * (aerosol - 1f));
        Set(1, 1f + twilight * (height - 1f));
        Set(2, 1f + twilight * (ozone - 1f));
        Set(3, 0.35f + twilight * (spread - 0.35f));
        _initialized = true;

        void Set(int index, float target) => frame[Offset + index] += alpha * (target - frame[Offset + index]);
    }
}
