using System;
using HarmonyLib;
using OpenTK.Graphics.OpenGL4;
using Vintagestory.API.Client;

namespace DRTAgX;

/// <summary>
/// Marks the native sun draw in standard without changing ordinary world/UI draws.
/// Vintage Story 1.22.7 uses standard for the sun and celestialobject for the moon.
/// </summary>
internal sealed class CelestialSunBindings : IDisposable
{
    private const string PatchId = "drtagx.celestial.sun";
    private static CelestialSunBindings? _current;
    private readonly ICoreClientAPI _api;
    private readonly Harmony _harmony = new(PatchId);
    private IShaderProgram? _shader;
    private int _program, _location = -1;

    private readonly record struct DrawScope(int Program, int Location, int Previous);

    internal CelestialSunBindings(ICoreClientAPI api)
    {
        _api = api;
        try
        {
            var draw = AccessTools.Method("Vintagestory.Client.NoObf.SystemRenderSunMoon:OnRenderFrame3D");
            if (draw == null) throw new MissingMethodException("SystemRenderSunMoon.OnRenderFrame3D");
            _harmony.Patch(draw,
                prefix: new HarmonyMethod(typeof(CelestialSunBindings), nameof(BeforeDraw)),
                finalizer: new HarmonyMethod(typeof(CelestialSunBindings), nameof(AfterDraw)));
            _current = this;
            api.Event.ReloadShader += Reload;
            api.Logger.Notification("[DRT AgX] Native standard sun draw uses celestial HDR composition.");
        }
        catch (Exception ex)
        {
            // Missing native integration keeps the normal standard path available.
            _harmony.UnpatchAll(PatchId);
            api.Logger.Warning("[DRT AgX] Celestial sun binding unavailable: " + ex.Message);
        }
    }

    private static void BeforeDraw(out DrawScope __state)
    {
        __state = default;
        var owner = _current;
        if (owner == null) return;
        IShaderProgram? shader = owner._api.Render.GetEngineShader(EnumShaderProgram.Standard);
        if (shader == null || shader.Disposed || shader.LoadError) return;
        if (!ReferenceEquals(shader, owner._shader) || shader.ProgramId != owner._program)
        {
            owner._shader = shader;
            owner._program = shader.ProgramId;
            owner._location = GL.GetUniformLocation(shader.ProgramId, "drtSunSprite");
        }
        if (owner._location < 0) return;
        // Render thread only. Direct program uniforms change no shader, texture,
        // or framebuffer binding; preserve prior state even for nested callbacks.
        GL.GetUniform(owner._program, owner._location, out int previous);
        __state = new DrawScope(owner._program, owner._location, previous);
        GL.ProgramUniform1(__state.Program, __state.Location, 1);
    }

    private static void AfterDraw(DrawScope __state)
    {
        // Harmony finalizers run on normal return and exceptions. The flag must
        // never escape into terrain, entities, first-person items or GUI draws.
        if (__state.Program != 0 && GL.IsProgram(__state.Program))
            GL.ProgramUniform1(__state.Program, __state.Location, __state.Previous);
    }

    private bool Reload()
    {
        // Native programs may be recompiled in place; invalidate cached locations
        // even if a reload reuses the shader object or its numeric program name.
        _shader = null;
        _program = 0;
        _location = -1;
        return true;
    }

    public void Dispose()
    {
        _api.Event.ReloadShader -= Reload;
        _harmony.UnpatchAll(PatchId);
        if (_current == this) _current = null;
    }
}
