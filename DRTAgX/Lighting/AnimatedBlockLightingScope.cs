using System;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace DRTAgX;

/// <summary>Identifies placed block animations in the shared entity shader without changing light gains.</summary>
internal sealed class AnimatedBlockLightingScope : IDisposable
{
    private static AnimatedBlockLightingScope? _instance;
    private readonly Harmony _harmony = new("drtagx.animated.block.receiver");
    private int _depth;

    internal static bool Active => _instance?._depth > 0;

    internal AnimatedBlockLightingScope(ICoreClientAPI api)
    {
        _instance = this;
        // The public block renderer also covers modded animatable furniture.
        // Scope by renderer, not block names: mobs and first-person hands share
        // the shader but must keep their inexpensive voxel-facing lighting.
        var render = AccessTools.Method(typeof(AnimatableRenderer), nameof(AnimatableRenderer.OnRenderFrame));
        _harmony.Patch(render,
            prefix: new HarmonyMethod(typeof(AnimatedBlockLightingScope), nameof(Begin)),
            finalizer: new HarmonyMethod(typeof(AnimatedBlockLightingScope), nameof(End)));
    }

    private static void Begin(AnimatableRenderer __instance, EnumRenderStage stage, out bool __state)
    {
        __state = false;
        var owner = _instance;
        if (owner == null || !__instance.ShouldRender || !__instance.LightAffected ||
            stage is EnumRenderStage.ShadowNear or EnumRenderStage.ShadowFar) return;
        __state = true;
        // Native culling can discard this callback before using any shader.
        // Publish only the CPU scope here; the actual shader Use hook uploads
        // the block marker when there is a draw, including after shader reload.
        owner._depth++;
    }

    private static void End(bool __state)
    {
        // Finalizers run even after a failed draw. Do not leak the block's PLS
        // receiving policy into subsequent creature, hand or preview draws.
        var owner = _instance;
        if (__state && owner != null) owner._depth--;
    }

    public void Dispose()
    {
        _harmony.UnpatchAll(_harmony.Id);
        if (_instance == this) _instance = null;
    }
}