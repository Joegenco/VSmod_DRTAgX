using System;
using OpenTK.Graphics.OpenGL4;
using Vintagestory.API.Client;

namespace DRTAgX;

// Per-program locations and fixed upload buffers. Shader reload invalidates
// locations even when the driver recycles a program ID.
internal sealed class MovingLightUniforms
{
    private int _native, _deferred, _quantity, _count, _projection, _positions, _colors, _radiance;
    private readonly int[] _nativePositions = new int[16], _nativeColors = new int[16];
    private readonly float[] _positionData = new float[48], _colorData = new float[48], _prepared = new float[64];

    internal void Reload()
    {
        _native = _deferred = 0;
    }

    internal int ReadNative(IShaderProgram shader, float[][] positions, float[][] colors)
    {
        if (_native != shader.ProgramId)
        {
            _native = shader.ProgramId;
            _quantity = GL.GetUniformLocation(_native, "pointLightQuantity");
            for (int i = 0; i < 16; ++i)
            {
                _nativePositions[i] = GL.GetUniformLocation(_native, $"pointLights[{i}]");
                _nativeColors[i] = GL.GetUniformLocation(_native, $"pointLightColors[{i}]");
            }
        }

        if (_quantity < 0) return 0;
        GL.GetUniform(_native, _quantity, out int count);
        count = Math.Clamp(count, 0, 16);
        for (int i = 0; i < count; ++i)
        {
            if (_nativePositions[i] < 0 || _nativeColors[i] < 0) return i;
            GL.GetUniform(_native, _nativePositions[i], positions[i]);
            GL.GetUniform(_native, _nativeColors[i], colors[i]);
        }

        return count;
    }

    internal void Publish(IShaderProgram shader, int count, float[][] positions, float[][] colors, float[] projection)
    {
        if (_deferred != shader.ProgramId)
        {
            _deferred = shader.ProgramId;
            _count = GL.GetUniformLocation(_deferred, "drtPointCount");
            _projection = GL.GetUniformLocation(_deferred, "drtProjectionMatrix");
            _positions = GL.GetUniformLocation(_deferred, "drtPointPos[0]");
            _colors = GL.GetUniformLocation(_deferred, "drtPointColor[0]");
            _radiance = GL.GetUniformLocation(_deferred, "drtPointRadiance[0]");
        }

        for (int i = 0; i < count; ++i)
        {
            Array.Copy(positions[i], 0, _positionData, i * 3, 3);
            Array.Copy(colors[i], 0, _colorData, i * 3, 3);
            float[] c = colors[i];
            PrepareRadiance(c, _prepared, i * 4);
            // Lighting must fade out at the same radius used by its shadow map.
            _prepared[i * 4 + 3] = MovingLightShadowRenderer.Reach(c);
        }

        // Program-uniform uploads address the deferred program directly. They
        // preserve the native active program without a driver state read or
        // temporarily rebinding it during the shared Opaque render stage.
        GL.ProgramUniform1(_deferred, _count, count);
        GL.ProgramUniformMatrix4(_deferred, _projection, 1, false, projection);
        if (count > 0)
        {
            GL.ProgramUniform3(_deferred, _positions, count, _positionData);
            GL.ProgramUniform3(_deferred, _colors, count, _colorData);
            GL.ProgramUniform4(_deferred, _radiance, count, _prepared);
        }
    }

    internal static void PrepareRadiance(float[] color, float[] result, int offset)
    {
        float peak = Math.Max(color[0], Math.Max(color[1], color[2]));
        // Preserve the established full lamp scale, but never amplify a tiny
        // source to unit radiance. A level-1 amulet is not a level-14 torch.
        float divisor = Math.Max(1f, peak);
        for (int j = 0; j < 3; ++j)
            result[offset + j] = float.IsFinite(peak) && peak > 1e-6f ? Math.Max(color[j] / divisor, 0) : 0;
    }
}