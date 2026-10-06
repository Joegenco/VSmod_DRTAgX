using System;
using System.Reflection;
using DRTAgX;
using OpenTK.Graphics.OpenGL4;
using static StaticCacheProbeFixture;

// Real D24 promotion with synthetic completed faces isolates invalidation and
// publication from native terrain traversal. Appearance remains human reviewed.
internal static class ShadowEditProbe
{
    internal static void Run()
    {
        RadiusAndWake();
        DepthUpdates();
        ReplacementWait();
    }

    private static void RadiusAndWake()
    {
        using var fixture = new StaticCacheProbeFixture(3);
        var near = new StaticLightSources.Source(0, 0, -10, 0, 0, 20);
        var edge = near with { X = 16 };
        var far = near with { Z = -80 };
        fixture.Source(near); fixture.Resident(near, 0);
        fixture.Source(edge); fixture.Resident(edge, 1);
        fixture.Source(far); fixture.Resident(far, 2); fixture.Settle();
        fixture.Sources.NativeGeometryNotifications = true;
        fixture.Edits.Add(new(0, 0, -9)); fixture.Update(); fixture.Edits.Clear();
        Check((bool)Get(fixture.Slot(0), "Dirty") && (bool)Get(fixture.Slot(1), "Dirty") &&
            !(bool)Get(fixture.Slot(2), "Dirty"), "exact block edit refreshes intersecting 16-block lights, leaving distant maps clean");
        Check(fixture.Maps.Active.Count == 3 && fixture.Maps.FacesBakedThisFrame == 0 && fixture.Maps.Active[0].Fade == 1,
            "pending caster upload retains full-strength completed lighting");
        fixture.Update();
        Check(!fixture.Maps.UpdatedThisFrame && fixture.Maps.BakeFailures == 0,
            "edit fallback sleeps until an upload or its deadline");

        fixture.Changes.Add(new(0, 0, -1));
        Call(fixture.Maps, "UpdateSlots", fixture.Sources, fixture.Camera, fixture.View, fixture.Projection);
        fixture.Changes.Clear();
        Check((double)Get(fixture.Slot(0), "RecheckAt") == 0 && (double)Get(fixture.Slot(1), "RecheckAt") == 0,
            "completed native upload releases nearby edit waits immediately");
        Check(!PlacedLightGeometry.EditsTouch(near, new[] { new StaticLightSources.Position(17, 0, -10) }),
            "exact edit outside the 16-block sphere does not trigger a local refresh");
    }

    private static void DepthUpdates()
    {
        using var state = new ShadowGlState();
        using var fixture = new StaticCacheProbeFixture();
        var source = new StaticLightSources.Source(0, 0, -10, 0, 0, 20);
        fixture.Source(source); fixture.Resident(source); fixture.Settle();
        fixture.Sources.NativeGeometryNotifications = true;
        Fill(fixture, 0, 0.25);
        foreach (float depth in new[] { 0.8f, 0.2f })
        {
            // Removing a blocker increases depth; placing one decreases it.
            // An exact edit invalidates before upload, without deleting light.
            fixture.Edits.Add(new(0, 0, -5)); fixture.Update(); fixture.Edits.Clear();
            fixture.Changes.Add(new(0, 0, -1));
            Call(fixture.Maps, "UpdateSlots", fixture.Sources, fixture.Camera, fixture.View, fixture.Projection);
            fixture.Changes.Clear();
            BeginRefresh(fixture, source);
            Fill(fixture, fixture.Maps.Capacity, depth);
            var bake = (PlacedLightBake)Get(fixture.Maps, "_bake"); bake.NextFace = 4;
            Set(fixture.Maps, "_bake", bake); Call(fixture.Maps, "Publish");
            Check(fixture.Maps.Active.Count == 1 && fixture.Maps.Active[0].Fade == 1 && fixture.Maps.Active[0].ShadowBlend == 0,
                "partial edited shadow refresh keeps complete resident lighting published");
            bake.NextFace = 6; Set(fixture.Maps, "_bake", bake);
            Set(fixture.Maps, "_blendStart", Now - 1); Set(fixture.Maps, "_incomingCompletedAt", Now - 1);
            Set(fixture.Maps, "_time", Now);
            Call(fixture.Maps, "AdvanceBake", fixture.Api); Call(fixture.Maps, "Publish");
            Check(ReadFaces(fixture, 0, depth) && !(bool)Get(fixture.Slot(), "Dirty") &&
                fixture.Maps.Active.Count == 1 && fixture.Maps.Active[0].Fade == 1,
                depth > 0.5f ? "removed blocker promotes all six refreshed depth faces" : "placed blocker promotes all six refreshed depth faces");
        }
        BeginRefresh(fixture, source);
        var interrupted = (PlacedLightBake)Get(fixture.Maps, "_bake"); interrupted.NextFace = 4;
        Set(fixture.Maps, "_bake", interrupted);
        fixture.Edits.Add(new(1, 0, -5)); fixture.Update(); fixture.Edits.Clear();
        Check(!((PlacedLightBake)Get(fixture.Maps, "_bake")).Active && ReadFaces(fixture, 0, 0.2f) &&
            fixture.Maps.Active.Count == 1 && fixture.Maps.Active[0].Fade == 1,
            "another caster edit cancels staging without clearing resident depth or lighting");
        Check(GL.GetError() == ErrorCode.NoError, "edited depth promotion has no GL errors");
    }

