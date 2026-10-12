using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.MathTools;
using Vintagestory.Client.NoObf;

namespace DRTAgX;

// Upload-time fingerprints of the frozen native payload, never source-array
// ownership. The point depth shaders read geometry/UV/flags and texture alpha,
// not MeshData.Rgba (native block/sun lighting at rgbaLightIn).
internal static class PlacedLightCasterFingerprint
{
    internal readonly record struct Signature(
        ulong A,
        ulong B,
        bool Known,
        CasterSignatureReason Reasons = CasterSignatureReason.None);

    // Native part fields are internal. Resolve once against their verified
    // declarations; typed ref accessors do not reflect or box during scanning.
    internal static readonly AccessTools.FieldRef<TesselatedChunk, TesselatedChunkPart[]?> Center =
        AccessTools.FieldRefAccess<TesselatedChunk, TesselatedChunkPart[]?>("centerParts");

    internal static readonly AccessTools.FieldRef<TesselatedChunk, TesselatedChunkPart[]?> Edge =
        AccessTools.FieldRefAccess<TesselatedChunk, TesselatedChunkPart[]?>("edgeParts");

    internal static readonly AccessTools.FieldRef<TesselatedChunk, ClientChunk?> Chunk =
        AccessTools.FieldRefAccess<TesselatedChunk, ClientChunk?>("chunk");

    private static readonly AccessTools.FieldRef<TesselatedChunk, int> X =
            AccessTools.FieldRefAccess<TesselatedChunk, int>("positionX"),
        Y = AccessTools.FieldRefAccess<TesselatedChunk, int>("positionYAndDimension"),
        Z = AccessTools.FieldRefAccess<TesselatedChunk, int>("positionZ");

    private static readonly AccessTools.FieldRef<TesselatedChunk, Sphere> Bounds =
        AccessTools.FieldRefAccess<TesselatedChunk, Sphere>("boundingSphere");

    private static readonly AccessTools.FieldRef<TesselatedChunkPart, int> Atlas =
        AccessTools.FieldRefAccess<TesselatedChunkPart, int>("atlasNumber");

    private static readonly AccessTools.FieldRef<TesselatedChunkPart, EnumChunkRenderPass> Pass =
        AccessTools.FieldRefAccess<TesselatedChunkPart, EnumChunkRenderPass>("pass");

    private static readonly AccessTools.FieldRef<TesselatedChunkPart, MeshData?> Lod0 =
            AccessTools.FieldRefAccess<TesselatedChunkPart, MeshData?>("modelDataLod0"),
        Lod1 = AccessTools.FieldRefAccess<TesselatedChunkPart, MeshData?>("modelDataLod1"),
        NotFar = AccessTools.FieldRefAccess<TesselatedChunkPart, MeshData?>("modelDataNotLod2Far"),
        Far = AccessTools.FieldRefAccess<TesselatedChunkPart, MeshData?>("modelDataLod2Far");

    internal static void VerifyContract()
    {
        _ = Center;
        _ = Edge;
        _ = Chunk;
    }

    internal static Signature Capture(TesselatedChunk input, TesselatedChunkPart[] parts, out long bytes)
    {
        var hash = new Hash();
        hash.Word((uint)X(input));
        hash.Word((uint)Y(input));
        hash.Word((uint)Z(input));
        var sphere = Bounds(input);
        hash.Word(BitConverter.SingleToUInt32Bits(sphere.x));
        hash.Word(BitConverter.SingleToUInt32Bits(sphere.y));
        hash.Word(BitConverter.SingleToUInt32Bits(sphere.z));
        hash.Word(BitConverter.SingleToUInt32Bits(sphere.radius));
        hash.Word(BitConverter.SingleToUInt32Bits(sphere.radiusY));
        hash.Word(BitConverter.SingleToUInt32Bits(sphere.radiusZ));
        hash.Word((uint)parts.Length);
        bool known = true;
        foreach (TesselatedChunkPart? part in parts)
        {
            hash.Word(part == null ? 0u : 1u);
            if (part == null)
            {
                known = false;
                hash.Reasons |= CasterSignatureReason.NullPart;
                continue;
            }

            hash.Word((uint)Atlas(part));
            hash.Word((uint)Pass(part));
            // Each independent LOD participates: camera selection of a different
            // native pool must not silently retain depth for different geometry.
            known &= Mesh(Lod0(part), ref hash);
            known &= Mesh(Lod1(part), ref hash);
            known &= Mesh(NotFar(part), ref hash);
            known &= Mesh(Far(part), ref hash);
        }

        bytes = hash.Bytes;
        return new(hash.A, hash.B, known, hash.Reasons);
    }


