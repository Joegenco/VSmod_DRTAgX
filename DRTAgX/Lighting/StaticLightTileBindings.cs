using System;
using System.Collections.Generic;
using OpenTK.Graphics.OpenGL4;
using Vintagestory.API.Client;
using Vintagestory.API.MathTools;

namespace DRTAgX;

/// <summary>
/// GPU buffer manager for placed-light records and compute-generated 16x16 tile masks.
/// Packs eye-space emitter positions, prepared native RGB and relative geometry, and per-tile candidate lists into SSBOs (bindings 4 and 5),
/// binding the static shadow atlas to texture unit 14 for deferred lighting evaluation.
/// </summary>
internal sealed class StaticLightTileBindings : IDisposable
{
    private const int TileSize = 16;
    private const int MaskWords = 5;
    private readonly float[] _lightData = new float[StaticTerrainShadowMaps.MaxPublishedSources * StaticLightGpuRecord.FloatCount];
    private readonly int[] _colourKeys = new int[StaticTerrainShadowMaps.MaxPublishedSources];
    private readonly float[] _inverseView = new float[16], _inverseProjection = new float[16];
    private readonly double[] _sourceCameraInverse = new double[16];
    internal Vec3d SourceCamera { get; } = new();
    private readonly int[] _rectangles = new int[StaticTerrainShadowMaps.MaxPublishedSources * 4];
    private readonly int[] _previousRectangles = new int[StaticTerrainShadowMaps.MaxPublishedSources * 4];
    private readonly float[] _preparedOrigin = new float[16];
    private readonly PlacedLightViewState _preparedView = new();
    private int _preparedRevision = -1, _preparedCount, _preparedWidth, _preparedHeight, _preparedTexture;
    private bool _sourceDataValid, _coarseMasksValid, _projectionInvertible;
    private double _preparedCandidates;
    private readonly StaticLightTileCompute _compute = new();
    private int _tileBytes, _program, _countLocation, _mapLocation, _blendLocation, _widthLocation, _allPassesLocation;
    private int _sourceBuffer, _tileBuffer;
    private int _oldGenericBuffer;
    private IndexedBufferState _oldSourceBuffer, _oldTileBuffer;
    private int _oldTexture14, _oldSampler14;
    private bool _pending;
    private float _blend;
    private float _preparedSaturation = float.NaN;
    internal double LastPrepareCpuMilliseconds { get; private set; }
    internal int PublishedCount { get; private set; }
    internal double RectangleCandidatesPerTile { get; private set; }
    internal bool DepthCulling { get; private set; }
    internal bool ViewCullingEnabled { get; set; } = true;
    internal bool AllTerrainPassesEnabled { get; set; }
    internal int SourceBuffer => _sourceBuffer;
    internal float PlsSaturation { get; set; } = AgxGuiDialog.DefaultPlsSaturation;
    internal int LightRevision { get; set; } = -1;
    internal bool UploadedRecordsThisFrame { get; private set; }
    internal bool GeneratedMasksThisFrame { get; private set; }
    internal StaticLightTileBindings() => Array.Fill(_colourKeys, -1);
    internal void Reload()
    {
        Release(); _program = 0; _compute.Reset();
        _sourceDataValid = _coarseMasksValid = false; _preparedView.Reset();
    }

    private void Locations(int program)
    {
        if (_program == program) return;
        _program = program;
        _sourceDataValid = _coarseMasksValid = false;
        _countLocation = GL.GetUniformLocation(program, "drtStaticCount");
        _mapLocation = GL.GetUniformLocation(program, "drtStaticMaps");
        _blendLocation = GL.GetUniformLocation(program, "drtStaticBlend");
        _widthLocation = GL.GetUniformLocation(program, "drtStaticTileWidth");
        _allPassesLocation = GL.GetUniformLocation(program, "drtStaticAllTerrainPasses");
    }
    internal bool IsBound => _pending;

