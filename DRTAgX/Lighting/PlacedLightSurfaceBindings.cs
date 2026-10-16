using System;
using System.Collections.Generic;
using OpenTK.Graphics.OpenGL4;
using Vintagestory.API.Client;
using Vintagestory.API.MathTools;
using Vintagestory.Client.NoObf;

namespace DRTAgX;

/// <summary>Memo binding scope composed inside the existing native Use/Stop bridge.</summary>
internal sealed class PlacedLightSurfaceBindings(PlacedLightSurfaceCache cache, ICoreClientAPI api) : IDisposable
{
    private readonly Dictionary<int, Locations?> _locations = new();
    private readonly double[] _inverse = new double[16];
    private readonly Vec3d _camera = new();
    private bool _bound, _memoBound, _writeBarrier;
    private int _generic, _texture, _sampler;
    private IndexedBufferState _memo, _sources;

    private sealed class Locations(int program, int enabled, int animatedBlock)
    {
        internal readonly int Enabled = enabled;
        internal readonly int Generation = GL.GetUniformLocation(program, "drtMemoGeneration");
        internal readonly int Frame = GL.GetUniformLocation(program, "drtMemoFrame");
        internal readonly int Mask = GL.GetUniformLocation(program, "drtMemoCapacityMask");
        internal readonly int Limit = GL.GetUniformLocation(program, "drtMemoFillLimit");
        internal readonly int Diagnostics = GL.GetUniformLocation(program, "drtMemoDiagnostics");
        internal readonly int Anchor = GL.GetUniformLocation(program, "drtMemoAnchorCells");
        internal readonly int Camera = GL.GetUniformLocation(program, "drtMemoCameraOffset");
        internal readonly int Count = GL.GetUniformLocation(program, "drtMemoSourceCount");
        internal readonly int RegionOffset = GL.GetUniformLocation(program, "drtMemoRegionOffset");
        internal readonly int Maps = GL.GetUniformLocation(program, "drtStaticMaps");
        internal readonly int BoundsReady = GL.GetUniformLocation(program, "drtMemoBoundsReady");
        internal readonly int BoundsMin = GL.GetUniformLocation(program, "drtMemoBoundsMin");
        internal readonly int BoundsMax = GL.GetUniformLocation(program, "drtMemoBoundsMax");
        internal readonly int Regions = GL.GetUniformLocation(program, "drtMemoRegionsEnabled");
        internal readonly int Transition = GL.GetUniformLocation(program, "drtMemoTransitionFrames");

        internal readonly int AnimatedBlock = animatedBlock;
        internal readonly bool UsesReceiverPlane = GL.GetUniformLocation(program, "drtReceiverPlaneReady") >= 0;

        // The owned model helper declares this source-only block readonly and
        // has no page buffer. Check the linked interface once, rather than infer
        // resource use from a pass name or borrow the full page cache per model.
        internal readonly bool SourceOnlyReceiver =
            GL.GetProgramResourceIndex(program, ProgramInterface.ShaderStorageBlock,
                "DrtPlacedReceiverSources") >= 0 &&
            GL.GetProgramResourceIndex(program, ProgramInterface.ShaderStorageBlock,
                "DrtPlacedMemoBuffer") < 0;
    }

