using System;
using Vintagestory.API.Common;

namespace DRTAgX;

/// <summary>Render-thread affine replay of native ambient overlays, without changing the manager.</summary>
internal static class AmbientSkyInputs
{
    internal const int ScaleOffset = 92, OverlayOffset = 96, NativeOffset = 100;

    internal static void Capture(IAmbientManager ambient, float[] frame)
    {
        var native = ambient.BlendedAmbientColor;
        frame[NativeOffset] = native.X; frame[NativeOffset + 1] = native.Y; frame[NativeOffset + 2] = native.Z;
        float cloud = ambient.BlendedCloudDensity;
        frame[NativeOffset + 3] = float.IsFinite(cloud) ? Math.Clamp(cloud, 0f, 1f) : 0f;
        var basis = ambient.Base;
        var baseColor = basis.AmbientColor?.Value;
        float r = baseColor is { Length: >= 3 } ? baseColor[0] : 1f;
        float g = baseColor is { Length: >= 3 } ? baseColor[1] : 1f;
        float b = baseColor is { Length: >= 3 } ? baseColor[2] : 1f;
        float nr = r, ng = g, nb = b, scale = 0f;
        float scene = basis.SceneBrightness?.Value ?? 1f, nativeScene = scene;
        bool foundSun = false;
        var modifiers = ambient.CurrentModifiers;
        // Indexed access avoids the native dictionary's allocating IEnumerator.
        for (int i = 0; i < modifiers.Count; ++i)
        {
            string key = modifiers.GetKeyAtIndex(i);
            var modifier = modifiers.GetValueAtIndex(i);
            if (modifier == null) continue;
            var color = modifier.AmbientColor;
            if (color?.Value is { Length: >= 3 } value)
            {
                float w = color.Weight, keep = 1f - w;
                nr = nr * keep + value[0] * w;
                ng = ng * keep + value[1] * w;
                nb = nb * keep + value[2] * w;
                if (key == "sunglow")
                {
                    // Runtime-verified producer: substitute only its environmental color.
                    scale = scale * keep + w;
                    r *= keep; g *= keep; b *= keep;
                    foundSun = true;
                }
                else
                {
                    scale *= keep;
                    r = r * keep + value[0] * w;
                    g = g * keep + value[1] * w;
                    b = b * keep + value[2] * w;
                }
            }
            var brightness = modifier.SceneBrightness;
            if (brightness == null) continue;
            nativeScene = nativeScene * (1f - brightness.Weight) + brightness.Value * brightness.Weight;
            // LUT solar/lunar strength already owns the day/night transition.
            // Water, weather, lightning, locations and unknown mod overlays retain their order.
            if (key != "night")
                scene = scene * (1f - brightness.Weight) + brightness.Value * brightness.Weight;
        }
        frame[ScaleOffset] = frame[ScaleOffset + 1] = frame[ScaleOffset + 2] = scale * scene;
        frame[OverlayOffset] = r * scene; frame[OverlayOffset + 1] = g * scene; frame[OverlayOffset + 2] = b * scene;
        frame[OverlayOffset + 3] = 0f;
        // Native smoothing or a changed provider contract must fall back rather than discard effects.
        float error = Math.Max(Math.Abs(nr * nativeScene - native.X),
            Math.Max(Math.Abs(ng * nativeScene - native.Y), Math.Abs(nb * nativeScene - native.Z)));
        bool valid = foundSun && float.IsFinite(error) && error <= 0.002f && float.IsFinite(scene) &&
            float.IsFinite(scale) && float.IsFinite(r) && float.IsFinite(g) && float.IsFinite(b);
        frame[ScaleOffset + 3] = valid ? 1f : 0f;
    }
}
