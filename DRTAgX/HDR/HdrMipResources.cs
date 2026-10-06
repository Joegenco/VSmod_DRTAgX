using System;
using System.Collections.Generic;
using OpenTK.Graphics.OpenGL4;

namespace DRTAgX;

// All textures are private to DRTAgX; native framebuffer slots retain their owners.
internal sealed class HdrMipResources : IDisposable
{
    internal const int LevelCount = 5;
    internal readonly int[] Scene = new int[LevelCount];
    internal readonly int[] Scratch = new int[LevelCount];
    internal readonly int[] Width = new int[LevelCount];
    internal readonly int[] Height = new int[LevelCount];
    internal readonly int[] Exposure = new int[2];
    internal int Bloom, Meter, DrawFbo, MeterLod, ExposureIndex;
    internal int ActiveLevels { get; private set; } = LevelCount;
    internal int MeterWidth, MeterHeight;
    private bool _performance;
    internal bool HasExposure;
    private static readonly DrawBuffersEnum[] TwoColorAttachments = { DrawBuffersEnum.ColorAttachment0, DrawBuffersEnum.ColorAttachment1 };
    private static readonly float[] ZeroClearColor = { 0f, 0f, 0f, 0f };
    private readonly HashSet<(int Color, int Meter)> _validatedTargets = new();
    private int _sourceWidth, _sourceHeight;

    internal bool Ensure(int sourceWidth, int sourceHeight)
    {
        if (sourceWidth < 2 || sourceHeight < 2) return false;
        bool performance = FrameQuality.Current.Performance;
        if (DrawFbo != 0 && sourceWidth == _sourceWidth && sourceHeight == _sourceHeight && _performance == performance) return true;
        Dispose();
        _sourceWidth = sourceWidth;
        _sourceHeight = sourceHeight;
        _performance = performance;
        ActiveLevels = performance ? 4 : LevelCount;
        MeterWidth = Math.Max(1, sourceWidth / 2); MeterHeight = Math.Max(1, sourceHeight / 2);
        int w = Math.Max(1, sourceWidth / (performance ? 4 : 2)), h = Math.Max(1, sourceHeight / (performance ? 4 : 2));
        try
        {
            GL.BindBuffer(BufferTarget.PixelUnpackBuffer, 0);
            DrawFbo = GL.GenFramebuffer();
            for (int i = 0; i < ActiveLevels; i++)
            {
                Width[i] = w;
                Height[i] = h;
                Scene[i] = CreateTexture(w, h, PixelInternalFormat.Rgba16f, PixelFormat.Rgba);
                Scratch[i] = CreateTexture(w, h, PixelInternalFormat.Rgba16f, PixelFormat.Rgba);
                w = Math.Max(1, w / 2);
                h = Math.Max(1, h / 2);
            }
            Bloom = CreateTexture(Width[0], Height[0], PixelInternalFormat.Rgba16f, PixelFormat.Rgba);
            // Keep the shipping half-resolution meter and emissive extraction order in both modes.
            MeterLod = (int)Math.Floor(Math.Log2(Math.Max(MeterWidth, MeterHeight)));
            Meter = CreateTexture(MeterWidth, MeterHeight, PixelInternalFormat.Rg16f, PixelFormat.Rg, MeterLod);
            for (int i = 0; i < 2; i++)
            {
                Exposure[i] = CreateTexture(1, 1, PixelInternalFormat.R16f, PixelFormat.Red);
                BindTarget(Exposure[i], 1, 1);
                GL.ClearBuffer(ClearBuffer.Color, 0, ZeroClearColor);
            }
            ExposureIndex = 0;
            HasExposure = false;
            return true;
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    // Attach a single owned output, with an optional metering MRT on the first downsample.
    internal void BindTarget(int color, int width, int height, int meter = 0)
    {
        GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer, DrawFbo);
        GL.FramebufferTexture2D(FramebufferTarget.DrawFramebuffer, FramebufferAttachment.ColorAttachment0,
            TextureTarget.Texture2D, color, 0);
        GL.FramebufferTexture2D(FramebufferTarget.DrawFramebuffer, FramebufferAttachment.ColorAttachment1,
            TextureTarget.Texture2D, meter, 0);
        if (meter != 0)
            GL.DrawBuffers(2, TwoColorAttachments);
        else
            GL.DrawBuffer(DrawBufferMode.ColorAttachment0);
        GL.Viewport(0, 0, width, height);
        if (!_validatedTargets.Contains((color, meter)))
        {
            if (GL.CheckFramebufferStatus(FramebufferTarget.DrawFramebuffer) != FramebufferErrorCode.FramebufferComplete)
                throw new InvalidOperationException("DRTAgX HDR framebuffer is incomplete.");
            _validatedTargets.Add((color, meter));
        }
    }

    internal void GenerateMeterMips()
    {
        GL.ActiveTexture(TextureUnit.Texture0);
        GL.BindTexture(TextureTarget.Texture2D, Meter);
        GL.GenerateMipmap(GenerateMipmapTarget.Texture2D);
    }

    internal void BindMeterOnly()
    {
        // Fragment output 0 is discarded; output 1 continues writing RG weighted luminance.
        GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer, DrawFbo);
        GL.FramebufferTexture2D(FramebufferTarget.DrawFramebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, 0, 0);
        GL.FramebufferTexture2D(FramebufferTarget.DrawFramebuffer, FramebufferAttachment.ColorAttachment1, TextureTarget.Texture2D, Meter, 0);
        GL.DrawBuffers(2, MeterOnlyBuffers);
        GL.Viewport(0, 0, MeterWidth, MeterHeight);
        if (_validatedTargets.Add((0, Meter)) && GL.CheckFramebufferStatus(FramebufferTarget.DrawFramebuffer) != FramebufferErrorCode.FramebufferComplete)
        { _validatedTargets.Remove((0, Meter)); throw new InvalidOperationException("DRTAgX meter framebuffer is incomplete."); }
    }
    private static readonly DrawBuffersEnum[] MeterOnlyBuffers = { DrawBuffersEnum.None, DrawBuffersEnum.ColorAttachment1 };

    private static int CreateTexture(int width, int height, PixelInternalFormat internalFormat,
        PixelFormat pixelFormat, int mipLevels = 0)
    {
        int texture = GL.GenTexture();
        GL.BindTexture(TextureTarget.Texture2D, texture);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter,
            (int)(mipLevels > 0 ? TextureMinFilter.LinearMipmapLinear : TextureMinFilter.Linear));
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMaxLevel, mipLevels);
        for (int i = 0; i <= mipLevels; i++)
            GL.TexImage2D(TextureTarget.Texture2D, i, internalFormat,
                Math.Max(1, width >> i), Math.Max(1, height >> i), 0, pixelFormat, PixelType.HalfFloat, IntPtr.Zero);
        return texture;
    }

    public void Dispose()
    {
        foreach (int id in Scene) if (id != 0) GL.DeleteTexture(id);
        foreach (int id in Scratch) if (id != 0) GL.DeleteTexture(id);
        foreach (int id in Exposure) if (id != 0) GL.DeleteTexture(id);
        if (Bloom != 0) GL.DeleteTexture(Bloom);
        if (Meter != 0) GL.DeleteTexture(Meter);
        if (DrawFbo != 0) GL.DeleteFramebuffer(DrawFbo);
        Array.Clear(Scene); Array.Clear(Scratch); Array.Clear(Exposure);
        Bloom = Meter = DrawFbo = 0;
        _validatedTargets.Clear();
        _sourceWidth = _sourceHeight = 0;
        HasExposure = false;
    }
}
