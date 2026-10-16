using System;
using System.Collections.Generic;
using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace DRTAgX;

/// <summary>CPU material metadata, built only when the block atlas changes.</summary>
internal sealed class LeafMaterialMask
{
    internal readonly record struct Page(int Width, int Height, int WordOffset);
    internal readonly record struct Rectangle(int Texture, float X1, float Y1, float X2, float Y2)
    {
        internal static Rectangle From(TextureAtlasPosition position) =>
            new(position.atlasTextureId, position.x1, position.y1, position.x2, position.y2);
    }

    internal readonly Dictionary<int, Page> Pages = new();
    internal uint[] Words = [0];

    internal static LeafMaterialMask Build(IEnumerable<Block> blocks, ITextureAtlasAPI atlas,
        IReadOnlyList<Rectangle> retired)
    {
        var result = new LeafMaterialMask();
        TextureAtlasPosition[] positions = atlas.Positions;
        if (positions == null || atlas.AtlasTextures == null) return result;
        var owners = new byte[positions.Length];
        var leafVisited = new HashSet<BakedCompositeTexture>(ReferenceEqualityComparer.Instance);
        var otherVisited = new HashSet<BakedCompositeTexture>(ReferenceEqualityComparer.Instance);
        foreach (Block block in blocks)
        {
            if (block?.Textures == null) continue;
            bool leaf = block.BlockMaterial == EnumBlockMaterial.Leaves;
            var visited = leaf ? leafVisited : otherVisited;
            foreach (CompositeTexture texture in block.Textures.Values)
                MarkOwners(texture?.Baked, leaf ? (byte)1 : (byte)2, owners, visited);
        }

        // The atlas's missing-texture tile must never become a leaf because a
        // mod happened to reference an absent leaf texture.
        Rectangle? unknown = atlas.UnknownTexturePosition is { } missing ? Rectangle.From(missing) : null;
        var dimensions = new Dictionary<int, (int Width, int Height)>();
        foreach (LoadedTexture texture in atlas.AtlasTextures)
            if (texture.TextureId > 0 && texture.Width > 0 && texture.Height > 0)
                dimensions[texture.TextureId] = (texture.Width, texture.Height);
        int wordCount = 0;
        for (int i = 0; i < owners.Length; ++i)
        {
            if (owners[i] != 1 || positions[i] is not { } position ||
                result.Pages.ContainsKey(position.atlasTextureId) ||
                !dimensions.TryGetValue(position.atlasTextureId, out var size)) continue;
            int count = checked((int)(((long)size.Width * size.Height + 31) / 32));
            result.Pages.Add(position.atlasTextureId, new Page(size.Width, size.Height, wordCount));
            wordCount = checked(wordCount + count);
        }
        result.Words = new uint[Math.Max(1, wordCount)];
        for (int i = 0; i < owners.Length; ++i)
            if (owners[i] == 1 && positions[i] is { } position)
                result.Fill(Rectangle.From(position), true);
        // A texture shared with a non-leaf cannot identify material uniquely.
        // Clear it conservatively, including overlapping/inclusive UV edges.
        for (int i = 0; i < owners.Length; ++i)
            if ((owners[i] & 2) != 0 && positions[i] is { } position)
                result.Fill(Rectangle.From(position), false);
        if (unknown is { } unknownRectangle) result.Fill(unknownRectangle, false);
        foreach (Rectangle rectangle in retired) result.Fill(rectangle, false);
        return result;
    }

    private static void MarkOwners(BakedCompositeTexture? texture, byte owner, byte[] owners,
        HashSet<BakedCompositeTexture> visited)
    {
        // BakedVariants/BakedTiles may include their parent; reference tracking
        // handles that native representation and cyclic custom definitions.
        if (texture == null || !visited.Add(texture)) return;
        if ((uint)texture.TextureSubId < (uint)owners.Length) owners[texture.TextureSubId] |= owner;
        if (texture.BakedVariants != null)
            foreach (var variant in texture.BakedVariants) MarkOwners(variant, owner, owners, visited);
        if (texture.BakedTiles != null)
            foreach (var tile in texture.BakedTiles) MarkOwners(tile, owner, owners, visited);
    }

    private void Fill(Rectangle rectangle, bool leaf)
    {
        if (!Pages.TryGetValue(rectangle.Texture, out Page page) ||
            !float.IsFinite(rectangle.X1) || !float.IsFinite(rectangle.Y1) ||
            !float.IsFinite(rectangle.X2) || !float.IsFinite(rectangle.Y2)) return;
        // TextureAtlasPosition.ContainsUV includes both endpoints. Native SSBO
        // UV unpacking and conventional meshes can land on either padded edge.
        int x1 = Math.Clamp((int)MathF.Floor(Math.Min(rectangle.X1, rectangle.X2) * page.Width), 0, page.Width - 1);
        int x2 = Math.Clamp((int)MathF.Floor(Math.Max(rectangle.X1, rectangle.X2) * page.Width), 0, page.Width - 1);
        int y1 = Math.Clamp((int)MathF.Floor(Math.Min(rectangle.Y1, rectangle.Y2) * page.Height), 0, page.Height - 1);
        int y2 = Math.Clamp((int)MathF.Floor(Math.Max(rectangle.Y1, rectangle.Y2) * page.Height), 0, page.Height - 1);
        for (int y = y1; y <= y2; ++y)
        {
            int first = y * page.Width + x1, last = y * page.Width + x2;
            for (int word = first >> 5; word <= last >> 5; ++word)
            {
                int low = word == first >> 5 ? first & 31 : 0;
                int high = word == last >> 5 ? last & 31 : 31;
                uint mask = (uint.MaxValue << low) & (uint.MaxValue >> (31 - high));
                int index = page.WordOffset + word;
                if (leaf) Words[index] |= mask;
                else Words[index] &= ~mask;
            }
        }
    }
}
