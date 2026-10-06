using System;
using OpenTK.Graphics.OpenGL4;
using Vintagestory.API.Client;
using Vintagestory.API.MathTools;

namespace DRTAgX;

/// <summary>Optional shadow-weighted cumulative air scatter, generated before scene draws.</summary>
internal sealed class AtmosphereFogVolume : IDisposable
{
    private int _program, _matrix, _origin, _strength;
    private readonly float[] _camera = new float[4], _shadowCamera = new float[4];
    internal int Texture { get; private set; }
    internal bool Ready { get; private set; }

    internal void Initialize(ICoreClientAPI api)
    {
        Dispose();
        _program = AtmosphereCompute.Load(api, "drtagx_fog_volume");
        Texture = AtmosphereCompute.Texture(64, 36, 32);
        _matrix = GL.GetUniformLocation(_program, "toShadow");
        _origin = GL.GetUniformLocation(_program, "cameraInShadow");
        _strength = GL.GetUniformLocation(_program, "shaftStrength");
        GL.UseProgram(_program);
        GL.Uniform1(GL.GetUniformLocation(_program, "farShadow"), 0);
        int block = GL.GetUniformBlockIndex(_program, "DrtAtmosphere");
        GL.UniformBlockBinding(_program, block, AtmosphereProgramBindings.BufferBinding);
    }

    internal void Render(ICoreClientAPI api, float[] frame, int buffer, float strength)
    {
        Ready = false;
        if (_program == 0 || api.Settings.Int.Get("shadowMapQuality", 0) <= 0 ||
            strength <= 0.001f || frame[10] > 0.7f || frame[15] < 0.02f ||
            frame[4] <= 0.000001f && Math.Abs(frame[6]) <= 0.000001f && frame[28] * frame[29] <= 0.000001f) return;
        var matrix = api.Render.ShaderUniforms?.ToShadowMapSpaceMatrixFar;
        var buffers = api.Render.FrameBuffers;
        // Verified 1.22.7 far-cascade slot, also used by captured Sheyder renderer.
        if (matrix == null || buffers == null || buffers.Count <= 11 || buffers[11] == null ||
            buffers[11].Disposed || buffers[11].DepthTextureId <= 0) return;
        _camera[0] = frame[20]; _camera[1] = frame[21]; _camera[2] = frame[22]; _camera[3] = 1f;
        Mat4f.MulWithVec4(matrix, _camera, _shadowCamera);
        int generic = GL.GetInteger(GetPName.UniformBufferBinding);
        var previous = IndexedBufferState.Capture(BufferRangeTarget.UniformBuffer, AtmosphereProgramBindings.BufferBinding);
        try
        {
            GL.BindBufferBase(BufferRangeTarget.UniformBuffer, AtmosphereProgramBindings.BufferBinding, buffer);
            GL.UseProgram(_program);
            GL.UniformMatrix4(_matrix, 1, false, matrix);
            GL.Uniform3(_origin, _shadowCamera[0], _shadowCamera[1], _shadowCamera[2]);
            GL.Uniform1(_strength, strength);
            GL.ActiveTexture(TextureUnit.Texture0);
            GL.BindTexture(TextureTarget.Texture2D, buffers[11].DepthTextureId);
            GL.BindImageTexture(0, Texture, 0, true, 0, TextureAccess.WriteOnly, SizedInternalFormat.R16f);
            GL.DispatchCompute(8, 9, 1);
            GL.MemoryBarrier(MemoryBarrierFlags.TextureFetchBarrierBit | MemoryBarrierFlags.ShaderImageAccessBarrierBit);
            Ready = true;
        }
        finally
        {
            // Restoring an indexed range also changes the generic binding.
            previous.Restore();
            GL.BindBuffer(BufferTarget.UniformBuffer, generic);
        }
    }

    public void Dispose()
    {
        Ready = false;
        if (_program != 0) GL.DeleteProgram(_program);
        if (Texture != 0) GL.DeleteTexture(Texture);
        _program = Texture = 0;
    }
}
