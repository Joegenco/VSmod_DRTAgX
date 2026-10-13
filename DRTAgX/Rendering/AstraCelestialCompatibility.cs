using System;
using HarmonyLib;
using Vintagestory.API.Client;

// Domain folders share the root namespace; Harmony requires the __state name.
// ReSharper disable CheckNamespace, InconsistentNaming
namespace DRTAgX;

/// <summary>Keep AstraTerra/AstraExtera celestial draws out of surface and native-sun lighting.</summary>
internal sealed class AstraCelestialCompatibility : IDisposable
{
    private const string PatchId = "drtagx.celestial.astra";
    private readonly Harmony? _harmony;

    internal AstraCelestialCompatibility(ICoreClientAPI api)
    {
        // AstraExtera supplies catalogs to AstraTerra; it does not own another
        // renderer. There is no reflection or draw hook without that provider.
        if (!api.ModLoader.IsModEnabled("astraterra")) return;
        try
        {
            // Verified against AstraTerra v0.15.0's published source. Its star
            // pass runs INSIDE a native sun/moon postfix, while these two body
            // renderers run separately. Patch the three draw boundaries only.
            var stars = AccessTools.Method("AstraTerra.Client.Rendering.SkyStarSunMoonRenderer:RenderSky");
            var moon = AccessTools.Method("AstraTerra.Client.Rendering.MoonDiscRenderer:Draw", Type.EmptyTypes);
            var bodies = AccessTools.Method("AstraTerra.Client.Rendering.NearBodyRenderer:Draw", Type.EmptyTypes);
            if (stars is not { IsStatic: true } || stars.ReturnType != typeof(void) ||
                moon is not { IsStatic: false } || moon.ReturnType != typeof(bool) ||
                bodies is not { IsStatic: false } || bodies.ReturnType != typeof(void))
                throw new MissingMethodException("AstraTerra celestial draw boundaries");

            _harmony = new Harmony(PatchId);
            foreach (var draw in new[] { stars, moon, bodies })
                _harmony.Patch(draw,
                    prefix: new HarmonyMethod(typeof(AstraCelestialCompatibility), nameof(BeforeDraw)),
                    finalizer: new HarmonyMethod(typeof(AstraCelestialCompatibility), nameof(AfterDraw)));
            api.Logger.Notification("[DRT AgX] AstraTerra/AstraExtera celestial HDR composition installed.");
        }
        catch (Exception ex)
        {
            _harmony?.UnpatchAll(PatchId);
            api.Logger.Warning("[DRT AgX] Astra celestial compatibility unavailable: " + ex.Message);
        }
    }

    private static void BeforeDraw(out CelestialSunBindings.DrawScope __state) =>
        __state = CelestialSunBindings.BeginDraw(2);

    private static void AfterDraw(CelestialSunBindings.DrawScope __state) =>
        CelestialSunBindings.AfterDraw(__state);

    public void Dispose() => _harmony?.UnpatchAll(PatchId);
}