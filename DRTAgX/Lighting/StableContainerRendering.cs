using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace DRTAgX;

// Native containers normally move their entire mesh between chunk terrain and
// AnimatableRenderer when a lid starts/stops. Keep supported containers on the
// same native renderer in both poses, so an animation never changes PLS casters.
// Doors retain native terrain transitions because their changing occlusion is real.
internal sealed partial class StableContainerRendering : IDisposable
{
    private const string PatchId = "drtagx.stablecontainers";
    private static StableContainerRendering? _current;
    private readonly ICoreClientAPI _api;
    private readonly Action<BlockPos> _casterChanged;
    private readonly Harmony _harmony = new(PatchId);
    private readonly ConditionalWeakTable<BlockEntityAnimationUtil, State> _utilities = new();
    private readonly ConditionalWeakTable<BlockEntityAnimationUtil, State> _rejected = new();
    private readonly ConditionalWeakTable<AnimatableRenderer, State> _renderers = new();
    private readonly List<WeakReference<State>> _states = new();
    private readonly object _stateLock = new();
    private long _frame;

    private sealed class State
    {
        internal required BlockEntity BlockEntity;
        internal required BlockEntityAnimationUtil Utility;
        internal AnimatableRenderer? Renderer;
        internal MultiTextureMeshRef? ShadowMesh;
        internal readonly float[] LocalModel = Mat4f.Create();
        internal readonly float[] CustomModel = new float[16];
        internal float RestRadius, Radius, ScaleX, ScaleY, ScaleZ, Yaw;
        internal double X, Y, Z;
        internal bool HasTransform, HasCustomTransform;
        internal long RefreshFrame = -1;
    }

    internal StableContainerRendering(ICoreClientAPI api, Action<BlockPos> casterChanged)
    {
        _api = api;
        _casterChanged = casterChanged;
        try
        {
            Type container = AccessTools.TypeByName("Vintagestory.GameContent.BlockEntityGenericTypedContainer")
                             ?? throw new TypeLoadException("BlockEntityGenericTypedContainer");
            MethodInfo tessellate = AccessTools.DeclaredMethod(container, "OnTesselation",
                                        new[] { typeof(ITerrainMeshPool), typeof(ITesselatorAPI) })
                                    ?? throw new MissingMethodException(container.FullName, "OnTesselation");
            MethodInfo stateChange = AccessTools.DeclaredMethod(typeof(BlockEntityAnimationUtil),
                                         "OnAnimationsStateChange", new[] { typeof(bool) })
                                     ?? throw new MissingMethodException(
                                         "BlockEntityAnimationUtil.OnAnimationsStateChange");
            MethodInfo draw = AccessTools.Method(typeof(AnimatableRenderer), nameof(IRenderer.OnRenderFrame),
                                  new[] { typeof(float), typeof(EnumRenderStage) })
                              ?? throw new MissingMethodException("AnimatableRenderer.OnRenderFrame");
            MethodInfo dispose = AccessTools.DeclaredMethod(typeof(AnimatableRenderer), nameof(IDisposable.Dispose))
                                 ?? throw new MissingMethodException("AnimatableRenderer.Dispose");
            _current = this;
            _harmony.Patch(tessellate,
                prefix: new HarmonyMethod(typeof(StableContainerRendering), nameof(Tessellating)),
                finalizer: new HarmonyMethod(typeof(StableContainerRendering), nameof(Tessellated)));
            _harmony.Patch(stateChange,
                prefix: new HarmonyMethod(typeof(StableContainerRendering), nameof(AnimationStateChanging)));
            _harmony.Patch(draw,
                prefix: new HarmonyMethod(typeof(StableContainerRendering), nameof(Rendering))
                    { priority = Priority.First });
            _harmony.Patch(dispose,
                prefix: new HarmonyMethod(typeof(StableContainerRendering), nameof(RendererDisposing)));
            api.Logger.Notification(
                "[DRT AgX] Container lids retain one native renderer without rebaking placed shadows.");
        }
        catch (Exception ex)
        {
            Dispose();
            api.Logger.Warning("[DRT AgX] Stable container rendering unavailable; preserving native containers: " +
                               ex.Message);
        }
    }

