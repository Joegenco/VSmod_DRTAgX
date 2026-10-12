using System;
using System.Collections.Generic;
using HarmonyLib;
using OpenTK.Graphics.OpenGL4;
using Vintagestory.API.Client;
using Vintagestory.API.MathTools;
using Vintagestory.Client.NoObf;

namespace DRTAgX;

/// <summary>Render-thread PLS bindings for wind G-buffer fills and forward terrain.</summary>
internal sealed class PlacedLightTerrainBindings : IDisposable
{
    private static PlacedLightTerrainBindings? _instance;
    private StaticTerrainShadowMaps _maps;
    private readonly StaticLightTileBindings _tiles;
    private readonly Harmony _harmony = new("drtagx.placed.terrain.receiving");
    private readonly Dictionary<int, Locations?> _locations = new();
    private readonly double[] _cameraInverse = new double[16];
    private readonly Vec3d _camera = new();
    internal ICoreClientAPI? Api { get; set; }
    private bool _bound;
    private int _oldGeneric, _oldTexture, _oldSampler;
    private IndexedBufferState _oldSource, _oldCells;
    internal bool Enabled { get; set; }
    internal bool GridEnabled { get; set; } = true;
    internal PlacedLightSurfaceBindings? SurfaceCacheBindings { get; set; }

    private sealed class Locations(int program)
    {
        internal readonly int Count = GL.GetUniformLocation(program, "drtTerrainPlacedCount");
        internal readonly int Maps = GL.GetUniformLocation(program, "drtStaticMaps");
        internal readonly int Grid = GL.GetUniformLocation(program, "drtTerrainPlacedGridEnabled");
        internal readonly int Shift = GL.GetUniformLocation(program, "drtTerrainPlacedSourceShift");
        internal readonly int CellsReady = GL.GetUniformLocation(program, "drtTerrainPlacedCellsReady");
        internal readonly int CellsMinimum = GL.GetUniformLocation(program, "drtTerrainPlacedCellsMin");
    }

    internal PlacedLightTerrainBindings(StaticTerrainShadowMaps maps, StaticLightTileBindings tiles)
    {
        _maps = maps;
        _tiles = tiles;
        var use = AccessTools.Method(typeof(ShaderProgramBase), nameof(IShaderProgram.Use), Type.EmptyTypes);
        var stop = AccessTools.Method(typeof(ShaderProgramBase), nameof(IShaderProgram.Stop), Type.EmptyTypes);
        if (use == null || stop == null) throw new MissingMethodException("ShaderProgramBase.Use/Stop");
        _instance = this;
        _harmony.Patch(use, postfix: new HarmonyMethod(typeof(PlacedLightTerrainBindings), nameof(AfterUse)));
        _harmony.Patch(stop, prefix: new HarmonyMethod(typeof(PlacedLightTerrainBindings), nameof(BeforeStop)));
    }

    private static void AfterUse(ShaderProgramBase __instance) => _instance?.Bind(__instance.ProgramId);

    private static void BeforeStop() => _instance?.Restore();

    internal void PublishMaps(StaticTerrainShadowMaps maps)
    {
        if (ReferenceEquals(_maps, maps)) return;
        Restore(); // End any borrow before changing the owner supplying forward receivers.
        _maps = maps;
    }

