using System;
using System.Collections.Generic;
using OpenTK.Graphics.OpenGL4;
using Vintagestory.API.Client;

namespace DRTAgX
{
    internal sealed class HdrSceneBuffers
    {
        private static readonly EnumFrameBuffer[] Targets =
        {
            EnumFrameBuffer.Primary
        };

        private readonly Dictionary<EnumFrameBuffer, BufferSignature> _checked = new();
        private readonly HashSet<EnumFrameBuffer> _warnedColorMask = new();

        // Called on the render thread at EnumRenderStage.Before, before scene draws.
        // The owned bloom chain does not use native blur attachments.
        internal void Ensure(ICoreClientAPI api)
        {
            foreach (EnumFrameBuffer kind in Targets) EnsureAttachment(api, kind);
        }

        private void EnsureAttachment(ICoreClientAPI api, EnumFrameBuffer kind)
        {
            var buffers = api.Render.FrameBuffers;
            int index = (int)kind;
            if (buffers == null || index < 0 || index >= buffers.Count) return;

            FrameBufferRef? buffer = buffers[index];
            if (buffer == null || buffer.Disposed || buffer.ColorTextureIds == null || buffer.ColorTextureIds.Length == 0) return;

            int textureId = buffer.ColorTextureIds[0];
            if (textureId <= 0) return;
            int width = buffer.Width;
            int height = buffer.Height;
            bool needsTextureExtent = width <= 0 || height <= 0;
            bool wasChecked = _checked.TryGetValue(kind, out BufferSignature prior);
            // The low-res vertical FrameBufferRef keeps zero dimensions. Its texture only
            // needs another GL size query when the native attachment or frame size changes.
            if (needsTextureExtent && wasChecked && ReferenceEquals(buffer, prior.Framebuffer)
                && textureId == prior.TextureId && api.Render.FrameWidth == prior.FrameWidth
                && api.Render.FrameHeight == prior.FrameHeight) return;
            if (!needsTextureExtent && wasChecked
                && ReferenceEquals(buffer, prior.Framebuffer) && textureId == prior.TextureId
                && width == prior.Width && height == prior.Height) return;

            // Capture only the GL bindings and masks touched below. A bound unpack PBO would
            // turn IntPtr.Zero into a PBO offset instead of an empty texture allocation.
            using var state = new SavedTextureMutationState();
            GL.BindTexture(TextureTarget.Texture2D, textureId);
            // Vintage Story reports zero Width/Height for BlurVerticalLowRes even though
            // its color texture has storage. Read the actual texture extent in that case.
            if (needsTextureExtent)
            {
                GL.GetTexLevelParameter(TextureTarget.Texture2D, 0, GetTextureParameter.TextureWidth, out width);
                GL.GetTexLevelParameter(TextureTarget.Texture2D, 0, GetTextureParameter.TextureHeight, out height);
                if (width <= 0 || height <= 0) return;
            }
            if (wasChecked && ReferenceEquals(buffer, prior.Framebuffer) && textureId == prior.TextureId
                && width == prior.Width && height == prior.Height) return;

            GL.GetTexLevelParameter(TextureTarget.Texture2D, 0, GetTextureParameter.TextureInternalFormat, out int oldFormat);

            if (oldFormat == (int)PixelInternalFormat.Rgba16f)
            {
                MarkChecked(api, kind, buffer, textureId, width, height);
                api.Logger.Notification($"[DRT AgX] {kind} color 0 is already RGBA16F (texture {textureId}).");
                return;
            }

            if (oldFormat != (int)PixelInternalFormat.Rgba8)
            {
                api.Logger.Warning($"[DRT AgX] {kind} color 0 has unexpected format 0x{oldFormat:X}; HDR promotion skipped.");
                MarkChecked(api, kind, buffer, textureId, width, height);
                return;
            }

            // Do not modify the engine's per-target color write masks. The attachment
            // must be fully writable so its replacement can be cleared in this frame.
            if (!state.ColorWriteEnabled)
            {
                if (_warnedColorMask.Add(kind))
                {
                    api.Logger.Warning($"[DRT AgX] {kind} color 0 has a restricted color mask; HDR promotion deferred.");
                }
                return;
            }

            GL.BindBuffer(BufferTarget.PixelUnpackBuffer, 0);
            GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba16f,
                width, height, 0, PixelFormat.Rgba, PixelType.HalfFloat, IntPtr.Zero);
            GL.GetTexLevelParameter(TextureTarget.Texture2D, 0, GetTextureParameter.TextureInternalFormat, out int newFormat);

