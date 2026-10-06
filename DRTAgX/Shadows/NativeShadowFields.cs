using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.Client.NoObf;

namespace DRTAgX;

// Validated once, then direct field reads. Reflection is a compatibility fallback,
// never a search inside a face/pool loop.
internal static class NativeShadowFields
{
    internal static Func<T, F?> Reader<T, F>(string name) where T : class
    {
        FieldInfo? field = AccessTools.Field(typeof(T), name);
        if (field == null || !typeof(F).IsAssignableFrom(field.FieldType)) return _ => default;
        try
        {
            var access = AccessTools.FieldRefAccess<T, F>(field);
            return instance => access(instance);
        }
        catch { return instance => field.GetValue(instance) is F value ? value : default; }
    }

    internal static readonly Func<ClientMain, ChunkRenderer?> Renderer = Reader<ClientMain, ChunkRenderer>("chunkRenderer");
    internal static readonly Func<ChunkRenderer, MeshDataPoolManager[][]?> Passes = Reader<ChunkRenderer, MeshDataPoolManager[][]>("poolsByRenderPass");
    internal static readonly Func<ChunkRenderer, int[]?> Atlases = Reader<ChunkRenderer, int[]>("textureIds");
    internal static readonly Func<MeshDataPoolManager, List<MeshDataPool>?> Pools = Reader<MeshDataPoolManager, List<MeshDataPool>>("pools");
    internal static readonly Func<MeshDataPool, List<ModelDataPoolLocation>?> Locations = Reader<MeshDataPool, List<ModelDataPoolLocation>>("poolLocations");
    internal static readonly Func<MeshDataPool, int> Dimension = Reader<MeshDataPool, int>("dimensionId");
}
