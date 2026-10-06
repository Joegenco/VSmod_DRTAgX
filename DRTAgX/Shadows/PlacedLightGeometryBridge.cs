using System;
using System.Reflection;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.Client.NoObf;

namespace DRTAgX;

// Frozen 1.22.7 upload contract: AddTesselatedChunk(TesselatedChunk, ClientChunk).
// Capture identity before native upload consumes the tessellated input, then
// notify after success. Never replace drawing or retain MeshData.
internal sealed class PlacedLightGeometryBridge : IDisposable
{
    private const string PatchId = "drtagx.placedlight.geometry";
    private static PlacedLightGeometryBridge? _current;
    private readonly StaticLightSources _sources;
    private readonly Harmony _harmony = new(PatchId);
    private MethodInfo? _upload;
    private FieldInfo? _chunk;

    internal PlacedLightGeometryBridge(ICoreClientAPI api, StaticLightSources sources)
    {
        _sources = sources;
        try
        {
            Type tessellated = AccessTools.TypeByName("Vintagestory.Client.NoObf.TesselatedChunk")
                ?? throw new TypeLoadException("TesselatedChunk");
            Type clientChunk = AccessTools.TypeByName("Vintagestory.Client.NoObf.ClientChunk")
                ?? throw new TypeLoadException("ClientChunk");
            _upload = AccessTools.Method(typeof(ChunkRenderer), "AddTesselatedChunk", new[] { tessellated, clientChunk })
                ?? throw new MissingMethodException("ChunkRenderer.AddTesselatedChunk");
            _chunk = AccessTools.Field(tessellated, "chunk");
            if (_chunk == null || !typeof(IWorldChunk).IsAssignableFrom(_chunk.FieldType))
                throw new MissingFieldException("TesselatedChunk.chunk");
            _current = this;
            _harmony.Patch(_upload,
                prefix: new HarmonyMethod(typeof(PlacedLightGeometryBridge), nameof(Uploading)),
                postfix: new HarmonyMethod(typeof(PlacedLightGeometryBridge), nameof(Uploaded)));
            sources.NativeGeometryNotifications = true;
            api.Logger.Notification("[DRT AgX] Placed-shadow invalidation follows native terrain uploads.");
        }
        catch (Exception ex)
        {
            Dispose();
            api.Logger.Warning("[DRT AgX] Placed-shadow upload hook unavailable; using coalesced edit fallback: " + ex.Message);
        }
    }

    private static void Uploading(object __0, object __1, out IWorldChunk? __state)
    {
        // Rider confirms that __0.chunk is null after the native method returns.
        // Harmony state copies only the loaded identity; the second native
        // argument is authoritative, with the pre-upload field as a fallback.
        __state = __1 as IWorldChunk ?? _current?._chunk?.GetValue(__0) as IWorldChunk;
    }

    private static void Uploaded(IWorldChunk? __state)
    {
        if (__state != null) _current?._sources.OnGeometryUploaded(__state);
    }

    public void Dispose()
    {
        if (ReferenceEquals(_current, this)) _current = null;
        _sources.NativeGeometryNotifications = false;
        if (_upload != null) _harmony.Unpatch(_upload, HarmonyPatchType.All, PatchId);
    }
}