    internal void Prepare(ICoreClientAPI api, IShaderProgram deferred,
        IReadOnlyList<StaticTerrainShadowMaps.ActiveLight> lights, int shadowTexture,
        bool ready, float deltaTime)
    {
        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        PublishedCount = 0; RectangleCandidatesPerTile = 0; DepthCulling = false;
        UploadedRecordsThisFrame = GeneratedMasksThisFrame = false;
        Release();
        int program = deferred.ProgramId;
        int previousProgram = GL.GetInteger(GetPName.CurrentProgram);
        GL.UseProgram(program);
        try
        {
        Locations(program);
        // Publish the caster/receiver mode together; legacy foliage remains
        // normal-based only when the user explicitly disables all-pass PLS.
        if (_allPassesLocation >= 0) GL.Uniform1(_allPassesLocation, AllTerrainPassesEnabled ? 1 : 0);
        int countLocation = _countLocation;
        // Sampler types must occupy distinct units even while the static branch
        // is disabled; leaving this sampler at default unit 0 invalidates draw.
        int mapLocation = _mapLocation;
        if (mapLocation >= 0) GL.Uniform1(mapLocation, 14);
        // Ready maps are available independently. Their individual fade is
        // carried in each source record; this gate never restarts it.
        _blend = ready && !_compute.Failed ? 1f : 0f;
        int blendLocation = _blendLocation;
        if (blendLocation >= 0) GL.Uniform1(blendLocation, _blend);
        if (!ready || _compute.Failed || shadowTexture == 0 || lights.Count == 0)
        {
            if (countLocation >= 0) GL.Uniform1(countLocation, 0);
            return;
        }
        // Deferred fragments use the primary target's pixels, not window
        // pixels. Resolution scaling/SSAA can make those dimensions differ;
        // mixing them shifts edge-light masks and drops visible receivers.
        var buffers = api.Render.FrameBuffers;
        var primary = buffers.Count > 0 ? buffers[0] : null;
        bool primaryValid = primary is { Disposed: false, Width: > 0, Height: > 0 };
        int frameWidth = primaryValid ? primary!.Width : api.Render.FrameWidth;
        int frameHeight = primaryValid ? primary!.Height : api.Render.FrameHeight;
        int width = (frameWidth + TileSize - 1) / TileSize;
        int height = (frameHeight + TileSize - 1) / TileSize;
        if (width <= 0 || height <= 0)
        {
            if (countLocation >= 0) GL.Uniform1(countLocation, 0);
            return;
        }
        int tileBytes = checked(width * height * MaskWords * sizeof(uint));

        // Native full-world view is double precision. Convert each source
        // directly into the current eye space to avoid large-world jitter.
        double[] view = ((Vintagestory.Client.NoObf.ClientMain)api.World).CurrentModelViewMatrixd;
        float[] projection = api.Render.CurrentProjectionMatrix;
        float[] origin = api.Render.CameraMatrixOriginf;
        int count = Math.Min(lights.Count, StaticTerrainShadowMaps.MaxPublishedSources);
        // Slider edits refresh only source RGB; cached shadow maps remain valid.
        bool saturationChanged = _preparedSaturation != PlsSaturation;
        bool changed = saturationChanged || !_sourceDataValid || LightRevision < 0 || _preparedRevision != LightRevision ||
            _preparedCount != count || _preparedTexture != shadowTexture ||
            _preparedWidth != frameWidth || _preparedHeight != frameHeight;
        changed |= _preparedView.Update(view, projection);
        for (int i = 0; i < 16 && !changed; i++) changed = _preparedOrigin[i] != origin[i];
        if (changed)
        {
        // Retain the exact camera of these records. Early forward terrain can
        // draw before this frame's publication and must rebase the old snapshot.
        PlacedLightView.CameraFromView(view, _sourceCameraInverse, SourceCamera);
        Mat4f.Invert(_inverseView, origin);
        // The camera projection can stay fixed for thousands of depth dispatches.
        // Invert it when preparing changed inputs, preserving singular fallback.
        _projectionInvertible = Mat4f.Invert(_inverseProjection, projection) != null;
        for (int i = 0; i < count; i++)
        {
            var light = lights[i];
            double x = light.Source.X + 0.5,
                y = light.Source.Y + StaticTerrainShadowMaps.SourceHeight,
                z = light.Source.Z + 0.5;
            float ex = (float)(view[0] * x + view[4] * y + view[8] * z + view[12]);
            float ey = (float)(view[1] * x + view[5] * y + view[9] * z + view[13]);
            float ez = (float)(view[2] * x + view[6] * y + view[10] * z + view[14]);
            // Keep the gameplay cap while giving the shadowed direct light a
            // broader shoulder than the voxel light's integer-level reach.
            float range = PlacedLightView.Reach(light.Source);
            // Retain eye-space source/range for distance and tile culling.
            // Two source-only calculations now happen here rather than per pixel.
            int offset = i * StaticLightGpuRecord.FloatCount;
            _lightData[offset] = ex;
            _lightData[offset + 1] = ey;
            _lightData[offset + 2] = ez;
            _lightData[offset + 3] = range;
            int colourKey = light.Source.Hue * 256 + light.Source.Saturation;
            if (_colourKeys[i] != colourKey || saturationChanged)
            {
                StaticLightGpuRecord.Rgb(light.Source.Hue, light.Source.Saturation, PlsSaturation,
                    out _lightData[offset + 4], out _lightData[offset + 5], out _lightData[offset + 6]);
                _colourKeys[i] = colourKey;
            }
            _lightData[offset + 7] = light.Slot;
            _lightData[offset + 8] = Math.Clamp(light.Fade, 0f, 1f);
            _lightData[offset + 9] = light.ShadowBlend;
            _lightData[offset + 10] = light.SecondarySlot;
            _lightData[offset + 11] = light.ReplacementPair ? 1f : 0f;
            StaticLightGpuRecord.RotateEye(_lightData, offset + 12, ex, ey, ez, _inverseView);
            _lightData[offset + 15] = light.Source.Level;

            // A conservative projected sphere rectangle. Sources intersecting
            // the near plane cover all tiles rather than risking a missed pixel.
            int minX = 0, maxX = width - 1, minY = 0, maxY = height - 1;
            int rectangle = i * 4;
            // An empty inclusive rectangle cannot set any tile bits.
            _rectangles[rectangle] = _rectangles[rectangle + 1] = 1;
            _rectangles[rectangle + 2] = _rectangles[rectangle + 3] = 0;
            PlacedLightView.Bounds bounds = light.Bounds;
            if (bounds.Class > 1 || light.Fade <= 0) continue;
            // A fringe-class source can miss the viewport entirely. Reject it
            // before clamping, or it contaminates one border tile.
            if (bounds.MaxX < -1f || bounds.MinX > 1f ||
                bounds.MaxY < -1f || bounds.MinY > 1f) continue;
            if (bounds.MinX > -1 || bounds.MaxX < 1 || bounds.MinY > -1 || bounds.MaxY < 1)
            {
                minX = Math.Clamp((int)MathF.Floor((bounds.MinX + 1f) * 0.5f * frameWidth / TileSize), 0, width - 1);
                maxX = Math.Clamp((int)MathF.Ceiling((bounds.MaxX + 1f) * 0.5f * frameWidth / TileSize), 0, width - 1);
                minY = Math.Clamp((int)MathF.Floor((bounds.MinY + 1f) * 0.5f * frameHeight / TileSize), 0, height - 1);
                maxY = Math.Clamp((int)MathF.Ceiling((bounds.MaxY + 1f) * 0.5f * frameHeight / TileSize), 0, height - 1);
            }
            _rectangles[rectangle] = minX;
            _rectangles[rectangle + 1] = minY;
            _rectangles[rectangle + 2] = maxX;
            _rectangles[rectangle + 3] = maxY;
            RectangleCandidatesPerTile += (double)(maxX - minX + 1) * (maxY - minY + 1);
        }
        PublishedCount = count;
        RectangleCandidatesPerTile /= width * height;
        _preparedCandidates = RectangleCandidatesPerTile;
        }
        else
        {
            RectangleCandidatesPerTile = _preparedCandidates;
        }
        PublishedCount = count;
        bool masksChanged = !_coarseMasksValid || _preparedCount != count ||
            _preparedWidth != frameWidth || _preparedHeight != frameHeight;
        for (int i = 0; changed && i < count * 4 && !masksChanged; i++)
            masksChanged = _previousRectangles[i] != _rectangles[i];

        _sourceBuffer = _sourceBuffer == 0 ? GL.GenBuffer() : _sourceBuffer;
        _tileBuffer = _tileBuffer == 0 ? GL.GenBuffer() : _tileBuffer;
        _oldGenericBuffer = GL.GetInteger(GetPName.ShaderStorageBufferBinding);
        _oldSourceBuffer = IndexedBufferState.Capture(BufferRangeTarget.ShaderStorageBuffer, 4);
        _oldTileBuffer = IndexedBufferState.Capture(BufferRangeTarget.ShaderStorageBuffer, 5);
        int oldActive = GL.GetInteger(GetPName.ActiveTexture);
        GL.ActiveTexture(TextureUnit.Texture14);
        _oldTexture14 = GL.GetInteger(GetPName.TextureBinding2DArray);
        _oldSampler14 = GL.GetInteger(GetPName.SamplerBinding);
        GL.ActiveTexture((TextureUnit)oldActive);
        _pending = true;
        if (changed)
        {
            GL.BindBuffer(BufferTarget.ShaderStorageBuffer, _sourceBuffer);
            GL.BufferData(BufferTarget.ShaderStorageBuffer, count * StaticLightGpuRecord.FloatCount * sizeof(float),
                _lightData, BufferUsageHint.StreamDraw);
            UploadedRecordsThisFrame = true;
        }
        GL.BindBuffer(BufferTarget.ShaderStorageBuffer, _tileBuffer);
        if (_tileBytes != tileBytes)
        {
            GL.BufferData(BufferTarget.ShaderStorageBuffer, tileBytes, IntPtr.Zero, BufferUsageHint.DynamicDraw);
            _tileBytes = tileBytes;
        }
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 4, _sourceBuffer);
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 5, _tileBuffer);
        GL.BindBuffer(BufferTarget.ShaderStorageBuffer, _oldGenericBuffer);
        // Measured compute+placed-light time at 1080p/4K favors coarse masks for
        // sparse views. At >=16 sources and >=8 broad candidates/tile, the depth
        // cost is small in overlap views and saves traversal in separated views.
        bool standard = Math.Abs(projection[11] + 1f) < 0.001f && Math.Abs(projection[15]) < 0.001f;
        if (ViewCullingEnabled && count >= 16 && RectangleCandidatesPerTile >= 8 && standard &&
            primaryValid && primary!.DepthTextureId > 0)
        {
            // Singular projections must never reuse the preceding frame's
            // inverse. The coarse shader is the conservative fallback.
            if (_projectionInvertible)
                DepthCulling = _compute.DispatchDepth(api, _rectangles, count, width, width * height,
                    primary!.DepthTextureId, _inverseProjection);
        }
        if (DepthCulling)
        {
            // Native foliage wind and global terrain warping change receiver
            // depth without chunk updates, even though light-space maps are static.
            // Reuse coarse masks, but keep depth rejection current and conservative.
            _coarseMasksValid = false; GeneratedMasksThisFrame = true;
        }
        else if (masksChanged)
        {
            _compute.Dispatch(api, _rectangles, count, width, width * height);
            _coarseMasksValid = true; GeneratedMasksThisFrame = true;
            Array.Copy(_rectangles, _previousRectangles, count * 4);
        }
        PlacedLightGpuProfile.SampleMasks(api, width * height);

