using System;
using System.Reflection;
using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace DRTAgX;

/// <summary>Let native rain splashes receive scene lighting without their legacy visibility glow.</summary>
internal sealed class RainSplashLightingCompatibility : IDisposable
{
    private const int GlowMask = 0xff; // Native vertex flags: glow occupies bits 0..7.
    private const int NativeSplashGlow = 32;
    private readonly ICoreClientAPI _api;
    private FieldInfo? _field;
    private SimpleParticleProperties? _provider;
    private int _originalFlags, _installedFlags;

    internal RainSplashLightingCompatibility(ICoreClientAPI api)
    {
        _api = api;
        try
        {
            // Startup only: identify the native splash provider, never arbitrary
            // mod particles with the same glow byte. No tick/render hook is needed.
            object? weather = api.ModLoader.GetModSystem("Vintagestory.GameContent.WeatherSystemClient");
            if (weather == null) return;
            var type = weather.GetType().Assembly.GetType("Vintagestory.GameContent.WeatherSimulationParticles");
            var field = type?.GetField("splashParticles", BindingFlags.Public | BindingFlags.Static);
            if (field?.GetValue(null) is not SimpleParticleProperties provider)
                throw new MissingFieldException("Native weather splashParticles provider");
            var flags = provider.VertexFlags;
            if ((flags & GlowMask) != NativeSplashGlow) return;

            // Vanilla splash glow 32 becomes 4*(32/256)=0.5 constant HDR light
            // and a bloom mask. Clear only this known visibility floor; preserve
            // shape/wind flags, material color, alpha and native light metadata.
            _field = field;
            _provider = provider;
            _originalFlags = flags;
            _installedFlags = flags & ~GlowMask;
            provider.VertexFlags = _installedFlags;
        }
        catch (Exception e)
        {
            Dispose();
            api.Logger.Warning("[DRT AgX] Rain splash lighting compatibility unavailable: " + e.Message);
        }
    }

    public void Dispose()
    {
        try
        {
            // Release only the exact state we own. A replacement provider or
            // any later flag edit belongs to the other mod and must survive.
            if (_provider != null && ReferenceEquals(_field?.GetValue(null), _provider) &&
                _provider.VertexFlags == _installedFlags)
                _provider.VertexFlags = _originalFlags;
        }
        catch (Exception e)
        {
            _api.Logger.Warning("[DRT AgX] Rain splash lighting restoration unavailable: " + e.Message);
        }
        finally
        {
            _provider = null;
            _field = null;
        }
    }
}
