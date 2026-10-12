using System;
using System.Collections.Generic;
using HarmonyLib;
using OpenTK.Graphics.OpenGL4;
using Vintagestory.API.Client;
using Vintagestory.API.MathTools;
using Vintagestory.Client.NoObf;

namespace DRTAgX;

internal sealed partial class DeferredReceiverResources
{
    private static DeferredReceiverResources? _current;
    internal static DeferredReceiverResources? Current => _current;
    private readonly Harmony _harmony = new("drtagx.deferred.receiver-resources");
    private readonly Dictionary<int, Locations> _locations = new();

    private static readonly Func<MeshDataPool, Vec3i?> PoolOrigin =
        NativeShadowFields.Reader<MeshDataPool, Vec3i>("poolOrigin");

    private readonly int[] _relightTexture = new int[3],
        _relightSampler = new int[3],
        _replayTexture = new int[3],
        _replaySampler = new int[3];

    private bool _replayActive;

    private sealed class Locations(int program)
    {
        internal readonly int Geometry = GL.GetUniformLocation(program, "drtReceiverGeometryEnabled");
        internal readonly int Pool = GL.GetUniformLocation(program, "drtReceiverPoolOffset");
        internal readonly int Payload = GL.GetUniformLocation(program, "drtDeferredIlluminationPayload");
        internal readonly int Plane = GL.GetUniformLocation(program, "drtReceiverPlane");
        internal readonly int Bounds = GL.GetUniformLocation(program, "drtReceiverBounds");
        internal readonly int PlaneReady = GL.GetUniformLocation(program, "drtReceiverPlaneReady");
        internal readonly int Irradiance = GL.GetUniformLocation(program, "drtDeferredDecalIrradiance");
        internal readonly int Replay = GL.GetUniformLocation(program, "drtDeferredDecalLighting");
    }

    private Locations GetLocations(int program)
    {
        if (!_locations.TryGetValue(program, out var locations)) _locations.Add(program, locations = new(program));
        return locations;
    }

    private void InstallBindings()
    {
        // Select the declared per-pool RenderMesh submission boundary rather
        // than the convenience Draw/culling wrapper. Runtime capture observed
        // zero Draw calls; metadata verifies this exact signature and the same
        // integer poolOrigin. Opt-in counters verify native runtime reachability.
        var draw = AccessTools.Method(typeof(MeshDataPool), "RenderMesh", new[] { typeof(IRenderAPI) })
                   ?? throw new MissingMethodException("MeshDataPool.RenderMesh(IRenderAPI)");
        var use = AccessTools.Method(typeof(ShaderProgramBase), nameof(IShaderProgram.Use), Type.EmptyTypes)
                  ?? throw new MissingMethodException("ShaderProgramBase.Use");
        var stop = AccessTools.Method(typeof(ShaderProgramBase), nameof(IShaderProgram.Stop), Type.EmptyTypes)
                   ?? throw new MissingMethodException("ShaderProgramBase.Stop");
        try
        {
            _harmony.Patch(draw, prefix: new HarmonyMethod(typeof(DeferredReceiverResources), nameof(BeforePool)),
                finalizer: new HarmonyMethod(typeof(DeferredReceiverResources), nameof(AfterPool)));
            _harmony.Patch(use, postfix: new HarmonyMethod(typeof(DeferredReceiverResources), nameof(AfterUse)));
            _harmony.Patch(stop, prefix: new HarmonyMethod(typeof(DeferredReceiverResources), nameof(BeforeRelightStop))
            {
                before = ["drtagx.atmosphere.bindings"]
            });
            _current = this;
        }
        catch
        {
            _harmony.UnpatchAll(_harmony.Id);
            throw;
        }
    }

