using System;
using System.Collections.Generic;
using OpenTK.Graphics.OpenGL4;
using Vintagestory.API.Client;
using Vintagestory.API.MathTools;

namespace DRTAgX;

/// <summary>Render-thread publication of live world light scales; never changes gameplay tables.</summary>
internal sealed class SurfaceLightBindings
{
    private static readonly EnumShaderProgram[] SurfacePrograms =
    [
        EnumShaderProgram.Chunkopaque, EnumShaderProgram.Chunktopsoil,
        EnumShaderProgram.Chunktransparent, EnumShaderProgram.Chunkliquid,
        EnumShaderProgram.Standard, EnumShaderProgram.Entityanimated,
        EnumShaderProgram.Entityanimated_Oit, EnumShaderProgram.Particlesquad,
        EnumShaderProgram.Particlescube
    ];

    private readonly Dictionary<int, Locations> _locations = new();
    private readonly float[] _calibration = new float[64];
    private readonly float[] _blockValues = new float[32];
    private readonly float[] _sky = new float[3];
    private readonly float[] _sunGridCameraPhase = new float[3];
    private object? _world, _sunTable, _blockTable;
    private int _sunBrightness = -1, _generation;
    private int _blockLength = -1;
    private float _sunZero, _sunFull = 1f, _nightVision;
    internal int Generation => _generation;
    internal int BlockCalibrationGeneration { get; private set; }

    private sealed class Locations(int program)
    {
        internal readonly int Bounds = GL.GetUniformLocation(program, "drtSunlightBounds");
        internal readonly int Calibration = GL.GetUniformLocation(program, "drtPlacedCalibration[0]");
        internal readonly int NativeSky = GL.GetUniformLocation(program, "rgbaAmbientIn");
        internal readonly int Sky = GL.GetUniformLocation(program, "drtSkyColor");
        internal readonly int SunGrid = GL.GetUniformLocation(program, "drtSunGridCameraPhase");
        internal readonly int NightVision = GL.GetUniformLocation(program, "drtNightVisionStrength");
        internal float PublishedNightVision = float.NaN;
        internal int Generation = -1;
    }

    internal void Reset()
    {
        _locations.Clear(); // Program IDs may be reused after native shader reload.
        _world = _sunTable = _blockTable = null;
        _sunBrightness = -1;
        _nightVision = 0f;
        Array.Clear(_sky);
        Array.Clear(_sunGridCameraPhase);
    }

    private static float Sample(float[] table, float level)
    {
        if (table.Length == 0) return 0f;
        float q = Math.Clamp(level, 0f, table.Length - 1);
        int lo = (int)q, hi = Math.Min(lo + 1, table.Length - 1);
        float value = table[lo] + (table[hi] - table[lo]) * (q - lo);
        return float.IsFinite(value) ? Math.Max(0f, value) : 0f;
    }

    // Outside source scheduling: precompute amplitudes once per world/table/reload.
    internal void Refresh(ICoreClientAPI api)
    {
        // The public native effect strength changes independently of light
        // tables. Refresh its uniforms without invalidating any cached PLS.
        var strength = api.Render.ShaderUniforms.NightVisionStrength;
        var nextNightVision = float.IsFinite(strength) ? Math.Clamp(strength, 0f, 1f) : 0f;
        _nightVision = nextNightVision;

        var world = api.World;
        if (world == null) return;
        float[] sun = world.SunLightLevels, block = world.BlockLightLevels;
        int brightness = world.SunBrightness;
        bool blockChanged = !ReferenceEquals(_world, world) || !ReferenceEquals(_blockTable, block) ||
                            _blockLength != block.Length;
        // Native tables may be edited in place. Only samples0..31 enter placed
        // calibration, so compare that small reusable snapshot without allocation.
        for (int i = 0; i < Math.Min(32, block.Length) && !blockChanged; ++i)
            blockChanged = !_blockValues[i].Equals(block[i]);
        // Other mods may edit the native sunlight table in place too. Only
        // these two endpoints enter the shader normalization, so compare their
        // sanitized values without scanning the whole table or allocating.
        var sunZero = Sample(sun, 0f);
        var sunFull = Math.Max(sunZero + 1e-6f, Sample(sun, brightness));
        if (!blockChanged && ReferenceEquals(_world, world) && ReferenceEquals(_sunTable, sun) &&
            ReferenceEquals(_blockTable, block) && _sunBrightness == brightness &&
            _sunZero.Equals(sunZero) && _sunFull.Equals(sunFull)) return;
        _world = world;
        _sunTable = sun;
        _blockTable = block;
        _sunBrightness = brightness;
        _sunZero = sunZero;
        _sunFull = sunFull;
        if (blockChanged)
        {
            Calibrate(block, _calibration);
            Array.Copy(block, _blockValues, Math.Min(32, block.Length));
            _blockLength = block.Length;
            ++BlockCalibrationGeneration;
        }

        // Shared surface uniform publication also tracks sunlight. The memo
        // consumes only BlockCalibrationGeneration, excluding time-of-day changes.
        ++_generation;
    }