    private static void ReplacementWait()
    {
        using var fixture = new StaticCacheProbeFixture();
        var old = new StaticLightSources.Source(0, 0, 50, 0, 0, 20);
        var incoming = old with { Z = -10 };
        fixture.Source(old); fixture.Resident(old); fixture.Settle();
        fixture.Sources.NativeGeometryNotifications = true; fixture.Source(incoming);
        fixture.Edits.Add(new(0, 0, -5)); fixture.Update(); fixture.Edits.Clear();
        Check(fixture.Pending.Count == 1 && fixture.Maps.BakeFailures == 0 && (bool)Get(fixture.Slot(), "Valid") &&
            (double)Get(fixture.Maps, "_nextSchedulerWake") > Now,
            "edited replacement candidate retains its victim and schedules a real geometry deadline");
        fixture.Update();
        Check(!fixture.Maps.UpdatedThisFrame, "waiting replacement performs no recurring bake attempts");
        fixture.Changes.Add(new(0, 0, -1));
        Call(fixture.Pending, "Update", fixture.Sources, fixture.Camera, fixture.View, fixture.Projection,
            Get(fixture.Maps, "_residents"), Now);
        fixture.Changes.Clear();
        Check(fixture.Pending.Choose(true, true, Now, new()) >= 0,
            "uploaded incoming geometry makes a retained replacement candidate eligible");
    }

    private static void BeginRefresh(StaticCacheProbeFixture fixture, StaticLightSources.Source source)
    {
        var kind = typeof(StaticTerrainShadowMaps).GetNestedType("StageKind", BindingFlags.NonPublic);
        Call(fixture.Maps, "BeginBake", 0, source, Enum.Parse(kind, "Refresh"), false);
    }

    private static void Attach(StaticCacheProbeFixture fixture, int layer)
    {
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, (int)Get(fixture.Maps, "_framebuffer"));
        GL.FramebufferTextureLayer(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment, fixture.Texture, 0, layer);
        GL.DrawBuffer(DrawBufferMode.None); GL.ReadBuffer(ReadBufferMode.None);
        if (GL.CheckFramebufferStatus(FramebufferTarget.Framebuffer) != FramebufferErrorCode.FramebufferComplete)
            throw new Exception("Depth-only edit fixture framebuffer is incomplete");
    }
    private static void Fill(StaticCacheProbeFixture fixture, int slot, double depth)
    {
        double previous = GL.GetDouble(GetPName.DepthClearValue);
        try {
            GL.Disable(EnableCap.ScissorTest); GL.DepthMask(true); GL.ClearDepth(depth);
            for (int face = 0; face < 6; face++) { Attach(fixture, slot * 6 + face); GL.Clear(ClearBufferMask.DepthBufferBit); }
        }
        finally { GL.ClearDepth(previous); }
    }
    private static bool ReadFaces(StaticCacheProbeFixture fixture, int slot, float expected)
    {
        float[] pixel = new float[1];
        for (int face = 0; face < 6; face++) {
            Attach(fixture, slot * 6 + face);
            GL.ReadPixels(0, 0, 1, 1, PixelFormat.DepthComponent, PixelType.Float, pixel);
            if (Math.Abs(pixel[0] - expected) > 0.000001f) return false;
        }
        return true;
    }
}
