using System;
using OpenTK.Graphics.OpenGL4;

namespace DRTAgX;

/// <summary>
/// RAII guard that preserves and restores OpenGL state during HDR downsampling,
/// progressive bloom reconstruction, and auto-exposure adaptation passes.
/// Restores texture units 0..4, active framebuffers, pixel unpack buffer, VAO, viewport, and rasterizer states.
/// </summary>
internal sealed class HdrPassState : IDisposable
{
    private static readonly int[] Units = { 0, 1, 2, 3, 4 };
    private readonly int[] _textures = new int[Units.Length];
    private readonly int[] _samplers = new int[Units.Length];
    private int _activeTexture, _drawFbo, _readFbo, _program, _vao, _unpack;
    private readonly int[] _viewport = new int[4];
    private readonly bool[] _colorMask0 = new bool[4], _colorMask1 = new bool[4];
    private bool _depth, _blend0, _blend1, _cull, _scissor, _depthMask;

    /// <summary>
    /// Captures the initial OpenGL state before binding HDR pyramid render targets and shaders.
    /// </summary>
    internal HdrPassState() => Capture();

    internal HdrPassState(bool capture)
    {
        if (capture) Capture();
    }

    // Render-thread owners reuse the arrays across non-overlapping passes.
    // Each Capture must be paired with Dispose before the next capture.
    internal void Capture()
    {
        GL.GetInteger(GetPName.ActiveTexture, out _activeTexture);
        GL.GetInteger(GetPName.DrawFramebufferBinding, out _drawFbo);
        GL.GetInteger(GetPName.ReadFramebufferBinding, out _readFbo);
        GL.GetInteger(GetPName.CurrentProgram, out _program);
        GL.GetInteger(GetPName.VertexArrayBinding, out _vao);
        GL.GetInteger(GetPName.PixelUnpackBufferBinding, out _unpack);
        GL.GetInteger(GetPName.Viewport, _viewport);
        // ColorMask without an index broadcasts to every native MRT. Borrow
        // only the two outputs used by owned HDR draws and retain each mask.
        GL.GetBoolean(GetIndexedPName.ColorWritemask, 0, _colorMask0);
        GL.GetBoolean(GetIndexedPName.ColorWritemask, 1, _colorMask1);
        GL.GetBoolean(GetPName.DepthWritemask, out _depthMask);
        _depth = GL.IsEnabled(EnableCap.DepthTest);
        _blend0 = GL.IsEnabled(IndexedEnableCap.Blend, 0);
        _blend1 = GL.IsEnabled(IndexedEnableCap.Blend, 1);
        _cull = GL.IsEnabled(EnableCap.CullFace);
        _scissor = GL.IsEnabled(EnableCap.ScissorTest);
        for (int i = 0; i < Units.Length; i++)
        {
            GL.ActiveTexture(TextureUnit.Texture0 + Units[i]);
            GL.GetInteger(GetPName.TextureBinding2D, out _textures[i]);
            GL.GetInteger(GetPName.SamplerBinding, out _samplers[i]);
            GL.BindSampler(Units[i], 0);
        }

        GL.Disable(EnableCap.DepthTest);
        GL.Disable(IndexedEnableCap.Blend, 0);
        GL.Disable(IndexedEnableCap.Blend, 1);
        GL.Disable(EnableCap.CullFace);
        GL.Disable(EnableCap.ScissorTest);
        GL.DepthMask(false);
        GL.ColorMask(0, true, true, true, true);
        GL.ColorMask(1, true, true, true, true);
    }

    public void Dispose()
    {
        GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer, _drawFbo);
        GL.BindFramebuffer(FramebufferTarget.ReadFramebuffer, _readFbo);
        GL.Viewport(_viewport[0], _viewport[1], _viewport[2], _viewport[3]);
        GL.UseProgram(_program);
        GL.BindVertexArray(_vao);
        GL.BindBuffer(BufferTarget.PixelUnpackBuffer, _unpack);
        GL.DepthMask(_depthMask);
        GL.ColorMask(0, _colorMask0[0], _colorMask0[1], _colorMask0[2], _colorMask0[3]);
        GL.ColorMask(1, _colorMask1[0], _colorMask1[1], _colorMask1[2], _colorMask1[3]);
        Set(EnableCap.DepthTest, _depth);
        if (_blend0) GL.Enable(IndexedEnableCap.Blend, 0);
        else GL.Disable(IndexedEnableCap.Blend, 0);
        if (_blend1) GL.Enable(IndexedEnableCap.Blend, 1);
        else GL.Disable(IndexedEnableCap.Blend, 1);
        Set(EnableCap.CullFace, _cull);
        Set(EnableCap.ScissorTest, _scissor);
        for (int i = 0; i < Units.Length; i++)
        {
            GL.ActiveTexture(TextureUnit.Texture0 + Units[i]);
            GL.BindTexture(TextureTarget.Texture2D, _textures[i]);
            GL.BindSampler(Units[i], _samplers[i]);
        }

        GL.ActiveTexture((TextureUnit)_activeTexture);
    }

    private static void Set(EnableCap capability, bool enabled)
    {
        if (enabled) GL.Enable(capability);
        else GL.Disable(capability);
    }
}