using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using DRTAgX;
using HarmonyLib;
using OpenTK.Graphics.OpenGL4;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.Client.NoObf;

// Synthetic source/residency state, real production owners and GL resources.
// No native terrain draws or game-wide performance enter this fixture.
internal sealed class StaticCacheProbeFixture : IDisposable
{
    internal const BindingFlags Fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    internal readonly StaticLightSources Sources;
    internal readonly StaticTerrainShadowMaps Maps;
    internal readonly ICoreClientAPI Api;
    internal readonly ClientMain World;
    internal readonly Vec3d Camera = new();
    internal readonly double[] View = { 1,0,0,0, 0,1,0,0, 0,0,1,0, 0,0,0,1 };
    internal readonly float[] Projection = { 1,0,0,0, 0,1,0,0, 0,0,-1,-1, 0,0,-1,0 };
    internal readonly float[] Origin = { 1,0,0,0, 0,1,0,0, 0,0,1,0, 0,0,0,1 };
    internal readonly List<FrameBufferRef> Buffers = new();
    internal int Width = 32, Height = 32;
    internal readonly int Texture;
    internal static double Now => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
    private static readonly Dictionary<ClientMain, double[]> Views = new(ReferenceEqualityComparer.Instance);
    private static Harmony _viewPatch;
    private const string ViewPatchId = "drtagx.probe.placedview";

    internal StaticCacheProbeFixture(int capacity = 1, string computePath = null)
    {
        var assetsApi = ProbeAssets.Api(computePath == null ? [] : File.ReadAllBytes(computePath));
        World = (ClientMain)RuntimeHelpers.GetUninitializedObject(typeof(ClientMain));
        GC.SuppressFinalize(World); // This input adapter owns no native world resources.
        var events = SurfaceApiProxy.Make<IClientEventAPI>((_, _) => null);
        var render = SurfaceApiProxy.Make<IRenderAPI>((method, _) => method.Name switch {
            "get_FrameWidth" => Width, "get_FrameHeight" => Height,
            "get_CurrentProjectionMatrix" => Projection, "get_CameraMatrixOriginf" => Origin,
            "get_FrameBuffers" => Buffers, _ => throw new NotSupportedException(method.Name)
        });
        Api = SurfaceApiProxy.Make<ICoreClientAPI>((method, _) => method.Name switch {
            "get_World" => World, "get_Event" => events, "get_Render" => render,
            "get_Assets" => assetsApi.Assets, "get_Logger" => assetsApi.Logger,
            _ => throw new NotSupportedException(method.Name)
        });
        Sources = new StaticLightSources(Api);
        Maps = new StaticTerrainShadowMaps(null);
        typeof(StaticTerrainShadowMaps).GetProperty("Capacity", Fields).SetValue(Maps, capacity);
        Maps.Invalidate();
        Texture = GL.GenTexture();
        GL.BindTexture(TextureTarget.Texture2DArray, Texture);
        GL.TexStorage3D(TextureTarget3d.Texture2DArray, 1, SizedInternalFormat.DepthComponent24,
            StaticTerrainShadowMaps.FaceSize, StaticTerrainShadowMaps.FaceSize, (capacity + 1) * 6);
        Set(Maps, "_texture", Texture);
        Set(Maps, "_framebuffer", GL.GenFramebuffer());
        Set(Maps, "_time", Now);
    }

    internal void RequireViewMatrix()
    {
        // Supply the frozen public matrix contract without constructing a game
        // or assuming undocumented private backing fields. Scope to these input
        // instances, and remove the prefix when the last fixture is disposed.
        if (_viewPatch == null) {
            _viewPatch = new Harmony(ViewPatchId);
            _viewPatch.Patch(typeof(ClientMain).GetProperty("CurrentModelViewMatrixd").GetMethod,
                prefix: new HarmonyMethod(typeof(StaticCacheProbeFixture), nameof(NativeView)));
        }
        Views[World] = View;
        if (!ReferenceEquals(World.CurrentModelViewMatrixd, View))
            throw new Exception("Native matrix input adapter failed");
    }
    private static bool NativeView(ClientMain __instance, ref double[] __result)
    {
        if (!Views.TryGetValue(__instance, out var view)) return true;
        __result = view; return false;
    }

    internal object Slot(int index = 0) => ((Array)Get(Maps, "_slots")).GetValue(index);
    internal PlacedLightPendingQueue Pending => (PlacedLightPendingQueue)Get(Maps, "_pending");
    internal List<StaticLightSources.Source> Candidates => (List<StaticLightSources.Source>)Get(Sources, "_sources");
    internal List<StaticLightSources.ChunkKey> Changes => (List<StaticLightSources.ChunkKey>)Get(Sources, "_changedChunks");
    internal List<StaticLightSources.Position> Edits => (List<StaticLightSources.Position>)Get(Sources, "_blockEdits");
    internal void Source(StaticLightSources.Source source, bool active = true)
    {
        ((Dictionary<StaticLightSources.Position, StaticLightSources.Source>)Get(Sources, "_byPosition"))
            [StaticLightSources.Identity(source)] = source;
        if (active) Candidates.Add(source);
        Bump(Sources, "Revision");
    }
    internal void Remove(StaticLightSources.Source source)
    {
        ((Dictionary<StaticLightSources.Position, StaticLightSources.Source>)Get(Sources, "_byPosition"))
            .Remove(StaticLightSources.Identity(source));
        Candidates.RemoveAll(s => StaticLightSources.Identity(s) == StaticLightSources.Identity(source));
        Bump(Sources, "Revision");
    }
    internal void Resident(StaticLightSources.Source source, int index = 0)
    {
        var free = (Stack<int>)Get(Maps, "_free");
        if (free.Pop() != index) throw new Exception("Fixture resident order");
        Call(Maps, "Assign", index, source);
        Set(Slot(index), "Valid", true); Set(Slot(index), "Fade", 1f);
        Set(Slot(index), "FadeAt", Now - 60); Set(Slot(index), "CompletedAt", Now - 60);
        Set(Slot(index), "LastHit", Now - 60);
    }
    internal void Update() => Maps.UpdateAndRender(Api, Sources, Camera, View, Projection, 1f / 60);
    internal void Settle() { for (int i = 0; i < 5; i++) Update(); }
    internal void ViewChanged() => Bump(Sources, "ViewRevision");
    internal void Wake() => Set(Maps, "_nextSchedulerWake", 0.0);
    internal static object Get(object target, string name) => target.GetType().GetField(name, Fields).GetValue(target);
    internal static void Set(object target, string name, object value) => target.GetType().GetField(name, Fields).SetValue(target, value);
    internal static object Call(object target, string name, params object[] args) => target.GetType().GetMethod(name, Fields).Invoke(target, args);
    internal static void Bump(object target, string name)
    {
        var property = target.GetType().GetProperty(name, Fields);
        property.SetValue(target, (int)property.GetValue(target) + 1);
    }
    internal static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception(name);
        Console.WriteLine("PASS " + name);
    }
    public void Dispose()
    {
        Sources.Dispose(); Maps.Dispose(); Views.Remove(World);
        if (Views.Count == 0 && _viewPatch != null) {
            _viewPatch.Unpatch(typeof(ClientMain).GetProperty("CurrentModelViewMatrixd").GetMethod,
                HarmonyPatchType.Prefix, ViewPatchId);
            _viewPatch = null;
        }
    }
}
