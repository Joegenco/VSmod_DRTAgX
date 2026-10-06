using System;
using System.Collections;
using System.Reflection;
using DRTAgX;
using Vintagestory.API.Common;

// Keep exact caster edits independent of native upload availability. A single
// inconsistent metadata snapshot must not force a resident emitter to rebake.
internal static class SourceEditProbe
{
    internal static void Run(Action<bool, string> check)
    {
        using var fixture = new SourceCacheFixture(1);
        fixture.Settle(); fixture.Sources.NativeGeometryNotifications = true;
        var source = fixture.Sources.Sources[0];
        fixture.Changed(4, 0, 0); fixture.Update();
        var edits = typeof(StaticLightSources).GetProperty("BlockEdits", BindingFlags.Instance | BindingFlags.NonPublic)
            ?.GetValue(fixture.Sources) as ICollection;
        check(edits?.Count == 1 && fixture.Sources.TryGet(source, out _) && fixture.Sources.ChangedChunks.Count == 0,
            "non-light block edits remain a shadow trigger when the upload hook is installed");

        fixture.SetLight(0, 0, 0, new byte[] { 0, 0, 0 });
        fixture.Dirty(EnumChunkDirtyReason.MarkedDirty); fixture.Update();
        check(fixture.Sources.TryGet(source, out _), "one zero-HSV metadata snapshot preserves a known emitter");
        fixture.SetLight(0, 0, 0, new byte[] { 0, 0, 20 }); fixture.Update();
        check(fixture.Sources.Sources.Count == 1 && fixture.Sources.TryGet(source, out _),
            "bounded next-frame confirmation recovers transient non-emitting metadata");
        fixture.ResetCounts(); for (int i = 0; i < 100; i++) fixture.Update();
        check(fixture.IndexReads == 0 && fixture.BlockQueries == 0,
            "metadata confirmation settles without recurring scans");

        fixture.SetLight(0, 0, 0, new byte[] { 0, 0, 0 });
        fixture.Dirty(EnumChunkDirtyReason.MarkedDirty); fixture.Update(); fixture.Update();
        check(!fixture.Sources.TryGet(source, out _), "confirmed non-emitting metadata releases a source without a block event");
        fixture.Add(0, 0, 0); fixture.Changed(0, 0, 0); fixture.Update();
        fixture.Remove(0, 0, 0); fixture.Changed(0, 0, 0); fixture.Update();
        check(!fixture.Sources.TryGet(source, out _), "an exact light removal remains immediate");
    }
}
