using System;
using OpenTK.Graphics.OpenGL4;
using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace DRTAgX;

/// <summary>Point-only primitive gate over the actual expanded native caster
/// shaders. Native sun shadows retain their original vertex/fragment program.</summary>
internal sealed class PointTerrainShadowProgram(string name) : IDisposable
{
    private IShaderProgram? _program;
    private int _nativeId;
    private ICoreClientAPI? _sourceApi;
    private string? _compiledGeometry;

    private bool _failed;

    // Native global deformation remains supported for stationary geometry.
    // Locations and scratch values are retained; warm calls allocate nothing.
    private static readonly string[] FloatNames =
    [
        "subpixelPaddingX", "subpixelPaddingY",
        "globalWarpIntensity", "glitchWaviness", "windWaveCounter", "timeCounter"
    ];

    private readonly int[] _nativeFloats = new int[FloatNames.Length], _pointFloats = new int[FloatNames.Length];
    private readonly float[] _vector = new float[3];
    private int _nativePlayer, _pointPlayer;

    internal IShaderProgram? Get(ICoreClientAPI api, IShaderProgram native)
    {
        // Called only on the render thread inside the caller's GL state guard.
        if (_nativeId != native.ProgramId || _program?.Disposed == true)
        {
            DisposeProgram();
            _nativeId = native.ProgramId;
            _failed = false;
            try
            {
                var program = api.Shader.NewShaderProgram();
                program.VertexShader = Copy(api, native.VertexShader, EnumShaderType.VertexShader, true);
                program.FragmentShader = Copy(api, native.FragmentShader, EnumShaderType.FragmentShader, false);
                program.GeometryShader = api.Shader.NewShader(EnumShaderType.GeometryShader);
                program.GeometryShader.Code =
                    PointVersion(api.Assets.Get(new AssetLocation("drtagx", "shaders/point_shadowmap.gsh")).ToText());
                api.Shader.RegisterMemoryShaderProgram(name, program);
                if (!program.Compile())
                {
                    program.Dispose();
                    throw new InvalidOperationException("Point caster program failed to compile");
                }

                _program = program;
                // Keep the actual successful caster policy after program reload:
                // retained depth may have been baked before Assets was refreshed.
                _sourceApi = api;
                _compiledGeometry = program.GeometryShader.Code;
                for (int i = 0; i < FloatNames.Length; ++i)
                {
                    _nativeFloats[i] = GL.GetUniformLocation(native.ProgramId, FloatNames[i]);
                    _pointFloats[i] = GL.GetUniformLocation(program.ProgramId, FloatNames[i]);
                }

                _nativePlayer = GL.GetUniformLocation(native.ProgramId, "playerpos");
                _pointPlayer = GL.GetUniformLocation(program.ProgramId, "playerpos");
            }
            catch (Exception ex)
            {
                _failed = true;
                api.Logger.Warning("[DRTAgX] Point caster exclusion unavailable: " + ex.Message);
            }
        }

        if (_failed || _program == null) return null;
        for (int i = 0; i < FloatNames.Length; ++i)
        {
            if (_nativeFloats[i] < 0 || _pointFloats[i] < 0) continue;
            GL.GetUniform(native.ProgramId, _nativeFloats[i], out float value);
            GL.ProgramUniform1(_program.ProgramId, _pointFloats[i], value);
        }

        if (_nativePlayer >= 0 && _pointPlayer >= 0)
        {
            GL.GetUniform(native.ProgramId, _nativePlayer, _vector);
            GL.ProgramUniform3(_program.ProgramId, _pointPlayer, _vector[0], _vector[1], _vector[2]);
        }

        return _program;
    }

    private static IShader Copy(ICoreClientAPI api, IShader source, EnumShaderType type, bool pointVertex)
    {
        var shader = api.Shader.NewShader(type);
        // Native file loading promotes USESSBO terrain from330 to430. Memory
        // programs do not pass that loader boundary; IShader has no version
        // property, so preserve the complete source but explicitly promote its
        // version directive. Extensions and native prefix remain untouched.
        shader.Code = PointVersion(source.Code);
        shader.PrefixCode =
            (source.PrefixCode ?? string.Empty) + (pointVertex ? "\n#define DRT_POINT_SHADOWS 1\n" : "");
        return shader;
    }

    private static string PointVersion(string code)
    {
        int start = code.IndexOf("#version ", StringComparison.Ordinal);
        if (start < 0) return "#version 430 core\n" + code;
        int digits = start + 9, end = digits;
        while (end < code.Length && char.IsDigit(code[end])) ++end;
        if (!int.TryParse(code.AsSpan(digits, end - digits), out int version))
            throw new InvalidOperationException("Invalid native point-caster GLSL version");
        return version >= 430 ? code : code[..digits] + "430" + code[end..];
    }

    internal void Reload()
    {
        DisposeProgram();
        _nativeId = 0;
        _failed = false;
    }

    internal bool GeometrySourceUnchanged()
    {
        if (_compiledGeometry == null) return true; // No successful caster could have produced retained depth.
        try
        {
            return _sourceApi != null && string.Equals(_compiledGeometry,
                PointVersion(_sourceApi.Assets.Get(new AssetLocation("drtagx", "shaders/point_shadowmap.gsh"))
                    .ToText()),
                StringComparison.Ordinal);
        }
        catch
        {
            return false;
        } // Unknown current policy cannot certify old completed depth.
    }

    private void DisposeProgram()
    {
        _program?.Dispose();
        _program = null;
    }

    public void Dispose()
    {
        DisposeProgram();
        _sourceApi = null;
        _compiledGeometry = null;
    }
}