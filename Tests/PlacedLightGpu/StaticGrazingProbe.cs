using System;
using System.Linq;
using DRTAgX;
using OpenTK.Graphics.OpenGL4;

internal static class StaticGrazingProbe
{
    internal static void Run(string maintained)
    {
        using var state = new ShadowGlState();
        // Keep depth visibility controlled while exercising the production
        // receiver math, dead band and refresh blend in a real fragment shader.
        string constants = string.Join("\n", maintained.Split('\n').Where(line => line.StartsWith("const float STATIC_")));
        string fragment = """
            #version 430 core
            uniform mat4 invModelViewMatrix;
            uniform float incidence;
            uniform float oldVisibility;
            uniform float newVisibility;
            uniform float blend;
            out vec4 color;
            float drtStaticVisibilityAtSlot(vec3 q, vec3 gradient, int slot) {
                return slot == 0 ? oldVisibility : newVisibility;
            }
            """ + "\n" + constants + "\n" + ProbeShader.Function(maintained, "float drtStaticVisibility(") + "\n" + """
            void main() {
                vec3 emitter = vec3(sqrt(1.0 - incidence * incidence), incidence, 0.0);
                float visibility = drtStaticVisibility(-emitter, vec3(0.0, 1.0, 0.0), 1.0, -incidence, 0, 1, blend);
                color = vec4(visibility);
            }
            """;
        string vertex = """
            #version 430 core
            void main() {
                vec2 p = vec2((gl_VertexID << 1) & 2, gl_VertexID & 2);
                gl_Position = vec4(p * 2.0 - 1.0, 0.0, 1.0);
            }
            """;
        int program = ProbeShader.Program((ShaderType.VertexShader, vertex), (ShaderType.FragmentShader, fragment));
        int fbo = GL.GenFramebuffer(), texture = GL.GenTexture(), vao = GL.GenVertexArray();
        try
        {
            GL.ActiveTexture(TextureUnit.Texture14);
            GL.BindTexture(TextureTarget.Texture2D, texture);
            GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba32f, 1, 1, 0,
                PixelFormat.Rgba, PixelType.Float, IntPtr.Zero);
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, fbo);
            GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0,
                TextureTarget.Texture2D, texture, 0);
            GL.DrawBuffer(DrawBufferMode.ColorAttachment0);
            GL.ReadBuffer(ReadBufferMode.ColorAttachment0);
            if (GL.CheckFramebufferStatus(FramebufferTarget.Framebuffer) != FramebufferErrorCode.FramebufferComplete)
                throw new Exception("Grazing probe framebuffer incomplete");
            GL.Viewport(0, 0, 1, 1);
            GL.Disable(EnableCap.DepthTest);
            GL.Disable(EnableCap.Blend);
            GL.Disable(EnableCap.ScissorTest);
            GL.ColorMask(true, true, true, true);
            GL.BindVertexArray(vao);
            GL.UseProgram(program);
            float[] identity = { 1,0,0,0, 0,1,0,0, 0,0,1,0, 0,0,0,1 };
            GL.UniformMatrix4(GL.GetUniformLocation(program, "invModelViewMatrix"), 1, false, identity);
            GL.Uniform1(GL.GetUniformLocation(program, "oldVisibility"), 1f);
            GL.Uniform1(GL.GetUniformLocation(program, "newVisibility"), 1f);
            GL.Uniform1(GL.GetUniformLocation(program, "blend"), 0f);
            float[] result = new float[4];
            void Check(float incidence, float expected)
            {
                GL.Uniform1(GL.GetUniformLocation(program, "incidence"), incidence);
                GL.DrawArrays(PrimitiveType.Triangles, 0, 3);
                GL.ReadPixels(0, 0, 1, 1, PixelFormat.Rgba, PixelType.Float, result);
                if (Math.Abs(result[0] - expected) > 0.00001f)
                    throw new Exception($"Grazing visibility at {incidence}: {result[0]}, expected {expected}");
            }
            foreach (float incidence in new[] { -0.01f, -0.000001f, 0f, 0.000001f, 0.001f }) Check(incidence, 0);
            Check(0.0055f, 0.5f);
            Check(0.01f, 1);
            Check(0.2f, 1);
            GL.Uniform1(GL.GetUniformLocation(program, "oldVisibility"), 0.2f);
            GL.Uniform1(GL.GetUniformLocation(program, "newVisibility"), 0.8f);
            GL.Uniform1(GL.GetUniformLocation(program, "blend"), 0.25f);
            Check(0.000001f, 0);
            Check(0.0055f, 0.175f);
            Check(0.2f, 0.35f);
            if (GL.GetError() != ErrorCode.NoError) throw new Exception("Grazing probe GL error");
            Console.WriteLine("PASS static coplanar sign changes stay unlit; smooth grazing ramp preserves depth refresh blending");
        }
        finally
        {
            GL.DeleteVertexArray(vao);
            GL.DeleteFramebuffer(fbo);
            GL.DeleteTexture(texture);
            GL.DeleteProgram(program);
        }
    }
}
