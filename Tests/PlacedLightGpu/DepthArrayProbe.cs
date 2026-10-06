using System;
using DRTAgX;
using System.Linq;
using OpenTK.Graphics.OpenGL4;

internal static class DepthArrayProbe
{
    internal static void Run(string maintained)
    {
        string vertex = """
            #version 430 core
            void main() {
                vec2 p = vec2((gl_VertexID << 1) & 2, gl_VertexID & 2);
                gl_Position = vec4(p * 2.0 - 1.0, 0.0, 1.0);
            }
            """;
        string constants = string.Join("\n", maintained.Split('\n').Where(line => line.StartsWith("const float STATIC_")));
        string fragment = "#version 430 core\nuniform sampler2DArrayShadow drtStaticMaps;\nuniform vec3 q;\nuniform int slot;\nout vec4 color;\n" +
            constants + "\n" + ProbeShader.Function(maintained, "int drtCubeFace(") + "\n" +
            ProbeShader.Function(maintained, "float drtStaticFaceVisibility(") + "\n" +
            ProbeShader.Function(maintained, "float drtStaticVisibilityAtSlot(") +
            "\nvoid main() { color = vec4(drtStaticVisibilityAtSlot(q, vec3(0.0), slot)); }";
        int program = ProbeShader.Program((ShaderType.VertexShader, vertex), (ShaderType.FragmentShader, fragment));
        int depth = GL.GenTexture(), fbo = GL.GenFramebuffer();
        int colorFbo = GL.GetInteger(GetPName.DrawFramebufferBinding);
        int capacity = Math.Min(128, GL.GetInteger(GetPName.MaxArrayTextureLayers) / 6 - 1);
        if (capacity < 1) throw new Exception("No depth-array capacity");
        int active = GL.GetInteger(GetPName.ActiveTexture);
        GL.ActiveTexture(TextureUnit.Texture14);
        int previousTexture = GL.GetInteger(GetPName.TextureBinding2DArray), previousSampler = GL.GetInteger(GetPName.SamplerBinding);
        GL.BindSampler(14, 0);
        GL.BindTexture(TextureTarget.Texture2DArray, depth);
        GL.TexStorage3D(TextureTarget3d.Texture2DArray, 1, SizedInternalFormat.DepthComponent24, StaticTerrainShadowMaps.FaceSize, StaticTerrainShadowMaps.FaceSize, (capacity + 1) * 6);
        GL.TexParameter(TextureTarget.Texture2DArray, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
        GL.TexParameter(TextureTarget.Texture2DArray, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
        GL.TexParameter(TextureTarget.Texture2DArray, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        GL.TexParameter(TextureTarget.Texture2DArray, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
        GL.TexParameter(TextureTarget.Texture2DArray, TextureParameterName.TextureCompareMode, (int)TextureCompareMode.CompareRefToTexture);
        GL.TexParameter(TextureTarget.Texture2DArray, TextureParameterName.TextureCompareFunc, (int)DepthFunction.Lequal);
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, fbo);
        GL.DrawBuffer(DrawBufferMode.None);
        GL.ReadBuffer(ReadBufferMode.None);
        GL.DepthMask(true);
        GL.Disable(EnableCap.ScissorTest);
        GL.Disable(EnableCap.DepthTest);
        GL.UseProgram(program);
        GL.Uniform1(GL.GetUniformLocation(program, "drtStaticMaps"), 14);
        int query = GL.GetUniformLocation(program, "q"), slotLocation = GL.GetUniformLocation(program, "slot");

        void Fill(int source, float distance)
        {
            // Synthetic constant caster depth from the standard OpenGL perspective equation.
            const double near = 0.1, far = 22;
            double projected = far / (far - near) - far * near / ((far - near) * distance);
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, fbo);
            for (int face = 0; face < 6; face++)
            {
                GL.FramebufferTextureLayer(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment, depth, 0, source * 6 + face);
                if (GL.CheckFramebufferStatus(FramebufferTarget.Framebuffer) != FramebufferErrorCode.FramebufferComplete)
                    throw new Exception("Depth array framebuffer incomplete");
                GL.ClearDepth(projected);
                GL.Clear(ClearBufferMask.DepthBufferBit);
            }
        }

        void Sample(int source, float x, float y, float z, float expected, string name)
        {
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, colorFbo);
            GL.Viewport(0, 0, 1, 1);
            GL.Uniform1(slotLocation, source);
            GL.Uniform3(query, x, y, z);
            GL.DrawArrays(PrimitiveType.Triangles, 0, 3);
            float[] pixel = new float[4];
            GL.ReadPixels(0, 0, 1, 1, PixelFormat.Rgba, PixelType.Float, pixel);
            if (Math.Abs(pixel[0] - expected) > 0.0001f) throw new Exception($"{name}: {pixel[0]}, expected {expected}");
        }

        try
        {
            Fill(0, 2);
            Fill(capacity, 4);
            foreach (var d in new[] { (1,0,0), (-1,0,0), (0,1,0), (0,-1,0), (0,0,1), (0,0,-1) })
            {
                Sample(0, d.Item1, d.Item2, d.Item3, 1, "receiver before caster");
                Sample(0, d.Item1 * 3, d.Item2 * 3, d.Item3 * 3, 0, "receiver behind caster");
                Sample(capacity, d.Item1 * 3, d.Item2 * 3, d.Item3 * 3, 1, "highest staging layer is isolated");
            }
            GL.CopyImageSubData(depth, ImageTarget.Texture2DArray, 0, 0, 0, capacity * 6,
                depth, ImageTarget.Texture2DArray, 0, 0, 0, (capacity - 1) * 6, StaticTerrainShadowMaps.FaceSize, StaticTerrainShadowMaps.FaceSize, 6);
            Sample(capacity - 1, 3, 0, 0, 1, "six-layer promotion");
            Sample(0, 3, 0, 0, 0, "promotion retains unrelated resident");
            Sample(0, 3, 3, 0, 0, "shadowed face seam");
            Sample(capacity, 3, 3, 0, 1, "lit face seam");
            if (GL.GetError() != ErrorCode.NoError) throw new Exception("Depth probe GL error");
            Console.WriteLine($"PASS D24 depth array ({capacity} residents): six faces, projected compare, seams, isolated staging and promotion");
        }
        finally
        {
            GL.ClearDepth(1);
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, colorFbo);
            GL.ActiveTexture(TextureUnit.Texture14);
            GL.BindTexture(TextureTarget.Texture2DArray, previousTexture);
            GL.BindSampler(14, previousSampler);
            GL.ActiveTexture((TextureUnit)active);
            GL.DeleteFramebuffer(fbo);
            GL.DeleteTexture(depth);
            GL.DeleteProgram(program);
        }
    }
}
