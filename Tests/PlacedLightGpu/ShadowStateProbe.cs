using System;
using DRTAgX;
using OpenTK.Graphics.OpenGL4;

internal static class ShadowStateProbe
{
    internal static void Run()
    {
        int oldProgram = GL.GetInteger(GetPName.CurrentProgram), oldVao = GL.GetInteger(GetPName.VertexArrayBinding);
        int oldDraw = GL.GetInteger(GetPName.DrawFramebufferBinding), oldRead = GL.GetInteger(GetPName.ReadFramebufferBinding);
        int oldArray = GL.GetInteger(GetPName.ArrayBufferBinding), oldElement = GL.GetInteger(GetPName.ElementArrayBufferBinding);
        int oldGeneric = GL.GetInteger(GetPName.ShaderStorageBufferBinding), oldActive = GL.GetInteger(GetPName.ActiveTexture);
        GL.GetInteger(GetIndexedPName.ShaderStorageBufferBinding, 3, out int old3);
        int[] viewport = new int[4], restored = new int[4];
        GL.GetInteger(GetPName.Viewport, viewport);
        bool depth = GL.IsEnabled(EnableCap.DepthTest), blend = GL.IsEnabled(EnableCap.Blend);
        GL.GetBoolean(GetPName.DepthWritemask, out bool depthMask);
        bool[] color = new bool[4], restoredColor = new bool[4];
        GL.GetBoolean(GetPName.ColorWritemask, color);
        int vao = GL.GenVertexArray(), buffer = GL.GenBuffer();
        var state = new ShadowGlState(capture: false);
        for (int iteration = 0; iteration < 2; iteration++)
        {
            state.Capture();
            GL.BindVertexArray(vao);
            GL.BindBuffer(BufferTarget.ArrayBuffer, buffer);
            GL.BindBuffer(BufferTarget.ElementArrayBuffer, buffer);
            GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 3, buffer);
            GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer, 0);
            GL.BindFramebuffer(FramebufferTarget.ReadFramebuffer, 0);
            GL.UseProgram(0);
            GL.Viewport(2, 3, 7, 11);
            if (depth) GL.Disable(EnableCap.DepthTest); else GL.Enable(EnableCap.DepthTest);
            if (blend) GL.Disable(EnableCap.Blend); else GL.Enable(EnableCap.Blend);
            GL.DepthMask(!depthMask);
            GL.ColorMask(false, false, false, false);
            GL.ActiveTexture(TextureUnit.Texture0);
            GL.BindTexture(TextureTarget.Texture2D, 0);
            state.Dispose();
            GL.GetInteger(GetPName.Viewport, restored);
            GL.GetBoolean(GetPName.ColorWritemask, restoredColor);
            GL.GetInteger(GetIndexedPName.ShaderStorageBufferBinding, 3, out int restored3);
            GL.GetBoolean(GetPName.DepthWritemask, out bool restoredMask);
            if (GL.GetInteger(GetPName.CurrentProgram) != oldProgram || GL.GetInteger(GetPName.VertexArrayBinding) != oldVao ||
                GL.GetInteger(GetPName.DrawFramebufferBinding) != oldDraw || GL.GetInteger(GetPName.ReadFramebufferBinding) != oldRead ||
                GL.GetInteger(GetPName.ArrayBufferBinding) != oldArray || GL.GetInteger(GetPName.ElementArrayBufferBinding) != oldElement ||
                GL.GetInteger(GetPName.ShaderStorageBufferBinding) != oldGeneric || restored3 != old3 ||
                GL.GetInteger(GetPName.ActiveTexture) != oldActive || GL.IsEnabled(EnableCap.DepthTest) != depth ||
                GL.IsEnabled(EnableCap.Blend) != blend || restoredMask != depthMask)
                throw new Exception("Reused shadow state leaked a native binding/capability");
            for (int i = 0; i < 4; i++)
                if (restored[i] != viewport[i] || restoredColor[i] != color[i]) throw new Exception("Reused shadow state leaked viewport/mask");
        }
        GL.DeleteVertexArray(vao);
        GL.DeleteBuffer(buffer);
        if (GL.GetError() != ErrorCode.NoError) throw new Exception("Shadow state probe GL error");
        Console.WriteLine("PASS production shadow state: reusable capture restores native raster state and bindings");
    }
}
