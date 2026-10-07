using System;
using System.Collections.Generic;
using HarmonyLib;
using OpenTK.Graphics.OpenGL4;
using Vintagestory.API.Client;
using Vintagestory.Client.NoObf;

namespace DRTAgX;

/// <summary>Cached native Use/Stop bindings and a lifetime no-dither policy.</summary>
internal sealed class AtmosphereProgramBindings : IDisposable
{
    internal const int BufferBinding = 10; // Native animation owns UBO 0.
    private static AtmosphereProgramBindings? _instance;
    private readonly Dictionary<int, Binding?> _programs = new();
    private readonly Harmony _harmony = new("drtagx.atmosphere.bindings");
    private readonly ICoreClientAPI _api;
    private readonly AtmosphereSkyResources _sky;
    private readonly int _buffer;
    private readonly bool _previousDither;
    private int _boundProgram, _previousGenericUbo;
    private IndexedBufferState _previousUbo;
    private bool _skyBlendLogged;
    internal int Volume { get; set; }

    private sealed class Binding
    {
        internal readonly int[] Locations = new int[4], Units = new int[4];
        internal readonly int[] Textures = new int[4], Samplers = new int[4];
        internal int Count;
        internal int Quality = -2, QualityGeneration = -1;
    }

    internal AtmosphereProgramBindings(ICoreClientAPI api, AtmosphereSkyResources sky, int buffer)
    {
        _api = api; _sky = sky; _buffer = buffer;
        // Render-thread lifetime policy: disable fixed-function quantization noise
        // for every native shader, including programs without the atmosphere UBO.
        // Restore the incoming capability only when this owner is disposed.
        _previousDither = GL.IsEnabled(EnableCap.Dither);
        GL.Disable(EnableCap.Dither);
        _instance = this;
        // Public native shader ABI, checked at startup. Owned compute uses raw GL
        // and bypasses these callbacks, preserving engine shader tracking.
        var use = AccessTools.Method(typeof(ShaderProgramBase), nameof(IShaderProgram.Use), Type.EmptyTypes);
        var stop = AccessTools.Method(typeof(ShaderProgramBase), nameof(IShaderProgram.Stop), Type.EmptyTypes);
        if (use == null || stop == null) throw new MissingMethodException("ShaderProgramBase.Use/Stop");
        _harmony.Patch(use, postfix: new HarmonyMethod(typeof(AtmosphereProgramBindings), nameof(AfterUse)));
        _harmony.Patch(stop, prefix: new HarmonyMethod(typeof(AtmosphereProgramBindings), nameof(BeforeStop)));
        // Probe actual mesh draw state once; inspecting shader Use alone would
        // miss blend changes made between shader activation and mesh submission.
        // A startup diagnostic must not add a Harmony dispatch to every mesh
        // submission in ordinary gameplay. Developer sessions retain the probe.
        if (ClientSettings.DeveloperMode)
            foreach (var method in typeof(ClientPlatformWindows).GetMethods())
                if (method.Name == "RenderMesh") _harmony.Patch(method,
                    prefix: new HarmonyMethod(typeof(AtmosphereProgramBindings), nameof(BeforeMesh)));
    }

    private Binding? Get(int program)
    {
        if (_programs.TryGetValue(program, out Binding? cached)) return cached;
        int block = GL.GetUniformBlockIndex(program, "DrtAtmosphere");
        if (block < 0) { _programs.Add(program, null); return null; }
        GL.UniformBlockBinding(program, block, BufferBinding);
        var binding = new Binding();
        string[] names = ["drtSkyViewPrevious", "drtSkyViewCurrent", "drtFogVolume", "liquidDepth"];
        // Sampler indices span GL_MAX_COMBINED_TEXTURE_IMAGE_UNITS, not just
        // 0..15. ChunkLOD's automatic assignments can exhaust our old 10..13
        // pool even though the context has many unused units above fifteen.
        // Reserve native 0..9 and point-shadow 14/15, including later setters.
        var occupied = new bool[GL.GetInteger(GetPName.MaxCombinedTextureImageUnits)];
        for (int unit = 0; unit < Math.Min(10, occupied.Length); ++unit) occupied[unit] = true;
        if (occupied.Length > 14) occupied[14] = true;
        if (occupied.Length > 15) occupied[15] = true;
        GL.GetProgram(program, GetProgramParameterName.ActiveUniforms, out int count);
        for (int i = 0; i < count; ++i)
        {
            string name = GL.GetActiveUniform(program, i, out int size, out ActiveUniformType type);
            if (name.StartsWith("drtSkyView", StringComparison.Ordinal) || name == "drtFogVolume" || name == "liquidDepth") continue;
            if (!type.ToString().Contains("Sampler", StringComparison.Ordinal)) continue;
            // Sampler arrays may use nonconsecutive units; inspect each actual
            // assignment rather than assuming element j uses firstUnit+j.
            string arrayName = name.EndsWith("[0]", StringComparison.Ordinal) ? name[..^3] : name;
            for (int j = 0; j < size; ++j)
            {
                int location = GL.GetUniformLocation(program, size == 1 ? name : $"{arrayName}[{j}]");
                if (location < 0) continue;
                GL.GetUniform(program, location, out int unit);
                if (unit >= 0 && unit < occupied.Length) occupied[unit] = true;
            }
        }
        for (int i = 0; i < names.Length; ++i)
        {
            binding.Locations[i] = GL.GetUniformLocation(program, names[i]);
            if (binding.Locations[i] < 0) { binding.Units[i] = -1; continue; }
            int unit = 13;
            while (unit >= 10 && (unit >= occupied.Length || occupied[unit])) --unit;
            if (unit < 10)
            {
                unit = 16;
                while (unit < occupied.Length && occupied[unit]) ++unit;
                if (unit >= occupied.Length) throw new InvalidOperationException($"No free atmosphere texture units in program {program}");
            }
            binding.Units[i] = unit;
            occupied[unit] = true;
            GL.Uniform1(binding.Locations[i], unit);
            ++binding.Count;
        }
        _programs.Add(program, binding);
        return binding;
    }

