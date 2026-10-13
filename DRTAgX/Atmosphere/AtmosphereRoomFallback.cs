using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace DRTAgX;

/// <summary>Bounded structural enclosure check for spaces larger than native rooms.</summary>
internal sealed class AtmosphereRoomFallback
{
    internal const int MaxDistance = 64;
    private readonly BlockPos _current = new(0), _next = new(0);
    private readonly int[] _boundary = new int[6];

    internal bool TryFind(IBlockAccessor blocks, BlockPos camera, Cuboidi bounds)
    {
        // BlockPos.Set copies XYZ only; preserve the dimension explicitly when
        // reusing scratch positions across normal-world/other-world captures.
        _current.dimension = _next.dimension = camera.dimension;
        // A roof alone is insufficient: dense forest canopies must keep weather
        // fog. Require all six boundaries and fully loaded geometry in between.
        foreach (var face in BlockFacing.ALLFACES)
            if (!Trace(blocks, camera, face, out _boundary[face.Index]))
                return false;

        // Native face order: north/east/south/west/up/down. Tracing returns the
        // last interior block, so positive-side bounds become exclusive here.
        bounds.Set(_boundary[3], _boundary[5], _boundary[0],
            _boundary[1] + 1, _boundary[4] + 1, _boundary[2] + 1);
        return ConfirmPlanes(blocks, bounds);
    }

    private bool ConfirmPlanes(IBlockAccessor blocks, Cuboidi bounds)
    {
        // Cardinal hits alone can describe four posts under a roof. Check a
        // small grid on all six boundary planes as well; no flood or frame work.
        foreach (var face in BlockFacing.ALLFACES)
            for (int a = 0; a < 3; a++)
            for (int b = 0; b < 3; b++)
            {
                if (face.Axis == EnumAxis.X)
                    _current.Set(face.Normali.X < 0 ? bounds.X1 : bounds.X2 - 1,
                        Sample(bounds.Y1, bounds.Y2, a), Sample(bounds.Z1, bounds.Z2, b));
                else if (face.Axis == EnumAxis.Y)
                    _current.Set(Sample(bounds.X1, bounds.X2, a),
                        face.Normali.Y < 0 ? bounds.Y1 : bounds.Y2 - 1, Sample(bounds.Z1, bounds.Z2, b));
                else
                    _current.Set(Sample(bounds.X1, bounds.X2, a), Sample(bounds.Y1, bounds.Y2, b),
                        face.Normali.Z < 0 ? bounds.Z1 : bounds.Z2 - 1);
                if (!IsLoaded(blocks, _current)) return false;
                var inside = blocks.GetBlock(_current);
                if (!IsFoliage(inside) && inside.GetRetention(_current, face, EnumRetentionType.Heat) != 0) continue;
                _next.Set(_current);
                _next.Add(face);
                if (!IsLoaded(blocks, _next)) return false;
                var outside = blocks.GetBlock(_next);
                if (IsFoliage(outside) || outside.GetRetention(_next, face.Opposite, EnumRetentionType.Heat) == 0)
                    return false;
            }

        return true;
    }

    private static int Sample(int minimum, int maximum, int index) => minimum + (maximum - minimum - 1) * index / 2;

    private bool Trace(IBlockAccessor blocks, BlockPos camera, BlockFacing face, out int coordinate)
    {
        coordinate = 0;
        _current.Set(camera);
        if (!IsLoaded(blocks, _current)) return false;
        var current = blocks.GetBlock(_current);
        for (int step = 0; step < MaxDistance; step++)
        {
            if (!IsFoliage(current) && current.GetRetention(_current, face, EnumRetentionType.Heat) != 0)
                return Boundary(face, out coordinate);
            _next.Set(_current);
            _next.Add(face);
            if (!IsLoaded(blocks, _next)) return false;
            var next = blocks.GetBlock(_next);
            if (!IsFoliage(next) && next.GetRetention(_next, face.Opposite, EnumRetentionType.Heat) != 0)
                return Boundary(face, out coordinate);
            _current.Set(_next);
            current = next;
        }

        return false;
    }

    private bool Boundary(BlockFacing face, out int coordinate)
    {
        coordinate = face.Axis == EnumAxis.X ? _current.X : face.Axis == EnumAxis.Y ? _current.Y : _current.Z;
        return true;
    }

    internal static bool IsLoaded(IBlockAccessor blocks, BlockPos pos) =>
        blocks.IsValidPos(pos) && blocks.GetChunkAtBlockPos(pos) is { Disposed: false };

    internal static bool IsFoliage(Block block) =>
        // Thatch, hay and sod are Plant too. Only readily replaceable plants
        // (the native water-washable range) are vegetation, not building roofs.
        block.BlockMaterial == EnumBlockMaterial.Leaves ||
        block.BlockMaterial == EnumBlockMaterial.Plant && block.Replaceable > 5000;
}