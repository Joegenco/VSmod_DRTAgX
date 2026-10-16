using System;
using HarmonyLib;
using Vintagestory.API.Client;

namespace DRTAgX;

/// <summary>Include animated world surfaces in Sheyder's existing overexposure publication.</summary>
internal sealed class SheyderAnimatedOverexposure : IDisposable
{
    private static SheyderAnimatedOverexposure? _instance;
    private readonly Harmony _harmony = new("drtagx.sheyder.animated-overexposure");
    private EnumShaderProgram[]? _source, _extended;

    internal SheyderAnimatedOverexposure(ICoreClientAPI api)
    {
        object? system = api.ModLoader.GetModSystem("SheyderMod.SheyderModSystem");
        if (system == null) return;
        try
        {
            var type = system.GetType().Assembly.GetType("SheyderMod.Features.Overexposure.OverexposureRenderer", true)!;
            var getter = AccessTools.DeclaredProperty(type, "TargetShaders")?.GetGetMethod(true);
            if (getter == null || getter.IsStatic || getter.ReturnType != typeof(EnumShaderProgram[]))
                throw new MissingMethodException(type.FullName, "TargetShaders");
            _instance = this;
            // The native EffectRenderer reads this getter while publishing its
            // cached uniforms. Retain its fade, settings invalidation, shader
            // reload handling and change-only writes instead of a second binder.
            _harmony.Patch(getter,
                postfix: new HarmonyMethod(typeof(SheyderAnimatedOverexposure), nameof(IncludeAnimated)));
        }
        catch (Exception e)
        {
            Dispose();
            api.Logger.Warning("[DRT AgX] Animated overexposure compatibility unavailable: " + e.Message);
        }
    }

    private static void IncludeAnimated(ref EnumShaderProgram[] __result)
    {
        var owner = _instance;
        if (owner == null || __result == null) return;
        if (!ReferenceEquals(owner._source, __result))
        {
            // Preserve the provider's array and every existing target, including
            // additions from other patches. Allocate only when that array changes.
            owner._source = __result;
            bool opaque = Array.IndexOf(__result, EnumShaderProgram.Entityanimated) >= 0;
            bool transparent = Array.IndexOf(__result, EnumShaderProgram.Entityanimated_Oit) >= 0;
            var count = __result.Length + (opaque ? 0 : 1) + (transparent ? 0 : 1);
            if (count == __result.Length) owner._extended = __result;
            else
            {
                var extended = new EnumShaderProgram[count];
                Array.Copy(__result, extended, __result.Length);
                var index = __result.Length;
                if (!opaque) extended[index++] = EnumShaderProgram.Entityanimated;
                if (!transparent) extended[index] = EnumShaderProgram.Entityanimated_Oit;
                owner._extended = extended;
            }
        }

        __result = owner._extended!;
    }

    public void Dispose()
    {
        _harmony.UnpatchAll(_harmony.Id);
        _source = _extended = null;
        if (_instance == this) _instance = null;
    }
}