    private static void BeforePool(MeshDataPool __instance, out int __state)
    {
        __state = 0;
        var owner = _current;
        if (owner == null) return;
        var shader = owner._api.Render.CurrentActiveShader;
        if (shader == null)
        {
            return;
        }

        // Keep the exact production predicate unchanged until the opt-in
        // runtime observations identify why authored receivers are missing.
        if (shader.PassName is not ("chunkopaque" or "chunktopsoil"))
        {
            return;
        }

        var locations = owner.GetLocations(shader.ProgramId);
        if (locations.Geometry < 0)
        {
            return;
        }

        __state = shader.ProgramId;
        GL.ProgramUniform1(__state, locations.Geometry, 0);
        var cache = PlacedLightSurfaceCache.Current;
        if (!owner._prepared)
        {
            return;
        }

        if (!owner._geometryActive && !owner._replayActive)
        {
            return;
        }

        if (!owner.AnchorMatches(cache))
        {
            return;
        }

        int dimension = NativeShadowFields.Dimension(__instance);
        if (dimension != 0)
        {
            return;
        }

        var origin = PoolOrigin(__instance);
        if (origin == null)
        {
            return;
        }

        // The frozen native pool origin is Vec3i, including dimension encoding.
        // Mini-dimensions have separate transforms and intentionally stay native.
        long x = (long)origin.X - (owner._anchorX >> 4),
            y = (long)origin.Y - (owner._anchorY >> 4),
            z = (long)origin.Z - (owner._anchorZ >> 4);

        if (Math.Abs(x) > 65535 || Math.Abs(y) > 65535 || Math.Abs(z) > 65535)
        {
            return;
        }

        if (locations.Pool >= 0) GL.ProgramUniform3(__state, locations.Pool, (int)x, (int)y, (int)z);
        GL.ProgramUniform1(__state, locations.Geometry, 1);
    }

    private static Exception? AfterPool(Exception? __exception, int __state)
    {

        if (__state != 0 && _current is { } owner)
        {
            var locations = owner.GetLocations(__state);
            if (locations.Geometry >= 0)
            {
                GL.ProgramUniform1(__state, locations.Geometry, 0);
            }
        }

        return __exception;
    }

    // Harmony injects the shader by this reserved parameter name.
    // ReSharper disable once InconsistentNaming
    private static void BeforeRelightStop(ShaderProgramBase __instance)
    {
        if (__instance.PassName != "deferredlighting") return;
        // Release the auxiliary MRT/texture loan at the end of its draw. Sheyder
        // restores all four native targets immediately after Stop; cleanup in
        // Flush would overwrite that restoration with the saved relight layout,
        // whose normal target is NONE, losing every subsequent model's normals.
        _current?.EndRelight();
    }

    private static void AfterUse(ShaderProgramBase __instance)
    {
        var owner = _current;
        if (owner == null) return;

        if (__instance.PassName != "deferredlighting")
        {
            return;
        }

        var locations = owner.GetLocations(__instance.ProgramId);
        var payload = owner._prepared;
        if (locations.Payload >= 0)
            GL.Uniform1(locations.Payload, payload ? 1 : 0);
        if (locations.PlaneReady >= 0) GL.Uniform1(locations.PlaneReady, owner._prepared && owner._anchorReady ? 1 : 0);
        if (!owner._prepared)
        {
            return;
        }

        if (GL.GetInteger(GetPName.DrawFramebufferBinding) != owner._primaryFbo)
        {
            return;
        }

        // Sheyder resets its four draw buffers immediately before Use; append
        // our diffuse-only output here, after that native routing operation.
        if (!owner._relightTexturesBound)
        {
            owner.SaveTextures(owner._relightTexture, owner._relightSampler);
            owner._relightTexturesBound = true;
            try
            {
                owner.Attach(owner._primaryFbo, owner._irradiance, owner._relightSaved);
                owner._relightActive = true;
                owner.ClearAuxiliary(false);
                owner._irradianceAvailable = true;
            }
            catch (Exception ex)
            {
                // Terrain already wrote the private illumination transport.
                // Keep relight running with that matching payload; only shared
                // decal sampling is disabled if another owner changed this MRT.
                owner._irradianceAvailable = false;
                if (!owner._auxiliaryWarning)
                {
                    owner._auxiliaryWarning = true;
                    owner._api.Logger.Warning("[DRTAgX] Shared decal irradiance inactive: " + ex.Message);
                }
            }
        }

        if (locations.PlaneReady >= 0) GL.Uniform1(locations.PlaneReady, owner._anchorReady ? 1 : 0);
        if (locations.Plane >= 0) GL.Uniform1(locations.Plane, 11);
        owner.BindTexture(11, owner._plane);
        if (locations.Bounds >= 0) GL.Uniform1(locations.Bounds, 13);
        owner.BindTexture(13, owner._bounds);
    }

