using System;
using OpenTK.Graphics.OpenGL4;

namespace DRTAgX;

/// <summary>Reusable render-thread guard for compute passes; no raster/FBO state is changed.</summary>
internal sealed class AtmosphereComputeState : IDisposable
{
    private int _program, _active, _ubo, _unpack, _pack;
    private int _image, _level, _layered, _layer, _access, _format;
    private readonly int[] _textures = new int[2], _textures3D = new int[2], _samplers = new int[2];

    internal void Capture()
    {
        _program = GL.GetInteger(GetPName.CurrentProgram);
        _active = GL.GetInteger(GetPName.ActiveTexture);
        _ubo = GL.GetInteger(GetPName.UniformBufferBinding);
        _unpack = GL.GetInteger(GetPName.PixelUnpackBufferBinding);
        _pack = GL.GetInteger(GetPName.PixelPackBufferBinding);
        GL.GetInteger((GetIndexedPName)All.ImageBindingName, 0, out _image);
        GL.GetInteger((GetIndexedPName)All.ImageBindingLevel, 0, out _level);
        GL.GetInteger((GetIndexedPName)All.ImageBindingLayered, 0, out _layered);
        GL.GetInteger((GetIndexedPName)All.ImageBindingLayer, 0, out _layer);
        GL.GetInteger((GetIndexedPName)All.ImageBindingAccess, 0, out _access);
        GL.GetInteger((GetIndexedPName)All.ImageBindingFormat, 0, out _format);
        for (int i = 0; i < 2; ++i)
        {
            GL.ActiveTexture(TextureUnit.Texture0 + i);
            _textures[i] = GL.GetInteger(GetPName.TextureBinding2D);
            _textures3D[i] = GL.GetInteger(GetPName.TextureBinding3D);
            _samplers[i] = GL.GetInteger(GetPName.SamplerBinding);
            GL.BindSampler(i, 0);
        }
        GL.ActiveTexture(TextureUnit.Texture0);
        GL.BindBuffer(BufferTarget.PixelUnpackBuffer, 0);
        GL.BindBuffer(BufferTarget.PixelPackBuffer, 0);
    }

    public void Dispose()
    {
        GL.UseProgram(_program);
        GL.BindBuffer(BufferTarget.UniformBuffer, _ubo);
        GL.BindBuffer(BufferTarget.PixelUnpackBuffer, _unpack);
        GL.BindBuffer(BufferTarget.PixelPackBuffer, _pack);
        GL.BindImageTexture(0, _image, _level, _layered != 0, _layer,
            (TextureAccess)_access, (SizedInternalFormat)_format);
        for (int i = 0; i < 2; ++i)
        {
            GL.ActiveTexture(TextureUnit.Texture0 + i);
            GL.BindTexture(TextureTarget.Texture2D, _textures[i]);
            GL.BindTexture(TextureTarget.Texture3D, _textures3D[i]);
            GL.BindSampler(i, _samplers[i]);
        }
        GL.ActiveTexture((TextureUnit)_active);
    }
}