    private static void Tessellating(BlockEntity __instance, ref ITerrainMeshPool __0,
        out SubmissionPool? __state)
    {
        __state = null;
        var owner = _current;
        // The verified native body submits just its own model after this one
        // behavior. Custom behavior meshes and custom animation utilities retain
        // their original path. Derived containers calling the native base keep
        // any extra submissions made by their override outside this scoped pool.
        if (owner == null || __instance.Api?.Side != EnumAppSide.Client ||
            __instance.Behaviors.Count != 1 ||
            __instance.Behaviors[0].GetType() != typeof(BEBehaviorAnimatable) ||
            __instance.Behaviors[0] is not BEBehaviorAnimatable behavior ||
            behavior.animUtil == null || behavior.animUtil.GetType() != typeof(BlockEntityAnimationUtil) ||
            owner._rejected.TryGetValue(behavior.animUtil, out _)) return;
        __state = new SubmissionPool(owner, __instance, behavior.animUtil, __0);
        __0 = __state;
    }

    private static Exception? Tessellated(Exception? __exception, bool __result, SubmissionPool? __state)
    {
        __state?.Complete(__exception == null && __result);
        return __exception;
    }

    private bool RetainRenderer(BlockEntity blockEntity, BlockEntityAnimationUtil utility, MeshData? restMesh)
    {
        AnimatableRenderer? renderer = utility.renderer;
        if (!ReferenceEquals(_current, this) || renderer == null || utility.animator == null ||
            renderer.GetType() != typeof(AnimatableRenderer) ||
            renderer.mtmeshrefOpaque is { Disposed: true } || renderer.mtmeshrefTransparent is { Disposed: true })
            return false;
        float radius = RestMeshRadius(restMesh);
        if (restMesh != null && radius <= 0) return false;
        lock (_stateLock)
        {
            // Tessellation can finish concurrently with mod disposal. Never
            // suppress terrain after the hook retaining its model was removed.
            if (!ReferenceEquals(_current, this) || _rejected.TryGetValue(utility, out _)) return false;
            if (!_utilities.TryGetValue(utility, out State? state))
            {
                // Capture only scalar bounds, never retain tessellator arrays.
                // First encountered mid-animation: keep native behavior until
                // a rest mesh is available to establish its exact footprint.
                if (radius <= 0 || blockEntity.Pos.dimension != 0) return false;
                state = new State { BlockEntity = blockEntity, Utility = utility, RestRadius = radius };
                _utilities.Add(utility, state);
                _states.Add(new WeakReference<State>(state));
            }

            if (!ReferenceEquals(state.Renderer, renderer))
            {
                if (state.Renderer != null) _renderers.Remove(state.Renderer);
                state.Renderer = renderer;
                _renderers.Add(renderer, state);
                Volatile.Write(ref state.RefreshFrame, -1);
            }

            if (restMesh != null)
            {
                if (radius > 0 && radius != state.RestRadius)
                {
                    state.RestRadius = radius;
                    state.HasTransform = false;
                    Volatile.Write(ref state.RefreshFrame, -1);
                }
            }
        }

        return true;
    }

