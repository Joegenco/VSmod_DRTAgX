using System;
using HarmonyLib;
using OpenTK.Graphics.OpenGL4;
using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace DRTAgX;

/// <summary>Diffuse calibration for the native animated block renderer, including modded blocks.</summary>
internal sealed class AnimatedBlockLightBindings : IDisposable
{
    private static AnimatedBlockLightBindings? _instance;
    private readonly ICoreClientAPI _api;
    private readonly Harmony _harmony = new("drtagx.animated.block.light");
    private readonly IShaderProgram?[] _programs = new IShaderProgram?[2];
    private readonly int[] _ids = new int[2], _locations = [-1, -1];
    private int _depth;

    internal AnimatedBlockLightBindings(ICoreClientAPI api)
    {
        _api = api;
        _instance = this;
        // Public API renderer: its whole-model GetLightRGBs sample lacks the
        // terrain mesh's baked vertex light/occlusion. A diffuse-only artistic
        // calibration keeps opening furniture from becoming conspicuously bright.
        // This is deliberately scoped by renderer, not a block-code whitelist or
        // an entity-wide multiplier. Creature and first-person draws stay native.
        var render = AccessTools.Method(typeof(AnimatableRenderer), nameof(AnimatableRenderer.OnRenderFrame));
        _harmony.Patch(render,
            prefix: new HarmonyMethod(typeof(AnimatedBlockLightBindings), nameof(Begin)),
            finalizer: new HarmonyMethod(typeof(AnimatedBlockLightBindings), nameof(End)));
        api.Event.ReloadShader += Reload;
    }

    private static void Begin(AnimatableRenderer __instance, EnumRenderStage stage, out bool __state)
    {
        __state = false;
        var owner = _instance;
        if (owner == null || !__instance.ShouldRender || !__instance.LightAffected ||
            stage is EnumRenderStage.ShadowNear or EnumRenderStage.ShadowFar) return;
        __state = true;
        if (owner._depth++ == 0) owner.SetGain(0.66f);
    }

    private static void End(bool __state)
    {
        // A Harmony finalizer also runs on an aborted draw. Restoring both
        // programs prevents this block-only setting leaking into mobs or hands.
        var owner = _instance;
        if (__state && owner != null && --owner._depth == 0) owner.SetGain(1f);
    }

    private void SetGain(float gain)
    {
        // Render thread only. DSA updates neither the current program nor any
        // GL binding, and each native program's location is cached across draws.
        for (int i = 0; i < 2; ++i)
        {
            var shader = _api.Render.GetEngineShader(i == 0
                ? EnumShaderProgram.Entityanimated
                : EnumShaderProgram.Entityanimated_Oit);
            if (shader == null || shader.Disposed || shader.LoadError) continue;
            int id = shader.ProgramId;
            if (!ReferenceEquals(shader, _programs[i]) || id != _ids[i])
            {
                _programs[i] = shader;
                _ids[i] = id;
                _locations[i] = GL.GetUniformLocation(id, "drtAnimatedBlockLightGain");
            }

            if (_locations[i] >= 0) GL.ProgramUniform1(id, _locations[i], gain);
        }
    }

    private bool Reload()
    {
        // Native reloads may relink a retained program object/handle. Its old
        // uniform location is no longer valid even when its numeric ID matches.
        Array.Clear(_programs);
        return true;
    }

    public void Dispose()
    {
        _api.Event.ReloadShader -= Reload;
        _harmony.UnpatchAll(_harmony.Id);
        if (_instance == this) _instance = null;
    }
}