    private void Bind(int program)
    {
        // Native switches may omit Stop. Never nest owned array/SSBO bindings.
        Restore();
        SurfaceCacheBindings?.Bind(program);
        if (!_locations.TryGetValue(program, out var locations))
        {
            var found = new Locations(program);
            locations = found.Grid >= 0 || (found.Count >= 0 && found.Maps >= 0) ? found : null;
            _locations.Add(program, locations);
        }

        if (locations == null) return; // The receiving include exists only in terrain shaders.
        // Memo-only readonly receivers retain this policy while their obsolete
        // source-count and depth-map uniforms can be optimized out entirely.
        if (locations.Grid >= 0) GL.Uniform1(locations.Grid, GridEnabled ? 1 : 0);
        if (locations.Count < 0 || locations.Maps < 0) return;
        GL.Uniform1(locations.Maps, 14); // Reassert after native automatic sampler assignment.
        // Legacy receivers use source directions and the completed terrain depth.
        bool ready = Enabled && _maps.Ready &&
                     _maps.TextureId != 0 && _tiles.SourceBuffer != 0 && _tiles.PublishedCount > 0;
        GL.Uniform1(locations.Count, ready ? _tiles.PublishedCount : 0);
        bool cells = ready && _tiles.SourceGrid.Valid && _tiles.SourceGrid.Buffer != 0;
        if (locations.CellsReady >= 0) GL.Uniform1(locations.CellsReady, cells ? 1 : 0);
        if (cells && locations.CellsMinimum >= 0)
            GL.Uniform3(locations.CellsMinimum, _tiles.SourceGrid.MinimumX, _tiles.SourceGrid.MinimumY,
                _tiles.SourceGrid.MinimumZ);
        if (!ready) return;
        if (locations.Shift >= 0)
        {
            // Source records can precede this draw's camera. Subtract doubles
            // first; never upload absolute world positions as float uniforms.
            float x = 0, y = 0, z = 0;
            if (Api?.World is ClientMain world)
            {
                PlacedLightView.CameraFromView(world.CurrentModelViewMatrixd, _cameraInverse, _camera);
                x = (float)(_tiles.SourceCamera.X - _camera.X);
                y = (float)(_tiles.SourceCamera.Y - _camera.Y);
                z = (float)(_tiles.SourceCamera.Z - _camera.Z);
            }

            GL.Uniform3(locations.Shift, x, y, z);
        }

        _oldGeneric = GL.GetInteger(GetPName.ShaderStorageBufferBinding);
        _oldSource = IndexedBufferState.Capture(BufferRangeTarget.ShaderStorageBuffer, 4);
        _oldCells = IndexedBufferState.Capture(BufferRangeTarget.ShaderStorageBuffer, 5);
        int active = GL.GetInteger(GetPName.ActiveTexture);
        GL.ActiveTexture(TextureUnit.Texture14);
        _oldTexture = GL.GetInteger(GetPName.TextureBinding2DArray);
        _oldSampler = GL.GetInteger(GetPName.SamplerBinding);
        _bound = true;
        // Forward/OIT receivers use world-cell candidates with an all-record
        // fallback. Their depth/rest pose can differ from the opaque pixel.
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 4, _tiles.SourceBuffer);
        // Binding 5 is borrowed only during forward draws. Deferred relighting
        // continues using its separate screen/depth tile masks at this binding.
        if (cells) GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 5, _tiles.SourceGrid.Buffer);
        GL.BindSampler(14, 0);
        GL.BindTexture(TextureTarget.Texture2DArray, _maps.TextureId);
        GL.ActiveTexture((TextureUnit)active);
    }

    internal void Restore()
    {
        if (!_bound)
        {
            SurfaceCacheBindings?.Restore();
            return;
        }

        int active = GL.GetInteger(GetPName.ActiveTexture);
        GL.ActiveTexture(TextureUnit.Texture14);
        GL.BindTexture(TextureTarget.Texture2DArray, _oldTexture);
        GL.BindSampler(14, _oldSampler);
        GL.ActiveTexture((TextureUnit)active);
        _oldSource.Restore();
        _oldCells.Restore();
        GL.BindBuffer(BufferTarget.ShaderStorageBuffer, _oldGeneric);
        _bound = false;
        // Legacy map14 was borrowed after the memo scope. Unwind in reverse
        // order so two users of the same sampler cannot restore a stale binding.
        SurfaceCacheBindings?.Restore();
    }

    internal void Reload()
    {
        Restore();
        _locations.Clear();
    }

    public void Dispose()
    {
        Restore();
        _harmony.UnpatchAll(_harmony.Id);
        _locations.Clear();
        if (_instance == this) _instance = null;
    }
}