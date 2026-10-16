using System;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace DRTAgX;

/// <summary>Cached native room membership for camera-relative atmospheric transport.</summary>
internal sealed class AtmosphereRoomShelter
{
    internal const int FrameOffset = 120;
    internal const int FrameFloatCount = 896;
    internal const int MaxNativeCells = 29 * 29 * 29;
    private const int MaskOffset = FrameOffset + 8;
    private const int RefreshMilliseconds = 250;
    private const int FadeMilliseconds = 2000;
    private readonly ICoreClientAPI _api;
    private readonly BlockPos _lastCameraBlock = new(0), _roofPosition = new(0);
    private readonly AtmosphereRoomFallback _fallback = new();
    private readonly float[] _mask = new float[FrameFloatCount - MaskOffset];
    private readonly Cuboidi _bounds = new();
    private IWorldAccessor? _world;
    private RoomRegistry? _registry;
    private Room? _packedRoom;
    private float[]? _publishedFrame;
    private long _nextRefresh, _shelterEnteredAt, _lastCaptureAt;
    private bool _snapshot, _active, _rectangular, _packedRectangular, _maskDirty;

    internal AtmosphereRoomShelter(ICoreClientAPI api) => _api = api;

    // Main/render thread only: native RoomRegistry shares its flood-fill scratch.
    // Only bounds and fade strength change each frame; topology and bits stay cached.
    internal void Capture(BlockPos cameraBlock, Vec3d camera, float[] frame)
    {
        var world = _api.World;
        bool changedWorld = !ReferenceEquals(_world, world);
        if (changedWorld)
        {
            _world = world;
            _registry = _api.ModLoader.GetModSystem<RoomRegistry>();
            _packedRoom = null;
            _snapshot = false;
            _active = false;
        }

        long now = world.ElapsedMilliseconds;
        var clockReset = now < _lastCaptureAt;
        _lastCaptureAt = now;
        var wasActive = _active && _lastCameraBlock.dimension == cameraBlock.dimension;
        if (!_snapshot || clockReset || _lastCameraBlock.dimension != cameraBlock.dimension ||
            !_lastCameraBlock.Equals(cameraBlock) || now >= _nextRefresh)
        {
            _lastCameraBlock.Set(cameraBlock);
            _lastCameraBlock.dimension = cameraBlock.dimension;
            _nextRefresh = now + RefreshMilliseconds;
            _snapshot = true;
            Refresh(cameraBlock);
        }

        // Fade only on entering shelter, not on each room-cache refresh or step
        // inside it. World/dimension changes and a reset clock start a new fade.
        if (_active && (!wasActive || clockReset)) _shelterEnteredAt = now;
        var fade = _active ? Math.Clamp((now - _shelterEnteredAt) / (float)FadeMilliseconds, 0f, 1f) : 0f;
        frame[FrameOffset + 3] = fade * fade * (3f - 2f * fade);
        frame[FrameOffset + 7] = _rectangular ? 1f : 0f;
        if (!_active) return;

        // Room Y is dimension-local; render camera Y includes the dimension.
        double localCameraY = camera.Y - (cameraBlock.InternalY - cameraBlock.Y);
        frame[FrameOffset] = (float)(_bounds.X1 - camera.X);
        frame[FrameOffset + 1] = (float)(_bounds.Y1 - localCameraY);
        frame[FrameOffset + 2] = (float)(_bounds.Z1 - camera.Z);
        frame[FrameOffset + 4] = (float)(_bounds.X2 - camera.X);
        frame[FrameOffset + 5] = (float)(_bounds.Y2 - localCameraY);
        frame[FrameOffset + 6] = (float)(_bounds.Z2 - camera.Z);
        if (_maskDirty || !ReferenceEquals(_publishedFrame, frame))
        {
            Array.Copy(_mask, 0, frame, MaskOffset, _mask.Length);
            _publishedFrame = frame;
            _maskDirty = false;
        }
    }

