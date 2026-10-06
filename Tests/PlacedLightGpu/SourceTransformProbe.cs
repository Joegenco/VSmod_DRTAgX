using System;
using DRTAgX;
using OpenTK.Graphics.OpenGL4;
using Vintagestory.API.MathTools;

internal static class SourceTransformProbe
{
    internal static void Run()
    {
        using var state = new ShadowGlState();
        const int count = 257;
        int program = ProbeShader.Program((ShaderType.VertexShader,
            "#version 430 core\nvoid main(){vec2 p=vec2((gl_VertexID<<1)&2,gl_VertexID&2);gl_Position=vec4(p*2-1,0,1);}"),
            (ShaderType.FragmentShader, """
            #version 430 core
            layout(std430,binding=4) readonly buffer Samples { vec4 samples[]; };
            uniform mat4 inverseView;
            uniform bool prepared;
            out vec4 color;
            void main(){
                int i=int(gl_FragCoord.x)*4;
                vec3 q=prepared ? mat3(inverseView)*samples[i+2].xyz-samples[i+1].xyz :
                    mat3(inverseView)*(samples[i+2].xyz-samples[i].xyz);
                color=vec4(q,all(lessThan(abs(q-vec3(0,0,0.003)),vec3(0.5))) ? 1 : 0);
            }
            """));
        int buffer = GL.GenBuffer(), fbo = GL.GenFramebuffer(), texture = GL.GenTexture(), vao = GL.GenVertexArray();
        try
        {
            GL.ActiveTexture(TextureUnit.Texture0); GL.BindTexture(TextureTarget.Texture2D, texture);
            GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba32f, count, 1, 0, PixelFormat.Rgba, PixelType.Float, IntPtr.Zero);
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, fbo);
            GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, texture, 0);
            GL.DrawBuffer(DrawBufferMode.ColorAttachment0); GL.ReadBuffer(ReadBufferMode.ColorAttachment0);
            GL.Viewport(0, 0, count, 1); GL.BindVertexArray(vao); GL.Disable(EnableCap.DepthTest); GL.Disable(EnableCap.Blend); GL.Disable(EnableCap.ScissorTest);
            GL.UseProgram(program);
            var random = new Random(1815);
            float maxError = 0;
            foreach (double origin in new[] { 0.0, 529000.0, 10000000.0 })
            foreach (float yaw in new[] { 0f, 0.9f, 2.8f })
            {
                float[] view = Mat4f.Create(), inverse = new float[16];
                Mat4f.RotateX(view, view, 0.31f); Mat4f.RotateY(view, view, yaw); Mat4f.RotateZ(view, view, -0.15f);
                // Match the full inversion used by native deferred lighting;
                // a transposed rotation would amplify tiny matrix errors.
                Mat4f.Invert(inverse, view);
                float[] data = new float[count * 16];
                for (int i = 0; i < count; ++i)
                {
                    double[] source = { origin + random.Next(-100, 101) + 0.5, origin + random.Next(-80, 81) + 0.3, origin + random.Next(-100, 101) + 0.5 };
                    double[] receiver = { source[0], source[1], source[2] };
                    receiver[0] += i % 3 == 0 ? 0.4999 : i % 3 == 1 ? 0.5001 : random.NextDouble() * 20 - 10;
                    receiver[1] += i % 3 == 2 ? random.NextDouble() * 20 - 10 : 0.2;
                    receiver[2] += i % 3 == 2 ? random.NextDouble() * 20 - 10 : 0.4;
                    for (int c = 0; c < 3; ++c)
                    {
                        // Full-world view translation stays double, as in
                        // StaticLightTileBindings. Cast only final eye positions.
                        double translation = -origin * (view[c] + (double)view[4 + c] + view[8 + c]);
                        data[i * 16 + c] = (float)(view[c] * source[0] + view[4 + c] * source[1] + view[8 + c] * source[2] + translation);
                        data[i * 16 + 8 + c] = (float)(view[c] * receiver[0] + view[4 + c] * receiver[1] + view[8 + c] * receiver[2] + translation);
                    }
                    StaticLightGpuRecord.RotateEye(data, i * 16 + 4, data[i * 16], data[i * 16 + 1], data[i * 16 + 2], inverse);
                }
                GL.BindBuffer(BufferTarget.ShaderStorageBuffer, buffer);
                GL.BufferData(BufferTarget.ShaderStorageBuffer, data.Length * 4, data, BufferUsageHint.StaticDraw);
                GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 4, buffer);
                GL.UniformMatrix4(GL.GetUniformLocation(program, "inverseView"), 1, false, inverse);
                float[][] result = { new float[count * 4], new float[count * 4] };
                for (int variant = 0; variant < 2; ++variant)
                {
                    GL.Uniform1(GL.GetUniformLocation(program, "prepared"), variant);
                    GL.DrawArrays(PrimitiveType.Triangles, 0, 3);
                    GL.ReadPixels(0, 0, count, 1, PixelFormat.Rgba, PixelType.Float, result[variant]);
                }
                for (int i = 0; i < count; ++i)
                {
                    for (int c = 0; c < 3; ++c) maxError = Math.Max(maxError, Math.Abs(result[0][i * 4 + c] - result[1][i * 4 + c]));
                    if (result[0][i * 4 + 3] != result[1][i * 4 + 3]) throw new Exception("Prepared transform changed owning voxel");
                }
            }
            if (maxError > 0.00005f || GL.GetError() != ErrorCode.NoError) throw new Exception("Prepared camera-relative transform error: " + maxError);
            Console.WriteLine($"PASS prepared source transform: rotated cameras, world origins up to 10M, voxel boundaries; max delta {maxError:G3} blocks");
        }
        finally { GL.DeleteProgram(program); GL.DeleteBuffer(buffer); GL.DeleteFramebuffer(fbo); GL.DeleteTexture(texture); GL.DeleteVertexArray(vao); }
    }
}
