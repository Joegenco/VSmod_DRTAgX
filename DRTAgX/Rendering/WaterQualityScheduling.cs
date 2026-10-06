using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Vintagestory.API.Client;

namespace DRTAgX;

/// <summary>Captured Sheyder face-loop adapter; temporal state resets without editing its preferences.</summary>
internal sealed class WaterQualityScheduling : IDisposable
{
    private const string PatchId = "drtagx.water.quality.scheduling";
    private readonly Harmony _harmony = new(PatchId);
    private static WaterQualityScheduling? _current;
    private readonly ICoreClientAPI _api;
    private readonly ConditionalWeakTable<object, FaceCycle> _cycles = new();
    private readonly ConditionalWeakTable<object, History> _histories = new();
    private AccessTools.FieldRef<object, bool>? _historyValid;
    private Func<object, object>? _environment;
    private Action<object>? _clear;
    private int _epoch;
    private int _first, _end = 6;
    private bool _loopSupported;

    internal sealed class FaceCycle
    {
        public FaceCycle() { }
        private int _next;
        internal (int First, int End) Select(bool performance)
        {
            if (!performance) { _next = 0; return (0, 6); }
            int first = _next; _next = (_next + 1) % 6;
            return (first, first + 1);
        }
        internal void Reset() => _next = 0;
    }

    private sealed class History
    {
        public History() { }
        internal object? World, Primary;
        internal int Epoch=-1, Quality=-1, Color, Depth, Width, Height;
        internal double X,Y,Z;
        internal float Fx,Fy,Fz;
    }

    internal WaterQualityScheduling(ICoreClientAPI api)
    {
        _api = api;
        try
        {
            var type = AccessTools.TypeByName("SheyderMod.Features.WaterShader.EnvCubemap.WaterEnvCubemap");
            if (type == null) return;
            var capture = AccessTools.Method(type, "Capture");
            var clear = AccessTools.Method(type, "ClearFaces");
            var release = AccessTools.Method(type, "Release");
            if (capture == null || clear == null || release == null) return;
            _current = this;
            _harmony.Patch(capture, prefix: new HarmonyMethod(typeof(WaterQualityScheduling), nameof(BeginCapture)),
                transpiler: new HarmonyMethod(typeof(WaterQualityScheduling), nameof(FaceLoop)));
            _harmony.Patch(clear, postfix: new HarmonyMethod(typeof(WaterQualityScheduling), nameof(ResetCycle)));
            _harmony.Patch(release, postfix: new HarmonyMethod(typeof(WaterQualityScheduling), nameof(ResetCycle)));
            var water = AccessTools.TypeByName("SheyderMod.Features.WaterShader.WaterSsrRenderer");
            var render = AccessTools.Method(water, "OnRenderFrame");
            if (water != null && render != null)
            {
                _historyValid=AccessTools.FieldRefAccess<object,bool>(AccessTools.Field(water,"_historyValid"));
                var obj=Expression.Parameter(typeof(object));
                _environment=Expression.Lambda<Func<object,object>>(Expression.Convert(Expression.Field(Expression.Convert(obj,water),"_env"),typeof(object)),obj).Compile();
                _clear=Expression.Lambda<Action<object>>(Expression.Call(Expression.Convert(obj,type),clear),obj).Compile();
                _harmony.Patch(render,prefix:new HarmonyMethod(typeof(WaterQualityScheduling),nameof(CheckHistory)));
                var rebuild=AccessTools.Method("Vintagestory.Client.NoObf.ClientPlatformWindows:RebuildFrameBuffers");
                if(rebuild!=null) _harmony.Patch(rebuild,postfix:new HarmonyMethod(typeof(WaterQualityScheduling),nameof(Recreated)));
                api.Event.ReloadShader+=Reload;
            }
            if (!_loopSupported) api.Logger.Notification("[DRTAgX] Cubemap face scheduling inactive: captured six-face loop contract unavailable.");
        }
        catch (Exception ex) { api.Logger.Warning("[DRTAgX] Cubemap scheduling inactive; original capture retained: " + ex.Message); }
    }

    private static void BeginCapture(object __instance)
    {
        var owner = _current;
        if (owner == null || !owner._loopSupported) return;
        // Weak ownership bounds state across world changes; warmed lookups allocate nothing.
        var cycle = owner._cycles.GetOrCreateValue(__instance);
        (owner._first, owner._end) = cycle.Select(FrameQuality.Current.Performance);
    }
    private static void ResetCycle(object __instance)
    {
        if (_current?._cycles.TryGetValue(__instance, out var cycle) == true) cycle.Reset();
    }
    private static int FirstFace() => _current?._first ?? 0;
    private static int EndFace() => _current?._end ?? 6;