    private void Refresh(BlockPos camera)
    {
        _active = false;
        var blocks = _api.World.BlockAccessor;
        if (!AtmosphereRoomFallback.IsLoaded(blocks, camera)) return;

        // Rain height is maintained by the engine, but is defined only in the
        // normal dimension. It cheaply avoids native flood fills while outdoors.
        if (camera.dimension == 0 && !HasStructuralCover(blocks, camera)) return;

        Room? room = camera.dimension == 0 ? _registry?.GetRoomForPosition(camera) : null;
        if (room is { ExitCount: 0, AnyChunkUnloaded: 0 } && room.Contains(camera) && TryUseRoom(room))
        {
            _active = true;
            return;
        }

        // Native rooms have a 14-block span limit. A bounded enclosure check
        // supplies a conservative rectangular fallback for larger buildings.
        if (_fallback.TryFind(blocks, camera, _bounds))
        {
            _rectangular = true;
            _active = true;
        }
    }

    private bool HasStructuralCover(IBlockAccessor blocks, BlockPos camera)
    {
        int rainHeight = blocks.GetRainMapHeightAt(camera);
        if (rainHeight <= camera.Y) return false;
        _roofPosition.Set(camera);
        _roofPosition.dimension = camera.dimension;
        _roofPosition.Y = rainHeight;
        if (!AtmosphereRoomFallback.IsLoaded(blocks, _roofPosition))
            return HasLocalRoof(blocks, camera, rainHeight);
        var roof = blocks.GetBlock(_roofPosition);
        // Some height maps address the first air cell above the blocking block.
        if (roof.Id == 0)
        {
            _roofPosition.Y--;
            if (!AtmosphereRoomFallback.IsLoaded(blocks, _roofPosition))
                return HasLocalRoof(blocks, camera, rainHeight);
            roof = blocks.GetBlock(_roofPosition);
        }

        if (!AtmosphereRoomFallback.IsFoliage(roof)) return true;
        return HasLocalRoof(blocks, camera, rainHeight);
    }

    private bool HasLocalRoof(IBlockAccessor blocks, BlockPos camera, int rainHeight)
    {
        // A tree above a real roof must not hide that building. Search only this
        // column, also covering a loaded house below an unloaded high platform.
        for (int y = camera.Y + 1, end = Math.Min(rainHeight, camera.Y + AtmosphereRoomFallback.MaxDistance);
             y <= end;
             y++)
        {
            _roofPosition.Y = y;
            if (!AtmosphereRoomFallback.IsLoaded(blocks, _roofPosition)) return false;
            var roof = blocks.GetBlock(_roofPosition);
            if (!AtmosphereRoomFallback.IsFoliage(roof) &&
                roof.GetRetention(_roofPosition, BlockFacing.DOWN, EnumRetentionType.Heat) != 0) return true;
        }

        return false;
    }

    private bool TryUseRoom(Room room)
    {
        var location = room.Location;
        int sx = location.X2 - location.X1 + 1,
            sy = location.Y2 - location.Y1 + 1,
            sz = location.Z2 - location.Z1 + 1;
        if (sx <= 0 || sy <= 0 || sz <= 0 || sx > 29 || sy > 29 || sz > 29) return false;
        int cells = sx * sy * sz;
        if (room.PosInRoom.Length < (cells + 7) / 8) return false;
        _bounds.Set(location.X1, location.Y1, location.Z1, location.X2 + 1, location.Y2 + 1, location.Z2 + 1);
        if (ReferenceEquals(_packedRoom, room))
        {
            _rectangular = _packedRectangular;
            return true;
        }

        _packedRoom = room;
        _rectangular = true;
        Array.Clear(_mask);
        for (int word = 0, count = (cells + 31) / 32; word < count; word++)
        {
            uint bits = 0;
            for (int b = 0; b < 4 && word * 4 + b < room.PosInRoom.Length; b++)
                bits |= (uint)room.PosInRoom[word * 4 + b] << (b * 8);
            int remaining = cells - word * 32;
            uint valid = remaining >= 32 ? uint.MaxValue : (1u << remaining) - 1u;
            bits &= valid;
            _rectangular &= bits == valid;
            // Preserve the native little-endian bit layout without arithmetic:
            // GLSL reads these same four-byte words as uvec4, not float values.
            _mask[word] = BitConverter.Int32BitsToSingle(unchecked((int)bits));
        }

        _packedRectangular = _rectangular;
        _maskDirty = true;
        return true;
    }
}