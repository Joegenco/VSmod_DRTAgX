using System;
using System.Collections.Generic;
using System.Reflection;
using DRTAgX;
using OpenTK.Graphics.OpenGL4;
using Vintagestory.API.MathTools;

// Supply synthetic completed depth to exercise the production promotion and
// publication paths. This checks activation correctness, not native draw timing.
internal static class PlacementPublishProbe
{
    private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static object Get(object target, string name) => target.GetType().GetField(name, Fields).GetValue(target);
    private static void Set(object target, string name, object value) => target.GetType().GetField(name, Fields).SetValue(target, value);
    private static object Call(object target, string name, params object[] args) =>
        target.GetType().GetMethod(name, Fields).Invoke(target, args);

    internal static void Run()
    {
        using var saved = new ShadowGlState();
        using var owner = new StaticTerrainShadowMaps(null);
        // One resident plus staging suffices; copies use production face size.
        typeof(StaticTerrainShadowMaps).GetProperty("Capacity", Fields).SetValue(owner, 1);
        owner.Invalidate();
        int texture = GL.GenTexture();
        GL.ActiveTexture(TextureUnit.Texture14);
        GL.BindTexture(TextureTarget.Texture2DArray, texture);
        GL.TexStorage3D(TextureTarget3d.Texture2DArray, 1, SizedInternalFormat.DepthComponent24, DRTAgX.StaticTerrainShadowMaps.FaceSize, DRTAgX.StaticTerrainShadowMaps.FaceSize, 12);
        Set(owner, "_texture", texture); // Production Dispose owns this texture.
        Set(owner, "_time", 100.0);
        var source = new StaticLightSources.Source(0, 0, -10, 0, 0, 20);
        var kind = typeof(StaticTerrainShadowMaps).GetNestedType("StageKind", BindingFlags.NonPublic);
        var bounds = new PlacedLightView.Bounds(-0.5f, -0.5f, 0.5f, 0.5f, 0, 0, 0, 10);
        object slot = ((Array)Get(owner, "_slots")).GetValue(0);
        for (int pass = 0; pass < 2; pass++)
        {
            owner.Invalidate();
            Call(owner, "Assign", 0, source);
            Set(slot, "Immediate", pass == 0);
            Set(slot, "Bounds", bounds);
            Call(owner, "BeginBake", 0, source, Enum.Parse(kind, "Admission"), false);
            Complete(owner);
            Call(owner, "Publish");
            var ready = (List<StaticTerrainShadowMaps.ActiveLight>)Get(owner, "_readyLights");
            if (ready.Count != 1 || ready[0].Fade != (pass == 0 ? 1 : 0))
                throw new Exception("Placement activation or ordinary admission fade changed");
        }
        Console.WriteLine("PASS placement publishes full strength immediately; ordinary admission retains fade");

        // With the cache full, keep the old map sampled until replacement is
        // complete, and retain event priority if a transaction is cancelled.
        // Shader reload invalidates the whole cache; SchedulerCacheProbe covers it.
        Set(slot, "Fade", 1f);
        Set(slot, "Priority", 0);
        ((Stack<int>)Get(owner, "_free")).Clear();
        var pending = (PlacedLightPendingQueue)Get(owner, "_pending");
        var incoming = source with { X = 1 };
        double[] view = { 1,0,0,0, 0,1,0,0, 0,0,1,0, 0,0,0,1 };
        float[] projection = { 1,0,0,0, 0,1,0,0, 0,0,-1,-1, 0,0,-1,0 };
        Call(pending, "Enqueue", incoming, new Vec3d(), view, projection,
            Get(owner, "_residents"), 100.0, true);
        Call(owner, "AdmitPending", pending.ChoosePlacement(), false);
        Call(owner, "CancelStage");
        if (pending.ChoosePlacement() < 0) throw new Exception("Placement priority lost on stage cancellation");
        Call(owner, "AdmitPending", pending.ChoosePlacement(), false);
        var bake = (PlacedLightBake)Get(owner, "_bake");
        bake.NextFace = 4;
        Set(owner, "_bake", bake);
        Call(owner, "Publish");
        var partial = (List<StaticTerrainShadowMaps.ActiveLight>)Get(owner, "_readyLights");
        if (partial.Count != 1 || partial[0].Source != source) throw new Exception("Partial replacement leaked");
        Complete(owner);
        Call(owner, "Publish");
        if (partial.Count != 1 || partial[0].Source != incoming || partial[0].Fade != 1)
            throw new Exception("Completed placement replacement delayed activation");
        if (GL.GetError() != ErrorCode.NoError) throw new Exception("Placement promotion GL error");
        Console.WriteLine("PASS placement replacement survives cancellation and publishes only complete depth");
    }

    private static void Complete(StaticTerrainShadowMaps owner)
    {
        var bake = (PlacedLightBake)Get(owner, "_bake");
        bake.NextFace = 6;
        Set(owner, "_bake", bake);
        Set(owner, "_incomingCompletedAt", 100.0);
        Set(owner, "_blendStart", 100.0);
        Call(owner, "AdvanceBake", new object[] { null });
    }
}