    internal static void Calibrate(float[] block, float[] amplitudes)
    {
        Array.Clear(amplitudes);
        for (int level = 1; level < 32; ++level)
        {
            float radius = Math.Min(1.4f * level, 22f);
            float referenceDistance = Math.Min(5.5f, radius * 0.5f);
            float falloff = (1f - referenceDistance / radius) /
                            (1f + 0.25f * referenceDistance * referenceDistance);
            amplitudes[level * 2] = 0.5f * Sample(block, level - referenceDistance) / falloff;
            amplitudes[level * 2 + 1] = 0.5f * Sample(block, level);
        }
    }

    private Locations Get(int program)
    {
        if (!_locations.TryGetValue(program, out Locations? result))
            _locations.Add(program, result = new Locations(program));
        return result;
    }

    internal void BindSurfaces(ICoreClientAPI api)
    {
        Refresh(api);
        foreach (EnumShaderProgram kind in SurfacePrograms) Bind(api.Render.GetEngineShader(kind));
    }

    internal void Bind(IShaderProgram? shader)
    {
        if (shader == null || shader.Disposed || shader.LoadError || shader.ProgramId <= 0) return;
        Locations locations = Get(shader.ProgramId);
        var lightingChanged = locations.Generation != _generation;
        var nightVisionChanged = locations.NightVision >= 0 && !locations.PublishedNightVision.Equals(_nightVision);
        if (!lightingChanged && !nightVisionChanged) return;
        // Raw GL binding keeps engine shader tracking untouched; restore actual GL state too.
        int previous = GL.GetInteger(GetPName.CurrentProgram);
        try
        {
            GL.UseProgram(shader.ProgramId);
            if (lightingChanged)
            {
                if (locations.Bounds >= 0) GL.Uniform2(locations.Bounds, _sunZero, _sunFull);
                if (locations.Calibration >= 0) GL.Uniform2(locations.Calibration, 32, _calibration);
                locations.Generation = _generation;
            }

            if (!nightVisionChanged) return;
            GL.Uniform1(locations.NightVision, _nightVision);
            locations.PublishedNightVision = _nightVision;
        }
        finally
        {
            GL.UseProgram(previous);
        }
    }

    internal void PublishSky(IShaderProgram? native, IShaderProgram deferred)
    {
        Bind(deferred);
        Array.Clear(_sky); // Missing native sky must never retain a previous world's daylight.
        if (native != null && !native.Disposed && !native.LoadError && native.ProgramId > 0)
        {
            int source = Get(native.ProgramId).NativeSky;
            if (source >= 0) GL.GetUniform(native.ProgramId, source, _sky);
        }

        for (int i = 0; i < 3; ++i)
            if (!float.IsFinite(_sky[i]) || _sky[i] < 0f)
                _sky[i] = 0f;
        Locations target = Get(deferred.ProgramId);
        int previous = GL.GetInteger(GetPName.CurrentProgram);
        try
        {
            GL.UseProgram(deferred.ProgramId);
            if (target.Sky >= 0) GL.Uniform3(target.Sky, _sky[0], _sky[1], _sky[2]);
            if (target.SunGrid >= 0)
                GL.Uniform3(target.SunGrid,
                    _sunGridCameraPhase[0], _sunGridCameraPhase[1], _sunGridCameraPhase[2]);
        }
        finally
        {
            GL.UseProgram(previous);
        }
    }

    internal void SetSunGridCamera(Vec3d camera)
    {
        // Subtract whole blocks in double precision. The fragment then adds
        // only this small phase to its camera-relative position, even far from spawn.
        _sunGridCameraPhase[0] = (float)(camera.X - Math.Floor(camera.X));
        _sunGridCameraPhase[1] = (float)(camera.Y - Math.Floor(camera.Y));
        _sunGridCameraPhase[2] = (float)(camera.Z - Math.Floor(camera.Z));
    }
}