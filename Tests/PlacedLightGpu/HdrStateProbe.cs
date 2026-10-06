using System;
using DRTAgX;
using OpenTK.Graphics.OpenGL4;

/// <summary>Seed every state touched by the HDR guard and measure its reuse.</summary>
internal static class HdrStateProbe
{
    internal static void Run()
    {
        using var original = new HdrPassState();
        int[] textures = new int[5], samplers = new int[5];
        int draw = GL.GenFramebuffer(), read = GL.GenFramebuffer(), vao = GL.GenVertexArray(), unpack = GL.GenBuffer();
        int program = ProbeShader.Program(
            (ShaderType.VertexShader, "#version 430 core\nvoid main(){gl_Position=vec4(0);}"),
            (ShaderType.FragmentShader, "#version 430 core\nout vec4 color;void main(){color=vec4(1);}"));
        try {
            for (int i = 0; i < 5; i++) {
                textures[i] = GL.GenTexture(); samplers[i] = GL.GenSampler();
                GL.ActiveTexture(TextureUnit.Texture0 + i);
                GL.BindTexture(TextureTarget.Texture2D, textures[i]); GL.BindSampler(i, samplers[i]);
            }
            GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer, draw);
            GL.BindFramebuffer(FramebufferTarget.ReadFramebuffer, read);
            GL.UseProgram(program); GL.BindVertexArray(vao);
            GL.BindBuffer(BufferTarget.PixelUnpackBuffer, unpack);
            GL.BufferData(BufferTarget.PixelUnpackBuffer, 4, IntPtr.Zero, BufferUsageHint.StaticDraw);
            GL.ActiveTexture(TextureUnit.Texture14); GL.Viewport(7, 9, 19, 23);
            GL.Enable(EnableCap.DepthTest); GL.Enable(EnableCap.Blend); GL.Disable(EnableCap.CullFace); GL.Enable(EnableCap.ScissorTest);
            GL.DepthMask(true); GL.ColorMask(false, true, false, true);
            var guard = new HdrPassState(false);
            guard.Capture();
            try {
                GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0); GL.UseProgram(0); GL.BindVertexArray(0);
                GL.BindBuffer(BufferTarget.PixelUnpackBuffer, 0); GL.Viewport(0, 0, 1, 1);
                GL.Enable(EnableCap.CullFace);
                for (int i = 0; i < 5; i++) {
                    GL.ActiveTexture(TextureUnit.Texture0 + i); GL.BindTexture(TextureTarget.Texture2D, 0); GL.BindSampler(i, 0);
                }
            }
            finally { guard.Dispose(); }
            AssertBindings();
            // Warm managed/native interop before measuring only Capture/Dispose.
            // Resource creation, assertion arrays and shader adapters are excluded.
            for (int i = 0; i < 32; i++) { guard.Capture(); guard.Dispose(); }
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 1000; i++) { guard.Capture(); guard.Dispose(); }
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            AssertBindings();
            if (allocated != 0) throw new Exception("Reusable HDR guard allocated " + allocated + " bytes");
            if (GL.GetError() != ErrorCode.NoError) throw new Exception("HDR guard GL error");
            Console.WriteLine("PASS HDR guard: 1000 captures/restores allocate 0 bytes; framebuffer/program/VAO/PBO/viewport/masks/caps and all five texture/sampler pairs restored");

            void AssertBindings() {
                if (GL.GetInteger(GetPName.DrawFramebufferBinding) != draw || GL.GetInteger(GetPName.ReadFramebufferBinding) != read ||
                    GL.GetInteger(GetPName.CurrentProgram) != program || GL.GetInteger(GetPName.VertexArrayBinding) != vao ||
                    GL.GetInteger(GetPName.PixelUnpackBufferBinding) != unpack || GL.GetInteger(GetPName.ActiveTexture) != (int)TextureUnit.Texture14)
                    throw new Exception("HDR guard did not restore entry bindings");
                int[] viewport = new int[4]; bool[] mask = new bool[4];
                GL.GetInteger(GetPName.Viewport, viewport); GL.GetBoolean(GetPName.ColorWritemask, mask);
                if (viewport[0] != 7 || viewport[1] != 9 || viewport[2] != 19 || viewport[3] != 23 ||
                    mask[0] || !mask[1] || mask[2] || !mask[3] || !GL.GetBoolean(GetPName.DepthWritemask) ||
                    !GL.IsEnabled(EnableCap.DepthTest) || !GL.IsEnabled(EnableCap.Blend) || GL.IsEnabled(EnableCap.CullFace) || !GL.IsEnabled(EnableCap.ScissorTest))
                    throw new Exception("HDR guard did not restore entry raster state");
                for (int i = 0; i < 5; i++) {
                    GL.ActiveTexture(TextureUnit.Texture0 + i);
                    if (GL.GetInteger(GetPName.TextureBinding2D) != textures[i] || GL.GetInteger(GetPName.SamplerBinding) != samplers[i])
                        throw new Exception("HDR guard did not restore texture/sampler " + i);
                }
                GL.ActiveTexture(TextureUnit.Texture14);
            }
        }
        finally {
            foreach (int texture in textures) GL.DeleteTexture(texture);
            foreach (int sampler in samplers) GL.DeleteSampler(sampler);
            GL.UseProgram(0); GL.DeleteProgram(program); GL.DeleteVertexArray(vao); GL.DeleteBuffer(unpack);
            GL.DeleteFramebuffer(draw); GL.DeleteFramebuffer(read);
        }
    }
}
