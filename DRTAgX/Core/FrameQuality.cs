using System;

namespace DRTAgX;

/// <summary>Immutable render-thread snapshot. Caps never modify saved effect preferences.</summary>
internal readonly record struct FrameQuality(bool Performance)
{
    // Opt-in benchmark selection never writes preferences; ordinary launches use the checkbox.
    private static readonly bool? ProfileMode = string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DRTAGX_PROFILE_DIR"))
        ? null : Environment.GetEnvironmentVariable("DRTAGX_PROFILE_MODE") switch { "normal" => false, "performance" => true, _ => null };
    internal static FrameQuality Current { get; private set; }
    internal static int Generation { get; private set; }
    internal static void Publish(AgxConfig config)
    {
        var next = new FrameQuality(ProfileMode ?? config.PerformanceMode);
        if (next == Current) return;
        Current = next;
        ++Generation; // Resource owners react once to a new mode at the Before boundary.
    }
    internal int MovingLights(int preference) => Performance ? Math.Min(preference, 2) : preference;
    internal int MovingFace(int normal) => Performance ? Math.Max(1, normal / 2) : normal;
    internal int PlacedFace => Performance ? 48 : 96;
    // Placed receiver/ranking work is the measured scene outlier. Retain native
    // voxel lighting and dormant completed maps; never change the saved switch.
    internal bool PlacedLights(bool preference) => preference && !Performance;
    internal int GtaoSlices(int preference) => Performance ? Math.Min(preference, 2) : preference;
    internal int GtaoSteps(int preference) => Performance ? Math.Min(preference, 3) : preference;
    internal int VolumetricSteps(int preference) => Performance ? Math.Min(preference, 8) : preference;
    internal int SsrSteps(int preference) => Performance ? Math.Min(preference, 4) : preference;
    internal int SsrDownsample(int preference) => Performance ? Math.Max(preference, 6) : preference;
    internal bool ContactShadows(bool preference) => preference && !Performance;
}
