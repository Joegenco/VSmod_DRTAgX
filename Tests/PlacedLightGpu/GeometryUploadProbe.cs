using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using DRTAgX;
using Vintagestory.API.Common;
using Vintagestory.Client.NoObf;
using static StaticCacheProbeFixture;

// Verify native metadata and the installed Harmony adapter against actual game
// types. Calling the postfix supplies an upload; no engine body is decompiled.
internal static class GeometryUploadProbe
{
    internal static void Run()
    {
        using var fixture = new StaticCacheProbeFixture();
        using var bridge = new PlacedLightGeometryBridge(fixture.Api, fixture.Sources);
        Check(fixture.Sources.NativeGeometryNotifications, "native terrain upload hook installs on the frozen 1.22.7 signature");
        var assembly = typeof(ChunkRenderer).Assembly;
        var tessellated = assembly.GetType("Vintagestory.Client.NoObf.TesselatedChunk", true);
        var clientChunk = assembly.GetType("Vintagestory.Client.NoObf.ClientChunk", true);
        var chunk = (IWorldChunk)RuntimeHelpers.GetUninitializedObject(clientChunk);
        var upload = RuntimeHelpers.GetUninitializedObject(tessellated);
        GC.SuppressFinalize(chunk); GC.SuppressFinalize(upload);
        tessellated.GetField("chunk", Fields).SetValue(upload, chunk);
        var coordinates = Get(fixture.Sources, "_chunkCoordinates");
        var coordinateType = typeof(StaticLightSources).GetNestedType("ChunkCoordinates", BindingFlags.NonPublic);
        var coordinate = Activator.CreateInstance(coordinateType, Fields, null,
            new object[] { new StaticLightSources.ChunkKey(0, 0, -1) }, null);
        Call(coordinates, "Add", chunk, coordinate);
        var postfix = typeof(PlacedLightGeometryBridge).GetMethod("Uploaded", BindingFlags.Static | BindingFlags.NonPublic);
        var prefix = typeof(PlacedLightGeometryBridge).GetMethod("Uploading", BindingFlags.Static | BindingFlags.NonPublic);
        object[] captured = { upload, chunk, null };
        for (int i = 0; i < 1000; i++) {
            prefix?.Invoke(null, captured);
            postfix.Invoke(null, prefix == null ? new[] { upload } : new[] { captured[2] });
        }
        Call(fixture.Sources, "DrainNotifications");
        Check(fixture.Changes.Count == 1 && fixture.Changes[0] == new StaticLightSources.ChunkKey(0, 0, -1) &&
            fixture.Sources.NotificationsThisFrame == 1 && fixture.Sources.ChunkScansThisFrame == 0,
            "native upload adapter coalesces chunk identities without metadata rescans");
        fixture.Changes.Clear();
        ((HashSet<StaticLightSources.ChunkKey>)Get(fixture.Sources, "_changedSet")).Clear();
        // The live debugger showed that native upload clears these fields before
        // returning. Installation alone does not prove the notification survives.
        prefix?.Invoke(null, captured);
        tessellated.GetField("chunk", Fields).SetValue(upload, null);
        postfix.Invoke(null, prefix == null ? new[] { upload } : new[] { captured[2] });
        Call(fixture.Sources, "DrainNotifications");
        Check(fixture.Changes.Count == 1, "consumed native upload input still invalidates cached caster depth");
        bridge.Dispose();
        Check(!fixture.Sources.NativeGeometryNotifications, "upload bridge disposal restores fallback ownership");
    }
}
