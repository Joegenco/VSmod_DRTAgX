namespace DRTAgX;

// Accepted visible-cache throughput policy. Debug builds can restore the
// earlier adaptive experiment; Release always uses the user-validated policy.
internal static class PlacedLightUpdateExperiment
{
    internal const bool FastVisibleUpdates = true;
    internal const int PageJobs = 1024, PageQueue = 4096;
    internal static int PixelStep => FastVisibleUpdates ? 4 : 8;
    internal static int TransitionFrames => FastVisibleUpdates ? 0 : PlacedLightSurfaceCache.TransitionFrames;
}