using System;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
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
    private readonly ConditionalWeakTable<IWorldChunk, ChunkState> _chunks = new();

    private sealed class ChunkState
    {
        internal PlacedLightCasterFingerprint.Signature Center, Edge;
    }

    private readonly record struct UploadState(
        PlacedLightGeometryBridge? Owner,
        IWorldChunk? Chunk,
        bool CenterReplaced,
        PlacedLightCasterFingerprint.Signature Center,
        bool EdgeReplaced,
        PlacedLightCasterFingerprint.Signature Edge,
        long StartedAt);

    // Explicit profile-end read only; no diagnostic object is allocated on uploads.

    internal PlacedLightGeometryBridge(ICoreClientAPI api, StaticLightSources sources)
    {
        _sources = sources;
        try
        {
            Type tessellated = AccessTools.TypeByName("Vintagestory.Client.NoObf.TesselatedChunk")
                               ?? throw new TypeLoadException("TesselatedChunk");
            Type clientChunk = AccessTools.TypeByName("Vintagestory.Client.NoObf.ClientChunk")
                               ?? throw new TypeLoadException("ClientChunk");
            _upload = AccessTools.Method(typeof(ChunkRenderer), "AddTesselatedChunk",
                          new[] { tessellated, clientChunk })
                      ?? throw new MissingMethodException("ChunkRenderer.AddTesselatedChunk");
            PlacedLightCasterFingerprint.VerifyContract();
            _current = this;
            _harmony.Patch(_upload,
                prefix: new HarmonyMethod(typeof(PlacedLightGeometryBridge), nameof(Uploading)),
                finalizer: new HarmonyMethod(typeof(PlacedLightGeometryBridge), nameof(Uploaded)));
            sources.NativeGeometryNotifications = true;
            api.Logger.Notification("[DRT AgX] Placed-shadow invalidation follows native terrain uploads.");
        }
        catch (Exception ex)
        {
            Dispose();
            api.Logger.Warning("[DRT AgX] Placed-shadow upload hook unavailable; using coalesced edit fallback: " +
                               ex.Message);
        }
    }

    private static void Uploading(TesselatedChunk __0, object __1, out UploadState __state)
    {
        var owner = _current;
        __state = default;
        if (owner == null) return;
        // Render-thread boundary: native recycles these arrays during upload.
        // State contains only hashes and the loaded chunk identity. Null center
        // or edge means no replacement, whereas an empty array clears that part.
        long start = Stopwatch.GetTimestamp();
        var chunk = __1 as IWorldChunk;
        bool center = true, edge = true;
        PlacedLightCasterFingerprint.Signature a = default, b = default;
        try
        {
            chunk ??= PlacedLightCasterFingerprint.Chunk(__0);
            var centerParts = PlacedLightCasterFingerprint.Center(__0);
            var edgeParts = PlacedLightCasterFingerprint.Edge(__0);
            center = centerParts != null;
            edge = edgeParts != null;
            if (center)
            {
                a = PlacedLightCasterFingerprint.Capture(__0, centerParts!, out _);
            }

            if (edge)
            {
                b = PlacedLightCasterFingerprint.Capture(__0, edgeParts!, out _);
            }
        }
        catch
        {
            // Unknown layouts/invalid payloads must not block native upload or
            // make a later upload look clean against an obsolete signature.
            a = b = new(0, 0, false, CasterSignatureReason.CaptureException);
        }

        __state = new(owner, chunk, center, a, edge, b, start);
    }

    private static Exception? Uploaded(Exception? __exception, UploadState __state)
    {
        var owner = __state.Owner;
        if (owner == null || !ReferenceEquals(owner, _current)) return __exception;
        // Harmony's finalizer observes success after native/postfix execution.
        // Failed uploads never commit candidate hashes or hide a genuine retry.
        if (__exception != null)
        {
            return __exception;
        }

        if (__state.Chunk == null)
        {
            return null;
        }

        bool replaced = __state.CenterReplaced || __state.EdgeReplaced;
        if (!replaced)
        {
            return null;
        }

        ChunkState previous = owner._chunks.GetOrCreateValue(__state.Chunk);
        bool unknown = (__state.CenterReplaced && !__state.Center.Known) ||
                       (__state.EdgeReplaced && !__state.Edge.Known);
        bool changed = unknown || (__state.CenterReplaced && previous.Center != __state.Center) ||
                       (__state.EdgeReplaced && previous.Edge != __state.Edge);
        // Committing an unknown signature also clears an old known baseline.
        if (__state.CenterReplaced) previous.Center = __state.Center;
        if (__state.EdgeReplaced) previous.Edge = __state.Edge;
        // Successful equal uploads are useful evidence too. Acknowledgement
        // never suppresses the changed-content notification below, and unknown
        // or failed parts cannot certify a pending exact edit as processed.
        owner._sources.OnGeometryUploadProcessed(__state.Chunk, __state.StartedAt,
            __state.CenterReplaced, __state.Center.Known, __state.EdgeReplaced, __state.Edge.Known);

        if (!changed)
        {
            return null;
        }

        owner._sources.OnGeometryUploaded(__state.Chunk);
        return null;
    }

    public void Dispose()
    {
        if (ReferenceEquals(_current, this)) _current = null;
        _sources.NativeGeometryNotifications = false;
        _chunks.Clear();
        if (_upload != null) _harmony.Unpatch(_upload, HarmonyPatchType.All, PatchId);
    }
}