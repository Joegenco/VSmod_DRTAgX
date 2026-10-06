using System;
using System.Collections.Generic;
using System.Reflection;
using DRTAgX;
using OpenTK.Graphics.OpenGL4;
using Vintagestory.API.Client;
using Vintagestory.API.Common;

internal static class AoFormatProbe
{
    internal static void Run()
    {
        ProbeAssets.Api([]); // Install the existing fixture dependency resolver before proxy generation.
        using var guard = new HdrPassState();
        var buffers = new List<FrameBufferRef>();
        for (int i=0; i<16; ++i) buffers.Add(null);
        var render = SurfaceApiProxy.Make<IRenderAPI>((method, _) => method.Name == "get_FrameBuffers" ? buffers : throw new NotSupportedException(method.Name));
        var logger = SurfaceApiProxy.Make<ILogger>((_, _) => null);
        var api = SurfaceApiProxy.Make<ICoreClientAPI>((method, _) => method.Name switch {
            "get_Render" => render, "get_Logger" => logger, _ => throw new NotSupportedException(method.Name)
        });
        using var owner = new AoResourceFormats();
        typeof(AoResourceFormats).GetField("_supported", BindingFlags.Instance|BindingFlags.NonPublic).SetValue(owner, true);
        int[] textures = new int[3], fbos = new int[3];
        GL.GenTextures(3, textures); GL.GenFramebuffers(3, fbos);
        int pbo = GL.GenBuffer(), depth = GL.GenTexture();
        void Allocate(int texture, int w, int h, PixelInternalFormat format) {
            GL.BindBuffer(BufferTarget.PixelUnpackBuffer, 0); GL.ActiveTexture(TextureUnit.Texture0);
            GL.BindTexture(TextureTarget.Texture2D, texture);
            GL.TexImage2D(TextureTarget.Texture2D, 0, format, w, h, 0,
                format == PixelInternalFormat.DepthComponent24 ? PixelFormat.DepthComponent : PixelFormat.Rgba, PixelType.UnsignedByte, IntPtr.Zero);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
        }
        void Check(bool condition, string label) { if (!condition) throw new Exception(label); }
        int Format(int texture) {
            GL.BindTexture(TextureTarget.Texture2D, texture);
            GL.GetTexLevelParameter(TextureTarget.Texture2D, 0, GetTextureParameter.TextureInternalFormat, out int value); return value;
        }
        try {
            for (int i=0; i<3; ++i) {
                Allocate(textures[i], 32, 16, i==0 ? PixelInternalFormat.Rgb8 : PixelInternalFormat.Rgba8);
                GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer, fbos[i]);
                GL.FramebufferTexture2D(FramebufferTarget.DrawFramebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, textures[i], 0);
                GL.DrawBuffer(DrawBufferMode.ColorAttachment0);
                buffers[13+i] = new FrameBufferRef { FboId=fbos[i], Width=32, Height=16, ColorTextureIds=[textures[i]] };
            }
            Allocate(depth, 32, 16, PixelInternalFormat.DepthComponent24);
            GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer, fbos[0]);
            GL.FramebufferTexture2D(FramebufferTarget.DrawFramebuffer, FramebufferAttachment.DepthAttachment, TextureTarget.Texture2D, depth, 0);
            GL.BindBuffer(BufferTarget.PixelUnpackBuffer, pbo); GL.BufferData(BufferTarget.PixelUnpackBuffer, 4, IntPtr.Zero, BufferUsageHint.StreamDraw);
            owner.Ensure(api);
            Check(GL.GetInteger(GetPName.PixelUnpackBufferBinding)==pbo, "AO policy restores foreign unpack PBO");
            GL.BindBuffer(BufferTarget.PixelUnpackBuffer, 0);
            for (int i=0; i<3; ++i) {
                Check(Format(textures[i])==(int)PixelInternalFormat.R8, "visibility format uses R8");
                GL.GetTexParameter(TextureTarget.Texture2D, GetTextureParameter.TextureMinFilter, out int min);
                Check(min==(int)TextureMinFilter.Nearest, "native sampler state retained");
                GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer, fbos[i]);
                Check(GL.CheckFramebufferStatus(FramebufferTarget.DrawFramebuffer)==FramebufferErrorCode.FramebufferComplete, "R8 framebuffer complete");
            }
            GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer, fbos[0]);
            GL.GetFramebufferAttachmentParameter(FramebufferTarget.DrawFramebuffer, FramebufferAttachment.DepthAttachment, FramebufferParameterName.FramebufferAttachmentObjectName, out int attachedDepth);
            Check(attachedDepth==depth, "borrowed depth attachment retained");
            for (int i=0; i<100; ++i) owner.Ensure(api);
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i=0; i<1000; ++i) _ = api.Render.FrameBuffers;
            long proxyAllocation = GC.GetAllocatedBytesForCurrentThread()-before;
            before = GC.GetAllocatedBytesForCurrentThread();
            for (int i=0; i<1000; ++i) owner.Ensure(api);
            // DispatchProxy allocates its invocation arguments; native API property getters do not.
            long allocation = GC.GetAllocatedBytesForCurrentThread()-before-proxyAllocation;
            Check(allocation==0, "warm AO resource allocation excluding fixture proxy="+allocation);
            Allocate(textures[1], 32, 16, PixelInternalFormat.Rgba8); owner.Invalidate(); owner.Ensure(api);
            Check(Format(textures[1])==(int)PixelInternalFormat.R8, "same-size same-ID recreation reapplies validated format");
            Allocate(textures[2], 20, 10, PixelInternalFormat.Rgba8); buffers[15].Width=20; buffers[15].Height=10; owner.Ensure(api);
            Check(Format(textures[2])==(int)PixelInternalFormat.R8, "resize generation reapplies format");
            Allocate(textures[1], 32, 16, PixelInternalFormat.Rgba16f); owner.Invalidate(); owner.Ensure(api);
            Check(Format(textures[1])==(int)PixelInternalFormat.Rgba16f, "unknown high-precision AO resource retained");
            Check(GL.GetError()==ErrorCode.NoError, "AO fixture GL state valid");
            Console.WriteLine("PASS AO R8: attachment/depth/ID/sampler ownership, PBO isolation, recreation, resize, unsupported precision and zero warm allocation");
        }
        finally { GL.BindBuffer(BufferTarget.PixelUnpackBuffer, 0); GL.DeleteBuffer(pbo); GL.DeleteTexture(depth); GL.DeleteTextures(3,textures); GL.DeleteFramebuffers(3,fbos); }
    }
}