    private static void Recreated() { if(_current!=null) ++_current._epoch; }
    private bool Reload() { ++_epoch; return true; }
    private static void CheckHistory(object __instance, EnumRenderStage stage)
    {
        var owner=_current;
        if(stage!=EnumRenderStage.Opaque || owner?._historyValid==null || owner._environment==null || owner._clear==null) return;
        var buffers=owner._api.Render.FrameBuffers;
        var entity=owner._api.World?.Player?.Entity;
        if(buffers==null || buffers.Count==0 || buffers[0]?.ColorTextureIds is not {Length:>0} || entity==null) return;
        var history=owner._histories.GetOrCreateValue(__instance);
        var primary=buffers[0]; var camera=entity.CameraPos;
        var view=owner._api.Render.CameraMatrixOriginf;
        float fx=view[2],fy=view[6],fz=view[10];
        double dx=camera.X-history.X,dy=camera.Y-history.Y,dz=camera.Z-history.Z;
        bool discontinuity=history.Epoch!=owner._epoch || history.Quality!=FrameQuality.Generation ||
            !ReferenceEquals(history.World,owner._api.World) || !ReferenceEquals(history.Primary,primary) ||
            history.Color!=primary.ColorTextureIds[0] || history.Depth!=primary.DepthTextureId || history.Width!=primary.Width || history.Height!=primary.Height ||
            dx*dx+dy*dy+dz*dz>4096 || fx*history.Fx+fy*history.Fy+fz*history.Fz<.5f;
        if(discontinuity)
        {
            owner._historyValid(__instance)=false;
            object environment=owner._environment(__instance);
            // Clear confidence, not HDR color formats. Native teleport/resource resets use the same method.
            var field=AccessTools.Field(environment.GetType(),"_texId");
            // This slow boundary executes only on a discontinuity, never in steady-state submission.
            if(field?.GetValue(environment) is int texture && texture>0)
            {
                using var saved=new HdrPassState();
                owner._clear(environment);
            }
            if(owner._cycles.TryGetValue(environment,out var cycle)) cycle.Reset();
        }
        history.World=owner._api.World; history.Primary=primary;
        history.Epoch=owner._epoch; history.Quality=FrameQuality.Generation;
        history.Color=primary.ColorTextureIds[0]; history.Depth=primary.DepthTextureId; history.Width=primary.Width; history.Height=primary.Height;
        history.X=camera.X; history.Y=camera.Y; history.Z=camera.Z; history.Fx=fx; history.Fy=fy; history.Fz=fz;
    }

    private static IEnumerable<CodeInstruction> FaceLoop(IEnumerable<CodeInstruction> instructions)
    {
        var code = new List<CodeInstruction>(instructions);
        int bound = -1, start = -1, matches = 0;
        for (int i = 1; i+1 < code.Count; ++i)
            if (code[i].opcode == OpCodes.Ldc_I4_6 && (code[i+1].opcode == OpCodes.Blt || code[i+1].opcode == OpCodes.Blt_S))
            { bound = i; ++matches; }
        if (matches != 1) return code;
        object? local = code[bound-1].operand;
        OpCode load = code[bound-1].opcode;
        bool SameLocal(CodeInstruction store) => load == OpCodes.Ldloc_0 ? store.opcode == OpCodes.Stloc_0 :
            load == OpCodes.Ldloc_1 ? store.opcode == OpCodes.Stloc_1 :
            load == OpCodes.Ldloc_2 ? store.opcode == OpCodes.Stloc_2 :
            load == OpCodes.Ldloc_3 ? store.opcode == OpCodes.Stloc_3 :
            (load == OpCodes.Ldloc || load == OpCodes.Ldloc_S) && (store.opcode == OpCodes.Stloc || store.opcode == OpCodes.Stloc_S) && Equals(local, store.operand);
        for (int i = bound-2; i >= 0; --i)
            if (code[i].opcode == OpCodes.Ldc_I4_0 && i+2 < code.Count && SameLocal(code[i+1]) &&
                (code[i+2].opcode == OpCodes.Br || code[i+2].opcode == OpCodes.Br_S)) { start = i; break; }
        if (start < 0) return code;
        // Replace bounds only; face indices, confidence alpha, blend rate and native state remain intact.
        code[start].opcode = OpCodes.Call; code[start].operand = AccessTools.Method(typeof(WaterQualityScheduling), nameof(FirstFace));
        code[bound].opcode = OpCodes.Call; code[bound].operand = AccessTools.Method(typeof(WaterQualityScheduling), nameof(EndFace));
        if (_current != null) _current._loopSupported = true;
        return code;
    }

    public void Dispose()
    {
        _harmony.UnpatchAll(PatchId);
        if (ReferenceEquals(_current, this)) _current = null;
        _cycles.Clear();
        _histories.Clear(); _api.Event.ReloadShader-=Reload;
    }
}