    private static bool Mesh(MeshData? mesh, ref Hash hash)
    {
        hash.Word(mesh == null ? 0u : 1u);
        if (mesh == null) return true;
        int vertices = mesh.VerticesCount;
        if (vertices < 0 || vertices > int.MaxValue / 3)
        {
            hash.Reasons |= CasterSignatureReason.VertexCount;
            return false;
        }

        // Installed API XML: offsets target the destination VBO on updates;
        // they do not select a later source-array slice or change caster content.
        if (mesh.XyzInstanced || mesh.UvInstanced || mesh.FlagsInstanced || mesh.IndicesInstanced)
        {
            hash.Reasons |= CasterSignatureReason.InstancedGeometry;
            return false;
        }

        hash.Word((uint)mesh.mode);
        hash.Word((uint)mesh.VerticesPerFace);
        hash.Word((uint)mesh.IndicesPerFace);
        hash.Word(mesh.XyzInstanced ? 1u : 0u);
        hash.Word(mesh.UvInstanced ? 1u : 0u);
        hash.Word(mesh.FlagsInstanced ? 1u : 0u);
        hash.Word(mesh.IndicesInstanced ? 1u : 0u);
        // Capacity/tail bytes and recycling timestamps are not uploaded content.
        bool known = hash.Array(mesh.xyz, vertices * 3, required: vertices != 0,
            reason: CasterSignatureReason.Positions);
        known &= hash.Array(mesh.Uv, vertices * 2, required: vertices != 0, reason: CasterSignatureReason.Uvs);
        known &= hash.Array(mesh.Flags, vertices, required: vertices != 0, reason: CasterSignatureReason.Flags);
        known &= hash.Array(mesh.Indices, mesh.IndicesCount, required: mesh.IndicesCount != 0,
            reason: CasterSignatureReason.Indices);
        known &= hash.Array(mesh.Normals, mesh.NormalsCount);
        known &= hash.Array(mesh.XyzFaces, mesh.XyzFacesCount);
        known &= hash.Array(mesh.TextureIndices, mesh.TextureIndicesCount,
            reason: CasterSignatureReason.TextureRouting);
        known &= hash.Array(mesh.TextureIds, mesh.TextureIds?.Length ?? 0);
        known &= hash.Array(mesh.RenderPassesAndExtraBits, mesh.RenderPassCount);
        // Unknown custom semantics cannot safely be classified as lighting.
        // Hash their complete active streams/layouts conservatively, including
        // water flags, rather than ignoring a custom depth or alpha attribute.
        known &= Custom(mesh.CustomFloats, ref hash);
        known &= Custom(mesh.CustomInts, ref hash);
        hash.Word((uint)(mesh.CustomInts?.Conversion ?? 0));
        known &= Custom(mesh.CustomShorts, ref hash);
        hash.Word((uint)(mesh.CustomShorts?.Conversion ?? 0));
        known &= Custom(mesh.CustomBytes, ref hash);
        hash.Word((uint)(mesh.CustomBytes?.Conversion ?? 0));
        return known;
    }

    private static bool Custom<T>(CustomMeshDataPart<T>? part, ref Hash hash) where T : unmanaged
    {
        hash.Word(part == null ? 0u : 1u);
        if (part == null) return true;
        // XML documents BaseOffset as a destination partial-buffer update;
        // Count remains the number of source Values currently added.
        hash.Word((uint)part.InterleaveStride);
        hash.Word(part.Instanced ? 1u : 0u);
        bool known = hash.Array(part.Values, part.Count, required: part.Count != 0,
            reason: CasterSignatureReason.CustomStream);
        known &= hash.Array(part.InterleaveSizes, part.InterleaveSizes?.Length ?? 0);
        known &= hash.Array(part.InterleaveOffsets, part.InterleaveOffsets?.Length ?? 0);
        return known;
    }

    private struct Hash
    {
        internal ulong A = 0xcbf29ce484222325UL, B = 0x9e3779b97f4a7c15UL;
        internal long Bytes;
        internal CasterSignatureReason Reasons;

        public Hash()
        {
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void Word(ulong word)
        {
            unchecked
            {
                A = (A ^ word) * 0x100000001b3UL;
                B = BitOperations.RotateLeft(B + word * 0xc2b2ae3d27d4eb4fUL, 27) * 0x165667b19e3779f9UL;
            }
        }

        // Uploads can contain large active streams. Compile this arithmetic
        // loop at its optimized tier even in a diagnostic client Debug build.
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        internal bool Array<T>(T[]? data, int count, bool required = false,
            CasterSignatureReason reason = CasterSignatureReason.OptionalStream) where T : unmanaged
        {
            Word(data == null ? 0u : 1u);
            Word((uint)count);
            // Counters for absent optional tessellator streams can remain set.
            // Presence/count are hashed; only actual supplied slices are read.
            if (count < 0 || (data == null ? required : count > data.Length))
            {
                Reasons |= reason;
                return false;
            }

            if (data == null) return true;
            ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(data.AsSpan(0, count));
            Bytes += bytes.Length;
            int aligned = bytes.Length & ~7;
            ReadOnlySpan<ulong> words = MemoryMarshal.Cast<byte, ulong>(bytes[..aligned]);
            ulong a = A, b = B;
            // Read native 64-bit words directly; no per-word span slicing or
            // helper calls, including in the client's diagnostic Debug build.
            unchecked
            {
                foreach (ulong word in words)
                {
                    a = (a ^ word) * 0x100000001b3UL;
                    b += word * 0xc2b2ae3d27d4eb4fUL;
                    b = ((b << 27) | (b >> 37)) * 0x165667b19e3779f9UL;
                }
            }

            A = a;
            B = b;
            bytes = bytes[aligned..];
            ulong tail = 0;
            for (int i = 0; i < bytes.Length; i++) tail |= (ulong)bytes[i] << (i * 8);
            Word(tail);
            return true;
        }
    }
}