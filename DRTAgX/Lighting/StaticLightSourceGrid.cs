using System;
using System.Collections.Generic;
using OpenTK.Graphics.OpenGL4;
using Vintagestory.API.Client;
using Vintagestory.API.MathTools;

namespace DRTAgX;

/// <summary>Render-thread spatial masks for forward and undeformed terrain receivers.</summary>
internal sealed class StaticLightSourceGrid : IDisposable
{
    internal const int Dimension = 32, CellSize = 8, MaskWords = 5;
    // 129 source bits include the cache replacement's staging record.
    internal const int WordCount = Dimension * Dimension * Dimension * MaskWords;
    private const double Padding = 0.02;
    private readonly uint[] _masks = new uint[WordCount];
    private readonly int[] _sources = new int[StaticTerrainShadowMaps.MaxPublishedSources * 4];
    private int _count = -1;
    private double _minimumX, _minimumY, _minimumZ;
    private bool _failed;
    internal int Buffer { get; private set; }
    internal bool Valid { get; private set; }
    internal float MinimumX { get; private set; }
    internal float MinimumY { get; private set; }
    internal float MinimumZ { get; private set; }
    internal bool UploadedThisFrame { get; private set; }

    internal void Reset() { Valid = false; _failed = false; _count = -1; }

    internal void Prepare(ICoreClientAPI api, IReadOnlyList<StaticTerrainShadowMaps.ActiveLight> lights,
        int count, Vec3d sourceCamera)
    {
        UploadedThisFrame = false;
        if (_failed) return;
        if (!double.IsFinite(sourceCamera.X) || !double.IsFinite(sourceCamera.Y) || !double.IsFinite(sourceCamera.Z))
        { Valid = false; return; }
        // World-aligned cells stay fixed during rotation and sub-cell camera motion.
        // Subtract doubles before uploading the small camera-relative minimum.
        double x = Math.Floor(sourceCamera.X / CellSize) * CellSize - Dimension * CellSize / 2,
            y = Math.Floor(sourceCamera.Y / CellSize) * CellSize - Dimension * CellSize / 2,
            z = Math.Floor(sourceCamera.Z / CellSize) * CellSize - Dimension * CellSize / 2;
        MinimumX = (float)(x - sourceCamera.X); MinimumY = (float)(y - sourceCamera.Y); MinimumZ = (float)(z - sourceCamera.Z);
        bool changed = !Valid || count != _count || x != _minimumX || y != _minimumY || z != _minimumZ;
        for (int i = 0; i < count && !changed; ++i)
        {
            var source = lights[i].Source; int offset = i * 4;
            changed = _sources[offset] != source.X || _sources[offset + 1] != source.Y ||
                _sources[offset + 2] != source.Z || _sources[offset + 3] != source.Level;
        }
        if (!changed) return;
        Valid = false;
        try
        {
            Build(lights, count, x, y, z);
            int previous = GL.GetInteger(GetPName.ShaderStorageBufferBinding);
            try
            {
                if (Buffer == 0) Buffer = GL.GenBuffer();
                GL.BindBuffer(BufferTarget.ShaderStorageBuffer, Buffer);
                GL.BufferData(BufferTarget.ShaderStorageBuffer, WordCount * sizeof(uint), _masks, BufferUsageHint.DynamicDraw);
            }
            finally { GL.BindBuffer(BufferTarget.ShaderStorageBuffer, previous); }
            _minimumX = x; _minimumY = y; _minimumZ = z; _count = count;
            for (int i = 0; i < count; ++i)
            {
                var source = lights[i].Source; int offset = i * 4;
                _sources[offset] = source.X; _sources[offset + 1] = source.Y;
                _sources[offset + 2] = source.Z; _sources[offset + 3] = source.Level;
            }
            Valid = true; UploadedThisFrame = true;
        }
        catch (Exception ex)
        {
            // The optional lookup never disables existing PLS. Until reload,
            // forward programs retain their original all-record traversal.
            _failed = true;
            api.Logger.Warning("[DRT AgX] Forward placed-light cells unavailable; retaining source traversal: {0}", ex.Message);
        }
    }

    private void Build(IReadOnlyList<StaticTerrainShadowMaps.ActiveLight> lights, int count,
        double minimumX, double minimumY, double minimumZ)
    {
        Array.Clear(_masks);
        for (int i = 0; i < count; ++i)
        {
            var source = lights[i].Source;
            double x = source.X + 0.5 - minimumX,
                y = source.Y + StaticTerrainShadowMaps.SourceHeight - minimumY,
                z = source.Z + 0.5 - minimumZ,
                radius = PlacedLightView.Reach(source) + Padding;
            if (radius <= Padding) continue;
            int firstX = Math.Max(0, (int)Math.Floor((x - radius) / CellSize)), lastX = Math.Min(Dimension - 1, (int)Math.Floor((x + radius) / CellSize)),
                firstY = Math.Max(0, (int)Math.Floor((y - radius) / CellSize)), lastY = Math.Min(Dimension - 1, (int)Math.Floor((y + radius) / CellSize)),
                firstZ = Math.Max(0, (int)Math.Floor((z - radius) / CellSize)), lastZ = Math.Min(Dimension - 1, (int)Math.Floor((z + radius) / CellSize));
            for (int cz = firstZ; cz <= lastZ; ++cz)
            for (int cy = firstY; cy <= lastY; ++cy)
            for (int cx = firstX; cx <= lastX; ++cx)
            {
                // Sphere/AABB overlap: clamp the emitter to the cell, then
                // compare nearest squared distance with the padded light radius.
                double dx = Math.Max(Math.Max(cx * CellSize - x, x - (cx + 1) * CellSize), 0),
                    dy = Math.Max(Math.Max(cy * CellSize - y, y - (cy + 1) * CellSize), 0),
                    dz = Math.Max(Math.Max(cz * CellSize - z, z - (cz + 1) * CellSize), 0);
                if (dx * dx + dy * dy + dz * dz <= radius * radius)
                    _masks[((cz * Dimension + cy) * Dimension + cx) * MaskWords + (i >> 5)] |= 1u << (i & 31);
            }
        }
    }

    public void Dispose()
    {
        if (Buffer != 0) GL.DeleteBuffer(Buffer);
        Buffer = 0; Reset();
    }
}