    private static void AfterUse(ShaderProgramBase __instance) => _instance?.Bind(__instance.ProgramId);
    private static void BeforeStop() => _instance?.Restore();
    private static void BeforeMesh()
    {
        var instance = _instance;
        if (instance == null || instance._skyBlendLogged) return;
        var sky = instance._api.Render.GetEngineShader(EnumShaderProgram.Sky);
        if (sky == null || sky.ProgramId != instance._boundProgram) return;
        instance._skyBlendLogged = true;
        instance._api.Logger.Notification($"[DRT AgX] Native sky draw blend: enabled={GL.IsEnabled(EnableCap.Blend)}, " +
            $"RGB={GL.GetInteger(GetPName.BlendSrcRgb)}/{GL.GetInteger(GetPName.BlendDstRgb)}, " +
            $"alpha={GL.GetInteger(GetPName.BlendSrcAlpha)}/{GL.GetInteger(GetPName.BlendDstAlpha)}.");
    }

    private void Bind(int program)
    {
        Restore(); // Native shader switches may omit Stop; never nest owned state.
        GL.Disable(EnableCap.Dither);
        Binding? binding = Get(program);
        if (binding == null) return;
        if (binding.Quality == -2) binding.Quality = GL.GetUniformLocation(program, "drtPerformanceMode");
        if (binding.Quality >= 0 && binding.QualityGeneration != FrameQuality.Generation)
        {
            GL.Uniform1(binding.Quality, FrameQuality.Current.Performance ? 1 : 0);
            binding.QualityGeneration = FrameQuality.Generation;
        }
        _previousGenericUbo = GL.GetInteger(GetPName.UniformBufferBinding);
        // Other mods can bind a subrange at this index; preserve its full extent.
        _previousUbo = IndexedBufferState.Capture(BufferRangeTarget.UniformBuffer, BufferBinding);
        GL.BindBufferBase(BufferRangeTarget.UniformBuffer, BufferBinding, _buffer);
        _boundProgram = program;
        int active = GL.GetInteger(GetPName.ActiveTexture);
        var buffers = _api.Render.FrameBuffers;
        int liquid = buffers != null && buffers.Count > 5 && buffers[5] != null && !buffers[5].Disposed
            ? buffers[5].DepthTextureId : 0;
        for (int i = 0; i < 4; ++i)
        {
            int unit = binding.Units[i];
            if (unit < 0) continue;
            GL.ActiveTexture(TextureUnit.Texture0 + unit);
            binding.Textures[i] = GL.GetInteger(i == 2 ? GetPName.TextureBinding3D : GetPName.TextureBinding2D);
            binding.Samplers[i] = GL.GetInteger(GetPName.SamplerBinding);
            GL.BindSampler(unit, 0);
            // Native Use may reassign automatic sampler units. Reassert owned
            // assignments, including liquid depth, after each native Use.
            GL.Uniform1(binding.Locations[i], unit);
            GL.BindTexture(i == 2 ? TextureTarget.Texture3D : TextureTarget.Texture2D,
                i == 0 ? _sky.Previous : i == 1 ? _sky.Current : i == 2 ? Volume : liquid);
        }
        GL.ActiveTexture((TextureUnit)active);
    }

    internal void Restore()
    {
        if (_boundProgram == 0) return;
        Binding binding = _programs[_boundProgram]!;
        int active = GL.GetInteger(GetPName.ActiveTexture);
        for (int i = 0; i < 4; ++i)
        {
            int unit = binding.Units[i];
            if (unit < 0) continue;
            GL.ActiveTexture(TextureUnit.Texture0 + unit);
            GL.BindTexture(i == 2 ? TextureTarget.Texture3D : TextureTarget.Texture2D, binding.Textures[i]);
            GL.BindSampler(unit, binding.Samplers[i]);
        }
        GL.ActiveTexture((TextureUnit)active);
        _previousUbo.Restore();
        GL.BindBuffer(BufferTarget.UniformBuffer, _previousGenericUbo);
        _boundProgram = 0;
    }

    internal void Reload() { Restore(); _programs.Clear(); _skyBlendLogged = false; }
    public void Dispose()
    {
        Restore();
        _harmony.UnpatchAll(_harmony.Id);
        if (_previousDither) GL.Enable(EnableCap.Dither);
        else GL.Disable(EnableCap.Dither);
        _programs.Clear();
        if (_instance == this) _instance = null;
    }
}
