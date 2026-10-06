using System;

namespace DRTAgX;

// Four vec4s: eye position/range, native RGB/map index, transitions/pair,
// inverse-view-rotated eye position/native light level. No absolute float world
// coordinates are uploaded; source placement remains double-precision on CPU.
internal static class StaticLightGpuRecord
{
    internal const int FloatCount = 16;

    internal static void Rgb(int hue, int saturation, out float r, out float g, out float b)
        => Rgb(hue, saturation, AgxGuiDialog.DefaultPlsSaturation, out r, out g, out b);

    internal static void Rgb(int hue, int saturation, float saturationScale, out float r, out float g, out float b)
    {
        // Native integer HSV uses hue steps 4 and saturation steps 32.
        // Scale saturation from the saved slider, then quantize/clamp to the
        // native 8-bit range. Value remains 255; light chroma has no gamma decode.
        int h = Math.Clamp(hue * 4, 0, 255);
        int s = (int)MathF.Round(Math.Clamp(saturation * 32f * saturationScale, 0f, 255f));
        if (s == 0) { r = g = b = 1; return; }
        int region = h / 43, remainder = (h - region * 43) * 6;
        float p = ((255 * (255 - s)) >> 8) / 255f;
        float q = ((255 * (255 - ((s * remainder) >> 8))) >> 8) / 255f;
        float t = ((255 * (255 - ((s * (255 - remainder)) >> 8))) >> 8) / 255f;
        (r, g, b) = region switch {
            0 => (1f, t, p), 1 => (q, 1f, p), 2 => (p, 1f, t),
            3 => (p, q, 1f), 4 => (t, p, 1f), _ => (1f, p, q)
        };
    }

    internal static void RotateEye(float[] target, int offset, float x, float y, float z, float[] inverseView)
    {
        // Column-major mat3(invModelViewMatrix) * eye, evaluated once per source.
        // Receiver-only rotation happens once per pixel; subtraction replaces
        // the former per-candidate inverse-view matrix multiplication.
        target[offset] = inverseView[0] * x + inverseView[4] * y + inverseView[8] * z;
        target[offset + 1] = inverseView[1] * x + inverseView[5] * y + inverseView[9] * z;
        target[offset + 2] = inverseView[2] * x + inverseView[6] * y + inverseView[10] * z;
    }
}
