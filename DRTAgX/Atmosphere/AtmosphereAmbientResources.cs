using System;
using OpenTK.Graphics.OpenGL4;
using Vintagestory.API.Client;

namespace DRTAgX;

/// <summary>One workgroup resolves sky irradiance into the existing UBO; no readback or extra samplers.</summary>
internal sealed class AtmosphereAmbientResources : IDisposable
{
    internal const int ResultOffset = 104;
    private int _program;

    internal void Initialize(ICoreClientAPI api)
    {
        Dispose();
        _program = AtmosphereCompute.Load(api, "drtagx_atmosphere_ambient");
        GL.UniformBlockBinding(_program, GL.GetUniformBlockIndex(_program, "DrtAtmosphere"), 10);
        GL.UseProgram(_program);
        GL.Uniform1(GL.GetUniformLocation(_program, "drtSkyViewPrevious"), 0);
        GL.Uniform1(GL.GetUniformLocation(_program, "drtSkyViewCurrent"), 1);
    }

    internal void Render(AtmosphereSkyResources sky, int frameBuffer)
    {
        // SSBO slot 11 is scoped to this owned compute dispatch; no native draw sees it.
        int genericSsbo = GL.GetInteger(GetPName.ShaderStorageBufferBinding);
        int genericUbo = GL.GetInteger(GetPName.UniformBufferBinding);
        GL.GetInteger(GetIndexedPName.UniformBufferBinding, 10, out int previousUbo);
        GL.GetInteger64(GetIndexedPName.UniformBufferStart, 10, out long previousUboStart);
        GL.GetInteger64(GetIndexedPName.UniformBufferSize, 10, out long previousUboSize);
        GL.GetInteger(GetIndexedPName.ShaderStorageBufferBinding, 11, out int previousSsbo);
        GL.GetInteger64(GetIndexedPName.ShaderStorageBufferStart, 11, out long previousStart);
        GL.GetInteger64(GetIndexedPName.ShaderStorageBufferSize, 11, out long previousSize);
        try
        {
            GL.UseProgram(_program);
            GL.ActiveTexture(TextureUnit.Texture0); GL.BindTexture(TextureTarget.Texture2D, sky.Previous);
            GL.ActiveTexture(TextureUnit.Texture1); GL.BindTexture(TextureTarget.Texture2D, sky.Current);
            GL.BindBufferBase(BufferRangeTarget.UniformBuffer, 10, frameBuffer);
            // Bind the whole allocation; the shader writes only the disjoint tail.
            // This avoids assuming vendor-specific SSBO offset alignment.
            GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 11, frameBuffer);
            GL.DispatchCompute(1, 1, 1);
            // Shader-written bytes become same-frame uniform input to every surface program.
            GL.MemoryBarrier(MemoryBarrierFlags.ShaderStorageBarrierBit | MemoryBarrierFlags.UniformBarrierBit |
                MemoryBarrierFlags.BufferUpdateBarrierBit);
        }
        finally
        {
            if (previousSsbo != 0 && previousSize > 0)
                GL.BindBufferRange(BufferRangeTarget.ShaderStorageBuffer, 11, previousSsbo,
                    (IntPtr)previousStart, (IntPtr)previousSize);
            else GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 11, previousSsbo);
            GL.BindBuffer(BufferTarget.ShaderStorageBuffer, genericSsbo);
            // Restore ranges as well as names: another renderer may share a suballocated UBO.
            if (previousUbo != 0 && previousUboSize > 0)
                GL.BindBufferRange(BufferRangeTarget.UniformBuffer, 10, previousUbo,
                    (IntPtr)previousUboStart, (IntPtr)previousUboSize);
            else GL.BindBufferBase(BufferRangeTarget.UniformBuffer, 10, previousUbo);
            GL.BindBuffer(BufferTarget.UniformBuffer, genericUbo);
        }
        // The enclosing AtmosphereComputeState restores program and texture units 0/1.
    }

    public void Dispose()
    {
        if (_program != 0) GL.DeleteProgram(_program);
        _program = 0;
    }
}
