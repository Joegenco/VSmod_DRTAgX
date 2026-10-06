using System;
using System.Collections.Generic;

namespace DRTAgX;

// Pure edit/caster coverage shared by residents, admissions and replacements.
// Exact edits use the requested 16-block radius; completed chunk uploads retain
// the full 22-block caster reach, including geometry beyond that edit radius.
internal static class PlacedLightGeometry
{
    internal const double EditRadius = 16, EditFallbackSeconds = 1.5;

    internal static bool EditsTouch(StaticLightSources.Source source,
        IReadOnlyList<StaticLightSources.Position> edits, bool ignoreEmitter = false)
    {
        for (int i = 0; i < edits.Count; i++)
        {
            if (ignoreEmitter && edits[i] == StaticLightSources.Identity(source)) continue;
            var edit = edits[i];
            if (Touches(source, edit.X, edit.Y, edit.Z, 1, EditRadius)) return true;
        }
        return false;
    }

    internal static bool ChunksTouch(StaticLightSources.Source source,
        IReadOnlyList<StaticLightSources.ChunkKey> chunks)
    {
        for (int i = 0; i < chunks.Count; i++)
            if (IntersectsChunk(source, chunks[i])) return true;
        return false;
    }

    internal static bool IntersectsChunk(StaticLightSources.Source source, StaticLightSources.ChunkKey chunk) =>
        Touches(source, chunk.X * 32, chunk.Y * 32, chunk.Z * 32, 32, StaticTerrainShadowMaps.Range);

    private static bool Touches(StaticLightSources.Source source, int x, int y, int z, int size, double radius)
    {
        // Squared point/AABB distance tests the entire edited voxel/chunk, so a
        // caster touching the radius is not lost by using only its centre.
        double dx = Math.Max(x - (source.X + 0.5), source.X + 0.5 - (x + size));
        double dy = Math.Max(y - (source.Y + StaticTerrainShadowMaps.SourceHeight),
            source.Y + StaticTerrainShadowMaps.SourceHeight - (y + size));
        double dz = Math.Max(z - (source.Z + 0.5), source.Z + 0.5 - (z + size));
        dx = Math.Max(dx, 0); dy = Math.Max(dy, 0); dz = Math.Max(dz, 0);
        return dx * dx + dy * dy + dz * dz <= radius * radius;
    }
}