    private void SetProducerPayload(bool ready)
    {
        SetPayload(_api.Render.GetEngineShader(EnumShaderProgram.Chunkopaque), ready);
        SetPayload(_api.Render.GetEngineShader(EnumShaderProgram.Chunktopsoil), ready);
    }

    private void SetPayload(IShaderProgram? shader, bool ready)
    {
        if (shader is not { Disposed: false, LoadError: false, ProgramId: > 0 }) return;
        var locations = GetLocations(shader.ProgramId);
        if (locations.Payload >= 0) GL.ProgramUniform1(shader.ProgramId, locations.Payload, ready ? 1 : 0);
        if (locations.Geometry >= 0) GL.ProgramUniform1(shader.ProgramId, locations.Geometry, 0);
        if (locations.Replay >= 0) GL.ProgramUniform1(shader.ProgramId, locations.Replay, 0);
    }

    internal void BeginReplay(int program)
    {
        if (!_prepared || !_irradianceAvailable) return;
        SaveTextures(_replayTexture, _replaySampler);
        _replayActive = true;
        var locations = GetLocations(program);
        if (locations.Plane >= 0) GL.Uniform1(locations.Plane, 11);
        if (locations.Irradiance >= 0) GL.Uniform1(locations.Irradiance, 12);
        if (locations.Replay >= 0) GL.Uniform1(locations.Replay, 1);
        BindTexture(11, _plane);
        BindTexture(12, _irradiance);
    }

    internal void EndReplay(int program)
    {
        if (!_replayActive) return;
        var locations = GetLocations(program);
        if (locations.Replay >= 0) GL.ProgramUniform1(program, locations.Replay, 0);
        RestoreTextures(_replayTexture, _replaySampler);
        _replayActive = false;
    }

    private void BindTexture(int unit, int texture)
    {
        int active = GL.GetInteger(GetPName.ActiveTexture);
        GL.ActiveTexture(TextureUnit.Texture0 + unit);
        GL.BindTexture(TextureTarget.Texture2D, texture);
        GL.BindSampler(unit, 0);
        GL.ActiveTexture((TextureUnit)active);
    }

    private void SaveTextures(int[] textures, int[] samplers)
    {
        int active = GL.GetInteger(GetPName.ActiveTexture);
        for (int i = 0; i < 3; ++i)
        {
            GL.ActiveTexture(TextureUnit.Texture0 + 11 + i);
            textures[i] = GL.GetInteger(GetPName.TextureBinding2D);
            samplers[i] = GL.GetInteger(GetPName.SamplerBinding);
        }

        GL.ActiveTexture((TextureUnit)active);
    }

    private static void RestoreTextures(int[] textures, int[] samplers)
    {
        int active = GL.GetInteger(GetPName.ActiveTexture);
        for (int i = 0; i < 3; ++i)
        {
            GL.ActiveTexture(TextureUnit.Texture0 + 11 + i);
            GL.BindTexture(TextureTarget.Texture2D, textures[i]);
            GL.BindSampler(11 + i, samplers[i]);
        }

        GL.ActiveTexture((TextureUnit)active);
    }

    private void RestoreRelightTextures() => RestoreTextures(_relightTexture, _relightSampler);
}