        GL.ActiveTexture(TextureUnit.Texture14);
        try
        {
            GL.BindSampler(14, 0);
            GL.BindTexture(TextureTarget.Texture2DArray, shadowTexture);
        }
        finally { GL.ActiveTexture((TextureUnit)oldActive); }
        int tileWidthLocation = _widthLocation;
        if (countLocation >= 0) GL.Uniform1(countLocation, count);
        if (tileWidthLocation >= 0) GL.Uniform1(tileWidthLocation, width);
        if (changed)
        {
            _sourceDataValid = true; _preparedRevision = LightRevision;
            _preparedSaturation = PlsSaturation;
            _preparedCount = count; _preparedTexture = shadowTexture;
            _preparedWidth = frameWidth; _preparedHeight = frameHeight;
            Array.Copy(origin, _preparedOrigin, 16);
        }
        }
        catch
        {
            // Return shared GL bindings before propagating a compile/preparation
            // failure. The caller retains its logged vanilla-light fallback.
            _sourceDataValid = _coarseMasksValid = false;
            PublishedCount = 0; GeneratedMasksThisFrame = DepthCulling = false;
            Disable(deferred);
            throw;
        }
        finally
        {
            GL.UseProgram(previousProgram);
            LastPrepareCpuMilliseconds = System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        }
    }

    internal void Disable(IShaderProgram deferred)
    {
        Release();
        _blend = 0f;
        int previous = GL.GetInteger(GetPName.CurrentProgram);
        GL.UseProgram(deferred.ProgramId);
        Locations(deferred.ProgramId);
        int location = _countLocation;
        if (location >= 0) GL.Uniform1(location, 0);
        location = _mapLocation;
        if (location >= 0) GL.Uniform1(location, 14);
        location = _blendLocation;
        if (location >= 0) GL.Uniform1(location, 0f);
        GL.UseProgram(previous);
    }

    internal void Release()
    {
        if (!_pending) return;
        // BindBufferBase would widen another renderer's borrowed subranges.
        _oldSourceBuffer.Restore();
        _oldTileBuffer.Restore();
        GL.BindBuffer(BufferTarget.ShaderStorageBuffer, _oldGenericBuffer);
        int active = GL.GetInteger(GetPName.ActiveTexture);
        GL.ActiveTexture(TextureUnit.Texture14);
        GL.BindTexture(TextureTarget.Texture2DArray, _oldTexture14);
        GL.BindSampler(14, _oldSampler14);
        GL.ActiveTexture((TextureUnit)active);
        _pending = false;
    }

    public void Dispose()
    {
        Release();
        if (_sourceBuffer != 0) GL.DeleteBuffer(_sourceBuffer);
        if (_tileBuffer != 0) GL.DeleteBuffer(_tileBuffer);
        _sourceBuffer = _tileBuffer = 0;
        _tileBytes = 0;
        _sourceDataValid = _coarseMasksValid = false;
        _preparedView.Reset(); _program = 0;
        _compute.Dispose();
    }
}
