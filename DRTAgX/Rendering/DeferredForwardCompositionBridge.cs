using System;
using System.Collections;
using HarmonyLib;
using Vintagestory.API.Client;

namespace DRTAgX;

/// <summary>
/// Finishes Sheyder's terrain relighting before forward alpha draws. Blending
/// a lit item into packed terrain color/light metadata corrupts both surfaces.
/// </summary>
internal sealed class DeferredForwardCompositionBridge : IRenderer
{
    private readonly ICoreClientAPI _api;
    private readonly Action? _relight;
    private readonly DeferredTerrainDecalBridge? _decals;

    // Native terrain: .37; DRTAgX shadow/light publication: .38; entities: .4.
    // Relight at this boundary, while the terrain camera/projection are intact.
    public double RenderOrder => 0.39;
    public int RenderRange => int.MaxValue;

    internal DeferredForwardCompositionBridge(ICoreClientAPI api)
    {
        _api = api;
        try
        {
            var system = api.ModLoader.GetModSystem("SheyderMod.SheyderModSystem");
            var renderers = system == null ? null
                : AccessTools.Field(system.GetType(), "_renderers")?.GetValue(system) as IEnumerable;
            if (renderers != null)
                foreach (object renderer in renderers)
                {
                    // Resolve the existing owner once. Its OnRelight consumes
                    // _boundThisFrame, so the original late hook becomes a no-op;
                    // disabled/unready deferred mode also remains a no-op.
                    if (renderer.GetType().FullName != "SheyderMod.Features.Deferred.DeferredRenderer") continue;
                    var method = AccessTools.Method(renderer.GetType(), "OnRelight", Type.EmptyTypes);
                    if (method != null) {
                        _relight = method.CreateDelegate<Action>(renderer);
                        _decals = new DeferredTerrainDecalBridge(api,renderer);
                    }
                    break;
                }
            if (_relight == null)
                throw new MissingMethodException("Sheyder DeferredRenderer.OnRelight");

            api.Event.RegisterRenderer(this, EnumRenderStage.Opaque, "drtagx_deferred_before_forward");
            api.Logger.Notification("[DRT AgX] Deferred terrain relight runs before forward entity/item blending.");
        }
        catch (Exception ex)
        {
            _decals?.Dispose();
            // Retain Sheyder's native late hook if its integration contract changes.
            api.Logger.Warning("[DRT AgX] Early deferred composition unavailable: " + ex.Message);
        }
    }

    public void OnRenderFrame(float deltaTime, EnumRenderStage stage)
    {
        // Render thread only; cached delegate, no reflection/allocations per frame.
        // The existing relight method restores the primary FBO and GL draw state.
        // Alt+8 measures placed-owned work. This native pass also contains sun,
        // moving lights and other effects, so keep its separate Alt+9 scope.
        int movingTiming = MovingLightGpuProfile.Relight.Begin(MovingLightGpuProfile.Enabled);
        MovingLightGpuProfile.Total.BeginRelight();
        try { _relight?.Invoke(); _decals?.Flush(); }
        finally {
            MovingLightGpuProfile.Relight.End(movingTiming);
            MovingLightGpuProfile.Total.EndRelight();
        }
    }

    public void Dispose()
    {
        _decals?.Dispose();
        _api.Event.UnregisterRenderer(this, EnumRenderStage.Opaque);
    }
}
