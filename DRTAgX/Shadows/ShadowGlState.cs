using System;
using OpenTK.Graphics.OpenGL4;

namespace DRTAgX;

/// <summary>
/// RAII helper that captures and restores OpenGL state during shadow depth map generation.
/// Preserves framebuffers, active shaders, vertex arrays, buffer bindings (VBO, IBO, SSBO),
/// viewport/scissor metrics, depth/blend/cull/polygon-offset capabilities, and texture bindings to ensure
/// complete isolation from native engine rendering passes.
/// </summary>
internal sealed class ShadowGlState : IDisposable
{
    private int _drawFbo, _readFbo, _program, _vao, _activeTexture, _texture0,
        _sampler0,
        _ssbo, _ubo,
        _arrayBuffer, _elementBuffer, _depthFunction,
        _viewportX, _viewportY, _viewportW, _viewportH,
        _scissorX, _scissorY, _scissorW, _scissorH;
    private bool _depth, _blend, _cull, _scissor, _polygonOffset, _depthMask;
    private double _clearDepth;
    private IndexedBufferState _uniform5;
    private IndexedBufferState _storage3;
    private readonly bool[] _colorMask = new bool[4];

    /// <summary>
    /// Captures the active OpenGL state across all rasterization, binding, and framebuffer targets.
    /// </summary>
    private readonly int[] _viewport = new int[4], _scissorBox = new int[4];
    internal ShadowGlState() => Capture();
    internal ShadowGlState(bool capture) { if (capture) Capture(); }

    // A renderer can reuse one saver across non-overlapping passes, avoiding
    // per-bake state objects and query arrays. Capture/Dispose must remain paired.
    internal void Capture()
    {
        _drawFbo = GL.GetInteger(GetPName.DrawFramebufferBinding);
        _readFbo = GL.GetInteger(GetPName.ReadFramebufferBinding);
        _program = GL.GetInteger(GetPName.CurrentProgram);
        _vao = GL.GetInteger(GetPName.VertexArrayBinding);
        _arrayBuffer = GL.GetInteger(GetPName.ArrayBufferBinding);
        _elementBuffer = GL.GetInteger(GetPName.ElementArrayBufferBinding);
        _ssbo = GL.GetInteger(GetPName.ShaderStorageBufferBinding);
        _ubo = GL.GetInteger(GetPName.UniformBufferBinding);
        _uniform5 = IndexedBufferState.Capture(BufferRangeTarget.UniformBuffer, 5);
        _activeTexture = GL.GetInteger(GetPName.ActiveTexture);
        _depthFunction = GL.GetInteger(GetPName.DepthFunc);
        _clearDepth = GL.GetDouble(GetPName.DepthClearValue);
        int[] viewport = _viewport;
        GL.GetInteger(GetPName.Viewport, viewport);
        (_viewportX, _viewportY, _viewportW, _viewportH) = (viewport[0], viewport[1], viewport[2], viewport[3]);
        int[] scissor = _scissorBox;
        GL.GetInteger(GetPName.ScissorBox, scissor);
        (_scissorX, _scissorY, _scissorW, _scissorH) = (scissor[0], scissor[1], scissor[2], scissor[3]);
        GL.GetBoolean(GetPName.DepthWritemask, out _depthMask);
        GL.GetBoolean(GetPName.ColorWritemask, _colorMask);
        _depth = GL.IsEnabled(EnableCap.DepthTest);
        _blend = GL.IsEnabled(EnableCap.Blend);
        _cull = GL.IsEnabled(EnableCap.CullFace);
        _scissor = GL.IsEnabled(EnableCap.ScissorTest);
        _polygonOffset = GL.IsEnabled(EnableCap.PolygonOffsetFill);
        _storage3 = IndexedBufferState.Capture(BufferRangeTarget.ShaderStorageBuffer, 3);
        GL.ActiveTexture(TextureUnit.Texture0);
        _texture0 = GL.GetInteger(GetPName.TextureBinding2D);
        _sampler0 = GL.GetInteger(GetPName.SamplerBinding);
    }

    public void Dispose()
    {
        GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer, _drawFbo);
        GL.BindFramebuffer(FramebufferTarget.ReadFramebuffer, _readFbo);
        GL.UseProgram(_program);
        GL.BindVertexArray(_vao);
        GL.BindBuffer(BufferTarget.ArrayBuffer, _arrayBuffer);
        GL.BindBuffer(BufferTarget.ElementArrayBuffer, _elementBuffer);
        _storage3.Restore();
        GL.BindBuffer(BufferTarget.ShaderStorageBuffer, _ssbo);
        _uniform5.Restore();
        GL.BindBuffer(BufferTarget.UniformBuffer, _ubo);
        GL.Viewport(_viewportX, _viewportY, _viewportW, _viewportH);
        GL.Scissor(_scissorX, _scissorY, _scissorW, _scissorH);
        GL.DepthFunc((DepthFunction)_depthFunction);
        GL.DepthMask(_depthMask);
        GL.ClearDepth(_clearDepth);
        GL.ColorMask(_colorMask[0], _colorMask[1], _colorMask[2], _colorMask[3]);
        Set(EnableCap.DepthTest, _depth);
        Set(EnableCap.Blend, _blend);
        Set(EnableCap.CullFace, _cull);
        Set(EnableCap.ScissorTest, _scissor);
        Set(EnableCap.PolygonOffsetFill, _polygonOffset);
        GL.ActiveTexture(TextureUnit.Texture0);
        GL.BindTexture(TextureTarget.Texture2D, _texture0);
        GL.BindSampler(0, _sampler0);
        GL.ActiveTexture((TextureUnit)_activeTexture);
    }

    private static void Set(EnableCap cap, bool enabled)
    {
        if (enabled) GL.Enable(cap); else GL.Disable(cap);
    }
}
