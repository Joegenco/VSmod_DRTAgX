using System;
using DRTAgX;

// An independent point-in-frustum reference checks the conservative sphere
// bounds through camera turns, screen edges and shifted native projections.
internal static class ViewCoverageProbe
{
    internal static void Run()
    {
        double[] view = { 1,0,0,0, 0,1,0,0, 0,0,1,0, 0,0,0,1 };
        float[] projection = { 1,0,0,0, 0,1,0,0, 0,0,-1,-1, 0,0,-1,0 };
        int checkedPoints = 0;
        foreach (float shift in new[] { 0f, .2f, -.2f })
        foreach (double angle in new[] { -.3, -.1, 0, .1, .3 })
        foreach (float radius in new[] { 1f, 4f, 22f })
        {
            projection[8] = shift; projection[9] = shift * .5f;
            view[0] = view[10] = Math.Cos(angle); view[8] = Math.Sin(angle); view[2] = -view[8];
            foreach (double x in new[] { -24.0, -12.0, -10.0, 0, 10.0, 12.0, 24.0 })
            foreach (double y in new[] { -12.0, 0, 12.0 })
            {
                var bounds = PlacedLightView.Project(x, y, -10, radius, view, projection);
                for (int i = 0; i < 256; i++) {
                    // Uniform latitude with an irrational azimuth increment.
                    double height = 1 - 2 * (i + .5) / 256, ring = Math.Sqrt(1 - height * height);
                    double azimuth = i * 2.399963229728653;
                    double px = x + radius * ring * Math.Cos(azimuth), py = y + radius * height;
                    double pz = -10 + radius * ring * Math.Sin(azimuth);
                    double ex = view[0] * px + view[8] * pz, ey = py, ez = view[2] * px + view[10] * pz;
                    if (ez >= -.1) continue;
                    double nx = ex / -ez - projection[8], ny = ey / -ez - projection[9];
                    if (Math.Abs(nx) > 1 || Math.Abs(ny) > 1) continue;
                    checkedPoints++;
                    if (!PlacedLightPolicy.Visible(bounds) || nx < bounds.MinX - 1e-5 || nx > bounds.MaxX + 1e-5 ||
                        ny < bounds.MinY - 1e-5 || ny > bounds.MaxY + 1e-5)
                        throw new Exception("Visible edge receiver escaped conservative sphere coverage");
                }
            }
        }
        view[0] = view[10] = 1; view[8] = view[2] = 0; projection[8] = projection[9] = 0;
        var fringe = PlacedLightView.Project(12.4, 0, -10, 1, view, projection);
        if (fringe.Class != 1 || PlacedLightPolicy.Priority(fringe, 300) >= PlacedLightPolicy.Offscreen ||
            !PlacedLightPolicy.Visible(fringe) || fringe.MinX >= 1 || fringe.MaxX <= 1)
            throw new Exception("Residency guard must retain real conservative edge bounds");
        Console.WriteLine($"PASS {checkedPoints} visible sphere points retain edge coverage through camera turns and shifted projections; fringe bounds protect edge publication");
    }
}
