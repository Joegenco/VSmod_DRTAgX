using System;
using System.Linq.Expressions;
using Vintagestory.API.Client;

namespace DRTAgX;

/// <summary>Optional providers' live ranges, with startup-only reflection and no mod dependency.</summary>
internal sealed class LodFogRange
{
    private readonly ICoreClientAPI _api;
    private readonly Func<float>? _chunkLod, _farseer;

    internal LodFogRange(ICoreClientAPI api)
    {
        _api = api;
        // These contracts are verified against the installed ChunkLOD metadata
        // and released/public Farseer source. Read the current config each frame,
        // rather than retaining a config instance that a provider could replace.
        _chunkLod = Resolve("ChunkLod.ChunkLodModSystem", "MaxViewDistance");
        _farseer = Resolve("Farseer.FarseerModSystem", "FarViewDistance");
    }

    internal float Endpoint
    {
        get
        {
            if (!LodFogAssetPatch.Installed) return 0f;
            int cap = _farseer != null ? _api.World.Config.GetInt("maxFarViewDistance", 0) : 0;
            return SelectEndpoint(_chunkLod?.Invoke() ?? 0f, _farseer?.Invoke() ?? 0f, cap);
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

    internal static float SelectEndpoint(float chunkLod, float farseer, int farseerServerCap)
    {
        if (!float.IsFinite(chunkLod)) chunkLod = 0f;
        if (!float.IsFinite(farseer)) farseer = 0f;
        if (farseerServerCap > 0) farseer = Math.Min(farseer, farseerServerCap);
        return Math.Max(0f, Math.Max(chunkLod, farseer));
    }
}