    internal void Bind(int program)
    {
        Restore();
        if (!_locations.TryGetValue(program, out var locations))
        {
            int enabled = GL.GetUniformLocation(program, "drtMemoEnabled");
            int animatedBlock = GL.GetUniformLocation(program, "drtAnimatedBlockDraw");
            locations = enabled >= 0 || animatedBlock >= 0 ? new Locations(program, enabled, animatedBlock) : null;
            _locations.Add(program, locations);
        }

        if (locations == null) return;
        bool blockDraw = AnimatedBlockLightingScope.Active;
        // Native Use is reached only for an actual draw. The renderer scope is
        // CPU-only, so distant/culled callbacks no longer write both shader
        // programs. Reassert zero here for every subsequent creature/hand draw,
        // including PLS-off variants where the memo uniform is optimized away.
        if (locations.AnimatedBlock >= 0) GL.Uniform1(locations.AnimatedBlock, blockDraw ? 1 : 0);
        if (locations.Enabled < 0) return;

        // 0 is reserved for explicit cache-off comparisons. Allocation/reload
        // failures use 2 (native lighting), never unbounded legacy receiver work.
        // Empty snapshots cannot illuminate anything. Keep the native result
        // without querying/binding GL resources for hundreds of model draws.
        bool receiving = locations.AnimatedBlock < 0 || blockDraw;
        // Source publication follows terrain geometry. If an empty scene gains
        // its first light mid-frame, the deferred reader must not consume stale
        // plane/bounds textures from an earlier frame. Native/model receivers
        // without this owned plane contract continue using their own geometry.
        receiving &= !locations.UsesReceiverPlane || DeferredReceiverResources.Current is { Ready: true };
        int mode = cache.Active && cache.Ready && cache.SourceCount > 0 && receiving ? 1 : 2;
        GL.Uniform1(locations.Enabled, mode);
        // Clear stale counts on all disabled paths, including ordinary mobs
        // sharing the model shader and the frame after the last light is gone.
        if (locations.Count >= 0) GL.Uniform1(locations.Count, mode == 1 ? cache.SourceCount : 0);
        if (mode != 1) return;
        if (locations.Generation >= 0) GL.Uniform1(locations.Generation, cache.Generation);
        if (locations.Frame >= 0) GL.Uniform1(locations.Frame, cache.Frame);
        if (locations.Mask >= 0) GL.Uniform1(locations.Mask, cache.PageSetMask);
        if (locations.Limit >= 0) GL.Uniform1(locations.Limit, (uint)PlacedLightSurfaceCache.FillLimit);
        if (locations.Diagnostics >= 0) GL.Uniform1(locations.Diagnostics, cache.Diagnostics ? 1 : 0);
        if (locations.Anchor >= 0)
            GL.Uniform3(locations.Anchor, cache.AnchorCellX, cache.AnchorCellY, cache.AnchorCellZ);
        if (locations.RegionOffset >= 0)
            GL.Uniform1(locations.RegionOffset, PlacedLightSurfaceCache.SourcePrefixBytes / 16);
        if (locations.Maps >= 0) GL.Uniform1(locations.Maps, 14);
        if (locations.BoundsReady >= 0) GL.Uniform1(locations.BoundsReady, cache.BoundsReady ? 1 : 0);
        if (locations.Regions >= 0) GL.Uniform1(locations.Regions, 1);
        if (locations.Transition >= 0) GL.Uniform1(locations.Transition, PlacedLightUpdateExperiment.TransitionFrames);
        if (locations.BoundsMin >= 0) GL.Uniform3(locations.BoundsMin, cache.BoundsMinimum);
        if (locations.BoundsMax >= 0) GL.Uniform3(locations.BoundsMax, cache.BoundsMaximum);
        if (locations.Camera >= 0 && api.World is ClientMain world)
        {
            // The native camera changes after Before. Rebase the fixed world
            // anchor using this draw's exact double matrix, including early fills.
            PlacedLightView.CameraFromView(world.CurrentModelViewMatrixd, _inverse, _camera);
            GL.Uniform3(locations.Camera, cache.CameraOffset(_camera.X, _camera.Y, _camera.Z));
        }

        _generic = GL.GetInteger(GetPName.ShaderStorageBufferBinding);
        _memoBound = !locations.SourceOnlyReceiver;
        _writeBarrier = !locations.SourceOnlyReceiver;
        if (_memoBound) _memo = IndexedBufferState.Capture(BufferRangeTarget.ShaderStorageBuffer, 6);
        _sources = IndexedBufferState.Capture(BufferRangeTarget.ShaderStorageBuffer, 7);
        int active = GL.GetInteger(GetPName.ActiveTexture);
        GL.ActiveTexture(TextureUnit.Texture14);
        _texture = GL.GetInteger(GetPName.TextureBinding2DArray);
        _sampler = GL.GetInteger(GetPName.SamplerBinding);
        _bound = true;
        if (_memoBound) GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 6, cache.Buffer);
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 7, cache.SourceBuffer);
        GL.BindSampler(14, 0);
        GL.BindTexture(TextureTarget.Texture2DArray, cache.ShadowTexture);
        GL.ActiveTexture((TextureUnit)active);
        GL.BindBuffer(BufferTarget.ShaderStorageBuffer, _generic);
    }

    internal void Restore()
    {
        if (!_bound) return;
        // Source-only model receivers never publish pages or atomics. Their
        // producer already fences completed compute writes; inserting a write
        // barrier after each read-only model needlessly serializes submissions.
        // Retain the existing barrier for every other/unknown cache consumer.
        if (_writeBarrier) GL.MemoryBarrier(MemoryBarrierFlags.ShaderStorageBarrierBit);
        int active = GL.GetInteger(GetPName.ActiveTexture);
        GL.ActiveTexture(TextureUnit.Texture14);
        GL.BindTexture(TextureTarget.Texture2DArray, _texture);
        GL.BindSampler(14, _sampler);
        GL.ActiveTexture((TextureUnit)active);
        if (_memoBound) _memo.Restore();
        _sources.Restore();
        GL.BindBuffer(BufferTarget.ShaderStorageBuffer, _generic);
        _bound = _memoBound = _writeBarrier = false;
    }

    internal void Reload()
    {
        Restore();
        _locations.Clear();
    }

    public void Dispose()
    {
        Restore();
        _locations.Clear();
    }
}