using System;
using HarmonyLib;
using OpenTK.Graphics.OpenGL4;
using Vintagestory.API.Client;

namespace DRTAgX;

/// <summary>Validated native visibility-only allocation policy, keyed by wrapper identity and generation.</summary>
internal sealed class AoResourceFormats : IDisposable
{
    private const string PatchId = "drtagx.ao.resource.formats";
    private static AoResourceFormats? _current;
    private readonly Harmony _harmony = new(PatchId);
    private bool _supported;
    private readonly HdrPassState _state = new(false);
    private readonly Signature[] _checked = new Signature[3];
    private readonly record struct Signature(FrameBufferRef Buffer, int Texture, int Width, int Height);
    private static readonly float[] ClearVisibility = { 1f, 1f, 1f, 1f };

    internal void Start(ICoreClientAPI api)
    {
        var rebuild = AccessTools.Method("Vintagestory.Client.NoObf.ClientPlatformWindows:RebuildFrameBuffers");
        if (rebuild == null) { api.Logger.Notification("[DRTAgX] AO R8 inactive: framebuffer recreation contract unavailable."); return; }
        _current = this;
        _harmony.Patch(rebuild, postfix: new HarmonyMethod(typeof(AoResourceFormats), nameof(Recreated)));
        _supported = true;
    }
    private static void Recreated() => _current?.Invalidate();

    internal void Ensure(ICoreClientAPI api)
    {
        if (!_supported) return;
        var buffers = api.Render.FrameBuffers;
        if (buffers == null || buffers.Count <= 15) return;
        for (int i = 0; i < 3; ++i)
        {
            var target = buffers[13 + i];
            if (target is not { Disposed: false, FboId: > 0, Width: > 0, Height: > 0 } || target.ColorTextureIds is not { Length: > 0 }) continue;
            var signature = new Signature(target, target.ColorTextureIds![0], target.Width, target.Height);
            if (_checked[i] == signature) continue;
            _checked[i] = signature; // Failed/unsupported generations are not retried each frame.
            _state.Capture();
            using var state = _state;
            GL.ActiveTexture(TextureUnit.Texture0);
            GL.BindTexture(TextureTarget.Texture2D, signature.Texture);
            GL.GetTexLevelParameter(TextureTarget.Texture2D, 0, GetTextureParameter.TextureInternalFormat, out int format);
            GL.GetTexLevelParameter(TextureTarget.Texture2D, 0, GetTextureParameter.TextureRedSize, out int redBits);
            // Raw AO and both denoise targets are sampled exclusively through R in captured native/Sheyder paths.
            // Keep the separate RGB noise texture and any unexpected/high-precision resource unchanged.
            if (format == (int)PixelInternalFormat.R8 || redBits != 8 ||
                (format != (int)PixelInternalFormat.Rgb && format != (int)PixelInternalFormat.Rgb8 && format != (int)PixelInternalFormat.Rgba8)) continue;
            GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer, target.FboId);
            GL.GetFramebufferAttachmentParameter(FramebufferTarget.DrawFramebuffer, FramebufferAttachment.ColorAttachment0,
                FramebufferParameterName.FramebufferAttachmentObjectName, out int attachment);
            if (attachment != signature.Texture) continue;
            GL.BindBuffer(BufferTarget.PixelUnpackBuffer, 0);
            GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.R8, target.Width, target.Height, 0, PixelFormat.Red, PixelType.UnsignedByte, IntPtr.Zero);
            if (GL.CheckFramebufferStatus(FramebufferTarget.DrawFramebuffer) != FramebufferErrorCode.FramebufferComplete)
            {
                GL.TexImage2D(TextureTarget.Texture2D, 0, (PixelInternalFormat)format, target.Width, target.Height, 0,
                    format == (int)PixelInternalFormat.Rgba8 ? PixelFormat.Rgba : PixelFormat.Rgb, PixelType.UnsignedByte, IntPtr.Zero);
                api.Logger.Warning("[DRTAgX] AO R8 rejected for slot {0}; original format retained.", 13 + i);
            }
            else
            {
                GL.ClearBuffer(ClearBuffer.Color, 0, ClearVisibility);
                api.Logger.Notification("[DRTAgX] AO visibility slot {0}: R8, {1}x{2}; borrowed IDs retained.", 13 + i, target.Width, target.Height);
            }
        }
    }

    internal void Invalidate() => Array.Clear(_checked);
    public void Dispose()
    {
        _harmony.UnpatchAll(PatchId);
        if (ReferenceEquals(_current, this)) _current = null;
        _supported = false; Invalidate(); // Native targets retain their ownership and valid R sampling.
    }
}
