using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Reflection.Emit;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace DRTAgX;

/// <summary>Resolve only captured Sheyder contracts; no reflection in frame callbacks.</summary>
internal sealed class AtmosphereSheyderBridge : IDisposable
{
    private static AtmosphereSheyderBridge? _instance;
    private readonly Harmony _harmony = new("drtagx.atmosphere.volumetric");
    private Func<float>? _strength;
    // Prefer the original Sheyder shafts. The atmosphere volume is only a
    // compatibility fallback when that captured entry point cannot be resolved.
    internal bool LegacyAvailable { get; private set; }
    internal bool ReplacementReady { get; set; }
    internal float Strength => Math.Clamp(_strength?.Invoke() ?? 0f, 0f, 1f);

    internal AtmosphereSheyderBridge(ICoreClientAPI api)
    {
        _instance = this;
        // Captured Sheyder ComputeOcclusion combines cloud/rain occlusion with
        // flatFogDensity*40. Shared transport owns that weather attenuation;
        // replace only that verified getter and retain cloud/rain traversal.
        var occlusion = AccessTools.TypeByName("SheyderMod.Features.Shared.SunOcclusion");
        var compute = occlusion == null ? null : AccessTools.Method(occlusion, "ComputeOcclusion");
        if (compute != null) _harmony.Patch(compute,
            transpiler: new HarmonyMethod(typeof(AtmosphereSheyderBridge), nameof(SeparateWeather)));
        try
        {
            var system = api.ModLoader.GetModSystem("SheyderMod.SheyderModSystem");
            var renderers = system == null ? null : AccessTools.Field(system.GetType(), "_renderers")?.GetValue(system) as IEnumerable;
            if (renderers == null) return;
            foreach (object renderer in renderers)
            {
                if (renderer.GetType().FullName != "SheyderMod.Features.VolumetricFog.VolumetricFogRenderer") continue;
                var getter = AccessTools.Field(renderer.GetType(), "_getConfig")?.GetValue(renderer) as Delegate;
                if (getter == null) break;
                var config = Expression.Invoke(Expression.Constant(getter, getter.GetType()));
                var fog = Expression.Property(config, "VolumetricFog");
                var enabled = Expression.Property(fog, "Enabled");
                var intensity = Expression.Convert(Expression.Property(fog, "Intensity"), typeof(float));
                _strength = Expression.Lambda<Func<float>>(Expression.Condition(enabled,
                    Expression.Divide(intensity, Expression.Constant(0.1f)), Expression.Constant(0f))).Compile();
                var late = AccessTools.Method(renderer.GetType(), "RenderScatterAndBindFinal", Type.EmptyTypes);
                if (late != null)
                {
                    _harmony.Patch(late,
                        prefix: new HarmonyMethod(typeof(AtmosphereSheyderBridge), nameof(AllowLegacyScatter)));
                    LegacyAvailable = true;
                }
                break;
            }
        }
        catch (Exception ex)
        {
            api.Logger.Warning("[DRT AgX] Atmospheric shafts retain legacy fallback: " + ex.Message);
        }
    }

    private static bool AllowLegacyScatter() => _instance?.LegacyAvailable == true || _instance?.ReplacementReady != true;

    private static IEnumerable<CodeInstruction> SeparateWeather(IEnumerable<CodeInstruction> instructions)
    {
        var native = AccessTools.PropertyGetter(typeof(IAmbientManager), nameof(IAmbientManager.BlendedFlatFogDensity));
        var owned = AccessTools.Method(typeof(AtmosphereSheyderBridge), nameof(LegacyFlatDensity));
        foreach (var instruction in instructions)
        {
            if (instruction.Calls(native)) { instruction.opcode = OpCodes.Call; instruction.operand = owned; }
            yield return instruction;
        }
    }

    private static float LegacyFlatDensity(IAmbientManager ambient) => _instance == null ? ambient.BlendedFlatFogDensity : 0f;

    public void Dispose()
    {
        ReplacementReady = false;
        _harmony.UnpatchAll(_harmony.Id);
        if (_instance == this) _instance = null;
    }
}
