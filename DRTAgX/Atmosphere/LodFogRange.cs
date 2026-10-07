using System;
using System.Linq.Expressions;
using Vintagestory.API.Client;

namespace DRTAgX;

/// <summary>Optional providers' live ranges, with startup-only reflection and no mod dependency.</summary>
internal sealed class LodFogRange
{
    private readonly ICoreClientAPI _api;
    private readonly Func<float>? _chunkLod, _farseer, _distantVistas;

    internal LodFogRange(ICoreClientAPI api)
    {
        _api = api;
        // These contracts are verified against the installed ChunkLOD metadata
        // and released/public Farseer source. Read the current config each frame,
        // rather than retaining a config instance that a provider could replace.
        _chunkLod = Resolve("ChunkLod.ChunkLodModSystem", "MaxViewDistance");
        _farseer = Resolve("Farseer.FarseerModSystem", "FarViewDistance");
        try
        {
            object? system = api.ModLoader.GetModSystem("DistantVistas.DistantVistasModSystem");
            if (system != null) _distantVistas = CreateDistantGetter(system);
        }
        catch (Exception ex)
        {
            api.Logger.Warning("[DRT AgX] Distant Vistas range unavailable: " + ex.Message);
        }
    }

    internal float Endpoint
    {
        get
        {
            if (!LodFogAssetPatch.Installed && !DistantVistasAssetPatch.Installed) return 0f;
            int cap = _farseer != null ? _api.World.Config.GetInt("maxFarViewDistance", 0) : 0;
            float legacy = LodFogAssetPatch.Installed ? SelectEndpoint(_chunkLod?.Invoke() ?? 0f, _farseer?.Invoke() ?? 0f, cap) : 0f;
            float distant = DistantVistasAssetPatch.Installed ? _distantVistas?.Invoke() ?? 0f : 0f;
            return Math.Max(legacy, float.IsFinite(distant) ? Math.Max(0f, distant) : 0f);
        }
    }

    private Func<float>? Resolve(string name, string distanceMember)
    {
        try
        {
            object? system = _api.ModLoader.GetModSystem(name);
            return system == null ? null : CreateGetter(system, distanceMember);
        }
        catch (Exception ex)
        {
            // An unknown provider contract leaves ordinary native-range fog intact.
            _api.Logger.Warning($"[DRT AgX] LOD fog range unavailable for {name}: {ex.Message}");
            return null;
        }
    }

    internal static Func<float> CreateGetter(object system, string distanceMember)
    {
        var client = Expression.PropertyOrField(Expression.Constant(system), "Client");
        var config = Expression.PropertyOrField(client, "Config");
        var enabled = Expression.PropertyOrField(config, "Enabled");
        Expression configured = Expression.PropertyOrField(config, distanceMember);
        // ChunkLOD selects terrain to MaxViewDistance (plus chunk padding).
        // Its legacy farViewDistance uniform is half this range and controls
        // its old early color fade; the shared fog must use the full coverage.
        var distance = Expression.Convert(configured, typeof(float));
        var available = Expression.AndAlso(Expression.NotEqual(client, Expression.Constant(null, client.Type)),
            Expression.AndAlso(Expression.NotEqual(config, Expression.Constant(null, config.Type)), enabled));
        return Expression.Lambda<Func<float>>(Expression.Condition(available, distance, Expression.Constant(0f))).Compile();
    }

    internal static Func<float> CreateDistantGetter(object system)
    {
        // Installed 1.1.3: an idle/deferred provider has no renderer. Its live
        // EffectiveFarDistance already accounts for captured coverage and the cap.
        var renderer = Expression.PropertyOrField(Expression.Constant(system), "renderer");
        var distance = Expression.Convert(Expression.PropertyOrField(renderer, "EffectiveFarDistance"), typeof(float));
        return Expression.Lambda<Func<float>>(Expression.Condition(
            Expression.NotEqual(renderer, Expression.Constant(null, renderer.Type)), distance, Expression.Constant(0f))).Compile();
    }

    internal static float SelectEndpoint(float chunkLod, float farseer, int farseerServerCap)
    {
        if (!float.IsFinite(chunkLod)) chunkLod = 0f;
        if (!float.IsFinite(farseer)) farseer = 0f;
        if (farseerServerCap > 0) farseer = Math.Min(farseer, farseerServerCap);
        return Math.Max(0f, Math.Max(chunkLod, farseer));
    }
}