    private void ReleaseRenderer(BlockEntityAnimationUtil utility)
    {
        State state;
        lock (_stateLock)
        {
            // Unsupported containers that never opted in keep their untouched
            // native path, without an extra dirty notification or retained state.
            if (!_utilities.TryGetValue(utility, out state!)) return;
            _utilities.Remove(utility);
            _rejected.Add(utility, state);
            // Keep the renderer mapping until terrain is back. Native animation
            // updates may otherwise hide the old model before its chunk upload.
        }

        // Completion is invoked by the terrain worker. Native dirty scheduling
        // and the eventual render-policy handoff belong on the client thread.
        _api.Event.EnqueueMainThreadTask(() =>
        {
            if (!ReferenceEquals(_current, this)) return; // Dispose owns restoration now.
            if (!ReferenceEquals(state.BlockEntity.Api, _api) ||
                !ReferenceEquals(_api.World.BlockAccessor.GetBlockEntity(state.BlockEntity.Pos), state.BlockEntity))
            {
                lock (_stateLock)
                    if (state.Renderer != null)
                    {
                        _renderers.Remove(state.Renderer);
                        RetireCaster(state);
                    }

                return; // Native chunk unloading owns disposal of the old model.
            }

            _api.World.BlockAccessor.MarkBlockDirty(state.BlockEntity.Pos, () =>
            {
                lock (_stateLock)
                {
                    if (state.Renderer != null) _renderers.Remove(state.Renderer);
                    RetireCaster(state);
                    if (ReferenceEquals(state.Utility.renderer, state.Renderer) && state.Renderer != null)
                        state.Renderer.ShouldRender = state.Utility.activeAnimationsByAnimCode.Count > 0 ||
                                                      state.Utility.animator?.ActiveAnimationCount > 0;
                }
            });
        }, "drtagx-stable-container-fallback");
    }

    private static bool AnimationStateChanging(BlockEntityAnimationUtil __instance)
    {
        var owner = _current;
        if (owner == null || !owner._utilities.TryGetValue(__instance, out State? state) ||
            !ReferenceEquals(state.Renderer, __instance.renderer)) return true;
        // Only this native callback's animation-only MarkBlockDirty is omitted.
        // Inventory changes, block replacements, chunk edits and emitter updates
        // continue through their untouched native events and upload fingerprints.
        return false;
    }

    private static bool Rendering(AnimatableRenderer __instance, EnumRenderStage stage)
    {
        var owner = _current;
        if (owner == null || !owner._renderers.TryGetValue(__instance, out State? state) ||
            !ReferenceEquals(state.Utility.renderer, __instance)) return true;
        // Only our retained idle models lost native chunk culling. Reject their
        // camera draws before shader switches, light queries and bone uploads.
        // Never use the camera frustum to reject native sun-shadow callbacks.
        if (stage is EnumRenderStage.Opaque or EnumRenderStage.OIT && !owner.CameraVisible(state)) return false;
        // Native registers the renderer after main-thread GPU initialization.
        // Its own readiness checks still run. At rest AnimatorBase.Matrices
        // supplies the constructor's identity default pose; no extra tick occurs.
        __instance.ShouldRender = true;
        return true;
    }

    public void Dispose()
    {
        if (ReferenceEquals(_current, this)) _current = null;
        _harmony.UnpatchAll(PatchId);
        lock (_stateLock)
        {
            // Mod reload can happen with a live world. Let native chunk upload
            // restore the rest mesh before switching an idle renderer off.
            foreach (WeakReference<State> weak in _states)
            {
                if (!weak.TryGetTarget(out State? state)) continue;
                try
                {
                    if (!ReferenceEquals(state.BlockEntity.Api, _api) ||
                        !ReferenceEquals(_api.World.BlockAccessor.GetBlockEntity(state.BlockEntity.Pos),
                            state.BlockEntity))
                        continue;
                    _api.World.BlockAccessor.MarkBlockDirty(state.BlockEntity.Pos, () =>
                    {
                        if (state.Utility.renderer is { } renderer)
                            renderer.ShouldRender = state.Utility.activeAnimationsByAnimCode.Count > 0 ||
                                                    state.Utility.animator?.ActiveAnimationCount > 0;
                    });
                }
                catch
                {
                    // World shutdown already owns native mesh/renderer disposal.
                }
            }

            _states.Clear();
            _utilities.Clear();
            _rejected.Clear();
            _renderers.Clear();
        }
    }
}