            if (newFormat != (int)PixelInternalFormat.Rgba16f)
            {
                api.Logger.Warning($"[DRT AgX] {kind} color 0 remained 0x{newFormat:X} after RGBA16F allocation.");
                MarkChecked(api, kind, buffer, textureId, width, height);
                return;
            }

            // Reallocation discards the old contents. Clear only color 0; preserve the
            // other native color attachments and depth, which may carry packed metadata.
            GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer, buffer.FboId);
            FramebufferErrorCode status = GL.CheckFramebufferStatus(FramebufferTarget.DrawFramebuffer);
            if (status != FramebufferErrorCode.FramebufferComplete)
            {
                // Keep the native pass usable if this driver rejects the half-float attachment.
                GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba8,
                    width, height, 0, PixelFormat.Rgba, PixelType.UnsignedByte, IntPtr.Zero);
                GL.Disable(EnableCap.ScissorTest);
                GL.GetFloat(GetPName.ColorClearValue, state.ClearColor);
                GL.ClearBuffer(ClearBuffer.Color, 0, state.ClearColor);
                api.Logger.Error($"[DRT AgX] {kind} became incomplete ({status}); restored RGBA8.");
                MarkChecked(api, kind, buffer, textureId, width, height);
                return;
            }
            GL.Disable(EnableCap.ScissorTest);
            GL.GetFloat(GetPName.ColorClearValue, state.ClearColor);
            GL.ClearBuffer(ClearBuffer.Color, 0, state.ClearColor);

            _warnedColorMask.Remove(kind);
            MarkChecked(api, kind, buffer, textureId, width, height);
            api.Logger.Notification($"[DRT AgX] {kind} color 0 texture {textureId} promoted from RGBA8 to RGBA16F ({width}x{height}).");
        }

        private void MarkChecked(ICoreClientAPI api, EnumFrameBuffer kind, FrameBufferRef buffer, int textureId, int width, int height)
        {
            _checked[kind] = new BufferSignature(buffer, textureId, width, height,
                api.Render.FrameWidth, api.Render.FrameHeight);
        }

        private readonly record struct BufferSignature(FrameBufferRef Framebuffer, int TextureId, int Width,
            int Height, int FrameWidth, int FrameHeight);

        private readonly struct SavedTextureMutationState : IDisposable
        {
            private readonly int _texture2D;
            private readonly int _pixelUnpackBuffer;
            private readonly int _drawFramebuffer;
            private readonly bool _scissorTest;
            private readonly bool[] _colorMask = new bool[4];

            internal readonly float[] ClearColor = new float[4];
            internal bool ColorWriteEnabled => _colorMask[0] && _colorMask[1] && _colorMask[2] && _colorMask[3];

            public SavedTextureMutationState()
            {
                GL.GetInteger(GetPName.TextureBinding2D, out _texture2D);
                GL.GetInteger(GetPName.PixelUnpackBufferBinding, out _pixelUnpackBuffer);
                GL.GetInteger(GetPName.DrawFramebufferBinding, out _drawFramebuffer);
                _scissorTest = GL.IsEnabled(EnableCap.ScissorTest);
                GL.GetBoolean(GetPName.ColorWritemask, _colorMask);
            }

            public void Dispose()
            {
                GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer, _drawFramebuffer);
                GL.BindBuffer(BufferTarget.PixelUnpackBuffer, _pixelUnpackBuffer);
                GL.BindTexture(TextureTarget.Texture2D, _texture2D);
                if (_scissorTest) GL.Enable(EnableCap.ScissorTest);
                else GL.Disable(EnableCap.ScissorTest);
            }
        }
    }
}
