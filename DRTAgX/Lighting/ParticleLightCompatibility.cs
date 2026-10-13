using System;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.MathTools;
using Vintagestory.Client.NoObf;
using Vintagestory.Common;

namespace DRTAgX;

/// <summary>Give native world particles the same baked light lookup as Sheyder's model receivers.</summary>
internal sealed class ParticleLightCompatibility : IDisposable
{
    private const string PatchId = "drtagx.particle.baked-light";
    private readonly Harmony _harmony = new(PatchId);
    private static Action<WorldMap, int, int, int, Vec4f>? _applyLight;
    private static Func<bool>? _blockEnabled, _sunEnabled;
    [ThreadStatic] private static Vec4f? _sample;

    internal ParticleLightCompatibility(ICoreClientAPI api)
    {
        object? system = api.ModLoader.GetModSystem("SheyderMod.SheyderModSystem");
        if (system == null) return;
        try
        {
            var assembly = system.GetType().Assembly;
            var patch = assembly.GetType("SheyderMod.Features.BlockLight.Patches.RenderLightPatch", true)!;
            var gates = assembly.GetType("SheyderMod.Features.BlockLight.BlockLightGates", true)!;
            // Reuse the dependency's exact current-map, availability, dimension
            // and missing-volume rules. No duplicate bake, ray cast or GPU pass.
            _applyLight = AccessTools.DeclaredMethod(patch, "Postfix")
                .CreateDelegate<Action<WorldMap, int, int, int, Vec4f>>();
            _blockEnabled = AccessTools.PropertyGetter(gates, "Visibility").CreateDelegate<Func<bool>>();
            _sunEnabled = AccessTools.PropertyGetter(gates, "DirectionalSun").CreateDelegate<Func<bool>>();
            _harmony.Patch(AccessTools.DeclaredMethod(typeof(ParticleGeneric), "TickNow"),
                postfix: new HarmonyMethod(typeof(ParticleLightCompatibility), nameof(AfterParticleTick)));
            api.Logger.Notification("[DRT AgX] Native particle lighting follows Sheyder's baked block/sun light.");
        }
        catch (Exception e)
        {
            Dispose();
            api.Logger.Warning("[DRT AgX] Particle baked-light compatibility unavailable: " + e.Message);
        }
    }

    private static void AfterParticleTick(ParticleGeneric __instance, ICoreClientAPI __2)
    {
        var apply = _applyLight;
        if (apply == null || (_blockEnabled?.Invoke() != true && _sunEnabled?.Invoke() != true) ||
            __2.World is not ClientMain client || __instance.LightEmission != 0) return;
        // TickNow is shared by native cube, quad, rain and splash instances on
        // main/particle worker threads. Particle2D and global light APIs stay native.
        // A provider's explicit LightEmission is an authored RGBA override, not
        // ambient light: retain it along with all independent vertex glow flags.
        int packed = __instance.lightrgbs;
        var light = _sample ??= new Vec4f();
        light.R = (packed & 255) / 255f;
        light.G = ((packed >> 8) & 255) / 255f;
        light.B = ((packed >> 16) & 255) / 255f;
        light.A = ((uint)packed >> 24) / 255f;
        var position = __instance.Position;
        apply(client.WorldMap, (int)Math.Floor(position.X), (int)Math.Floor(position.Y),
            (int)Math.Floor(position.Z), light);
        // Packed particle light has RGB in bits 0..23 and sunlight in 24..31;
        // alpha here is lighting metadata, not particle opacity. 255.999 matches
        // the native table-to-byte convention and preserves unchanged bytes.
        __instance.lightrgbs = Pack(light.R) | Pack(light.G) << 8 | Pack(light.B) << 16 | Pack(light.A) << 24;
    }

    private static int Pack(float value) => Math.Clamp((int)(value * 255.999f), 0, 255);

    public void Dispose()
    {
        _harmony.UnpatchAll(PatchId);
        _applyLight = null;
        _blockEnabled = _sunEnabled = null;
        _sample = null;
    }
}
