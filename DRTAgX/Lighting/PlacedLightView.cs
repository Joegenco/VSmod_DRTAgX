using System;
using Vintagestory.API.MathTools;

namespace DRTAgX;

// Shared conservative sphere projection for resident selection and tile lists.
internal static class PlacedLightView
{
    internal const double ReceiverRange = 128.0;
    internal const double DiscoveryRange = ReceiverRange + StaticTerrainShadowMaps.Range;
    internal const int OffViewPriority = 3;
    // A 10% NDC guard keeps edge maps warm. Within that guard publish a
    // conservative rectangle; the pixel distance test still enforces light reach.
    internal const float ResidencyExtent = 1.1f;

    internal readonly record struct Bounds(float MinX, float MaxX, float MinY, float MaxY,
        int Class, float CenterX, float CenterY, double CenterDepth);

    internal static Bounds ForShadowCulling(Bounds projected, bool enabled)
    {
        // With view culling off, range-limited sources compete by distance and
        // cover every tile. Existing distance tests still bound their radiance.
        return enabled ? projected : new Bounds(-1, 1, -1, 1, 0, float.NaN, float.NaN, projected.CenterDepth);
    }

    internal static int Priority(Bounds bounds)
    {
        if (bounds.Class != 0) return OffViewPriority;
        // A light whose center is in the central view has stronger evidence
        // of reaching visible receivers than one whose halo clips a corner.
        if (bounds.CenterDepth <= 0.1 || !float.IsFinite(bounds.CenterX) ||
            !float.IsFinite(bounds.CenterY)) return 2;
        float x = Math.Abs(bounds.CenterX), y = Math.Abs(bounds.CenterY);
        if (x <= 0.6f && y <= 0.6f) return 0;
        return x <= 1f && y <= 1f ? 1 : 2;
    }

    internal static void CameraFromView(double[] view, double[] inverseScratch, Vec3d target)
    {
        // Native matrix columns are close to orthonormal but not exact.
        // Full inversion prevents large-world translation error from a
        // transpose-as-inverse shortcut.
        Mat4d.Invert(inverseScratch, view);
        target.X = inverseScratch[12];
        target.Y = inverseScratch[13];
        target.Z = inverseScratch[14];
    }

    internal static Bounds Project(double x, double y, double z, float radius,
        double[] view, float[] projection)
    {
        double ex = view[0] * x + view[4] * y + view[8] * z + view[12];
        double ey = view[1] * x + view[5] * y + view[9] * z + view[13];
        double ez = view[2] * x + view[6] * y + view[10] * z + view[14];
        if (ez >= radius) return new Bounds(0, 0, 0, 0, 2, 0, 0, -ez);
        bool standardPerspective = projection.Length >= 16 &&
            float.IsFinite(projection[0]) && float.IsFinite(projection[5]) &&
            projection[0] > 0 && projection[5] > 0 &&
            float.IsFinite(projection[8]) && float.IsFinite(projection[9]) &&
            Math.Abs(projection[11] + 1f) < 0.001f && Math.Abs(projection[15]) < 0.001f;
        int visibilityClass = 0;
        if (standardPerspective)
        {
            // Test the sphere against the four perspective frustum planes in
            // eye space before the conservative rectangle calculation. A
            // near-plane crossing far to the side cannot light the viewport.
            if (!IntersectsSides(ex, ey, ez, radius, projection, 1f))
            {
                if (!IntersectsSides(ex, ey, ez, radius, projection, ResidencyExtent))
                    return new Bounds(0, 0, 0, 0, 2, 0, 0, -ez);
                // Retain real projected bounds in the guard. The old zero-sized
                // fringe record could not supply conservative edge tile coverage.
                visibilityClass = 1;
            }
        }
        double reciprocal = -ez > 0.1 ? 1.0 / -ez : 0.0;
        float cx = reciprocal > 0 && standardPerspective ?
            (float)(projection[0] * ex * reciprocal - projection[8]) : float.NaN;
        float cy = reciprocal > 0 && standardPerspective ?
            (float)(projection[5] * ey * reciprocal - projection[9]) : float.NaN;
        // Near-plane and camera-inside intersections conservatively cover the
        // screen. This also handles an offscreen emitter whose halo enters it.
        if (-ez <= radius + 0.1 || !standardPerspective)
            return new Bounds(-1, 1, -1, 1, visibilityClass, cx, cy, -ez);
        // Off-axis center shift enlarges the sphere projection near the edge.
        float rx = (float)(projection[0] * radius * (1.0 + Math.Abs(ex) * reciprocal) / (-ez - radius));
        float ry = (float)(projection[5] * radius * (1.0 + Math.Abs(ey) * reciprocal) / (-ez - radius));
        float minX = cx - rx, maxX = cx + rx, minY = cy - ry, maxY = cy + ry;
        // The side planes reject outside the residency guard. Within it this
        // conservative rectangle may cover extra edge pixels, which the shader
        // rejects cheaply using the actual source radius.
        return new Bounds(minX, maxX, minY, maxY, visibilityClass, cx, cy, -ez);
    }

    private static bool IntersectsSides(double ex, double ey, double ez, float radius,
        float[] projection, float extent)
    {
        // For each plane, signed center distance plus sphere radius must be
        // nonnegative. clip.w = -eye.z for this perspective projection.
        double leftZ = projection[8] - extent, rightZ = -projection[8] - extent;
        double bottomZ = projection[9] - extent, topZ = -projection[9] - extent;
        return projection[0] * ex + leftZ * ez >=
                   -radius * Math.Sqrt(projection[0] * projection[0] + leftZ * leftZ) &&
               -projection[0] * ex + rightZ * ez >=
                   -radius * Math.Sqrt(projection[0] * projection[0] + rightZ * rightZ) &&
               projection[5] * ey + bottomZ * ez >=
                   -radius * Math.Sqrt(projection[5] * projection[5] + bottomZ * bottomZ) &&
               -projection[5] * ey + topZ * ez >=
                   -radius * Math.Sqrt(projection[5] * projection[5] + topZ * topZ);
    }

    internal static float Reach(StaticLightSources.Source source) =>
        Math.Min(source.Level * 1.4f, StaticTerrainShadowMaps.Range);

    internal static Bounds Project(StaticLightSources.Source source, double[] view, float[] projection) =>
        Project(source.X + 0.5, source.Y + StaticTerrainShadowMaps.SourceHeight,
            source.Z + 0.5, Reach(source), view, projection);
}
