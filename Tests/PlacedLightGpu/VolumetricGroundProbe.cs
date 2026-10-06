using System;
using System.Collections.Generic;
using System.IO;
using DRTAgX;
using OpenTK.Graphics.OpenGL4;

// Generate the real atmosphere LUT and compare the maintained fog sampler with
// the previous direct lookup. This reproduces the horizon halo's ground stripe.
internal static class VolumetricGroundProbe
{
    internal static void Run(string deferredPath)
    {
        using var state = new ShadowGlState();
        // The production compute guard also restores image 0 and pixel-buffer
        // bindings; the raster guard alone does not cover these LUT dispatches.
        using var computeState = new AtmosphereComputeState();
        computeState.Capture();
        string directory = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(deferredPath)!, "../../drtagx/shaders/atmosphere"));
        var seen = new HashSet<string>();
        string Expand(string name)
        {
            if (!seen.Add(name)) return "";
            var lines = File.ReadAllLines(Path.Combine(directory, name));
            for (int i = 0; i < lines.Length; i++)
                if (lines[i].Trim().StartsWith("#include ")) lines[i] = Expand(lines[i].Trim()[9..]);
            return string.Join('\n', lines);
        }
        string helpers = Expand("drtagx_atmosphere_sampling.fsh") + Expand("drtagx_volumetric_radiance.fsh");
        string scatter = File.ReadAllText(Path.GetFullPath(Path.Combine(directory, "../../../sheydermod/shaders/vfscatter.fsh")));
        helpers += "\nconst vec3 vf_SunDir=vec3(1,0,0),vf_FrontColor=vec3(1),vf_BackColor=vec3(0);\n" +
            ProbeShader.Function(scatter, "vec3 drtVolumetricSunRadiance(");
        int program = ProbeShader.Program((ShaderType.VertexShader, "#version 430 core\nvoid main(){gl_Position=vec4(gl_VertexID==1?3:-1,gl_VertexID==2?3:-1,0,1);}"),
            (ShaderType.FragmentShader, "#version 430 core\n" + helpers + """
            uniform vec3 ray;
            layout(location=0) out vec4 corrected;
            layout(location=1) out vec4 repeated;
            void main(){vec3 d=normalize(ray);corrected=vec4(drtVolumetricSunRadiance(d),1);repeated=vec4(drtClearSkyRadiance(d),1);}
            """));
        var api = ProbeAssets.AtmosphereApi(directory);
        int trans = AtmosphereCompute.Load(api, "drtagx_atmosphere_transmittance");
        int multiple = AtmosphereCompute.Load(api, "drtagx_atmosphere_multiscatter");
        int sky = AtmosphereCompute.Load(api, "drtagx_atmosphere_skyview");
        GL.ActiveTexture(TextureUnit.Texture1); int old1 = GL.GetInteger(GetPName.TextureBinding2D), sampler1 = GL.GetInteger(GetPName.SamplerBinding); GL.BindSampler(1, 0);
        GL.ActiveTexture(TextureUnit.Texture0);
        int tt = AtmosphereCompute.Texture(256, 64), mt = AtmosphereCompute.Texture(32, 32), lut = AtmosphereCompute.Texture(192, 108);
        int a = GL.GenTexture(), b = GL.GenTexture(), fbo = GL.GenFramebuffer(), vao = GL.GenVertexArray(), ubo = GL.GenBuffer();
        GL.GetInteger(GetIndexedPName.UniformBufferBinding, 10, out int oldUbo);
        int genericUbo = GL.GetInteger(GetPName.UniformBufferBinding);
        float[] frame = new float[AtmosphereRenderer.FrameFloatCount], actual = new float[4], old = new float[4];
        frame[24] = 1; frame[27] = 1; frame[108] = frame[109] = frame[110] = 1; frame[111] = .35f;
        static float Y(float[] rgb) => rgb[0] * .2126f + rgb[1] * .7152f + rgb[2] * .0722f;
        static float Radians(float degrees) => degrees * MathF.PI / 180;
        void Read(float azimuth, float elevation)
        {
            float c = MathF.Cos(Radians(elevation)), az = Radians(azimuth);
            GL.UseProgram(program); GL.ActiveTexture(TextureUnit.Texture0); GL.BindTexture(TextureTarget.Texture2D, lut);
            GL.Uniform3(GL.GetUniformLocation(program, "ray"), c * MathF.Cos(az), MathF.Sin(Radians(elevation)), c * MathF.Sin(az));
            GL.DrawArrays(PrimitiveType.Triangles, 0, 3);
            GL.ReadBuffer(ReadBufferMode.ColorAttachment0); GL.ReadPixels(0, 0, 1, 1, PixelFormat.Rgba, PixelType.Float, actual);
            GL.ReadBuffer(ReadBufferMode.ColorAttachment1); GL.ReadPixels(0, 0, 1, 1, PixelFormat.Rgba, PixelType.Float, old);
            for (int i = 0; i < 4; ++i) if (!float.IsFinite(actual[i]) || actual[i] < 0) throw new Exception("Ground fog produced invalid radiance");
        }
        try
        {
            GL.ActiveTexture(TextureUnit.Texture0);
            foreach (int texture in new[] { a, b })
            {
                GL.BindTexture(TextureTarget.Texture2D, texture);
                GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba32f, 1, 1, 0, PixelFormat.Rgba, PixelType.Float, IntPtr.Zero);
            }
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, fbo);
            GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, a, 0);
            GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment1, TextureTarget.Texture2D, b, 0);
            GL.DrawBuffers(2, new[] { DrawBuffersEnum.ColorAttachment0, DrawBuffersEnum.ColorAttachment1 });
            if (GL.CheckFramebufferStatus(FramebufferTarget.Framebuffer) != FramebufferErrorCode.FramebufferComplete) throw new Exception("Ground fog probe framebuffer");
            GL.BindVertexArray(vao); GL.Viewport(0, 0, 1, 1); GL.Disable(EnableCap.DepthTest); GL.Disable(EnableCap.Blend); GL.Disable(EnableCap.ScissorTest); GL.ColorMask(true, true, true, true);
            GL.UniformBlockBinding(program, GL.GetUniformBlockIndex(program, "DrtAtmosphere"), 10);
            GL.UseProgram(program); GL.Uniform1(GL.GetUniformLocation(program, "drtSkyViewPrevious"), 0); GL.Uniform1(GL.GetUniformLocation(program, "drtSkyViewCurrent"), 0);
            GL.BindBuffer(BufferTarget.UniformBuffer, ubo); GL.BindBufferBase(BufferRangeTarget.UniformBuffer, 10, ubo);
            AtmosphereCompute.Dispatch2D(trans, tt, 256, 64);
            GL.ActiveTexture(TextureUnit.Texture0); GL.BindTexture(TextureTarget.Texture2D, tt);
            GL.UseProgram(multiple); GL.Uniform1(GL.GetUniformLocation(multiple, "transmittanceTable"), 0); AtmosphereCompute.Dispatch2D(multiple, mt, 32, 32);
            foreach (float azimuth in new[] { 0f, 90f, 179.9f, -179.9f })
            {
                float elevation = 5, az = Radians(azimuth);
                frame[12] = MathF.Cos(az) * MathF.Cos(Radians(elevation)); frame[13] = MathF.Sin(Radians(elevation)); frame[14] = MathF.Sin(az) * MathF.Cos(Radians(elevation));
                frame[15] = 1;
                GL.ActiveTexture(TextureUnit.Texture0); GL.BindTexture(TextureTarget.Texture2D, tt);
                GL.ActiveTexture(TextureUnit.Texture1); GL.BindTexture(TextureTarget.Texture2D, mt);
                GL.UseProgram(sky); GL.Uniform1(GL.GetUniformLocation(sky, "transmittanceTable"), 0); GL.Uniform1(GL.GetUniformLocation(sky, "multiscatterTable"), 1);
                GL.Uniform4(GL.GetUniformLocation(sky, "sunDirection"), frame[12], frame[13], frame[14], 1f);
                GL.Uniform4(GL.GetUniformLocation(sky, "moonDirection"), 0f, -1f, 0f, 0f); GL.Uniform1(GL.GetUniformLocation(sky, "cameraAltitude"), 1f);
                AtmosphereCompute.Dispatch2D(sky, lut, 192, 108); GL.MemoryBarrier(MemoryBarrierFlags.TextureFetchBarrierBit);
                GL.ActiveTexture(TextureUnit.Texture0); GL.BindTexture(TextureTarget.Texture2D, lut); GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.Repeat);
                GL.BindBuffer(BufferTarget.UniformBuffer, ubo); GL.BufferData(BufferTarget.UniformBuffer, frame.Length * 4, frame, BufferUsageHint.DynamicDraw);
                Read(azimuth, 20); for (int c = 0; c < 3; c++) if (actual[c] != old[c]) throw new Exception("Shaft color outside the horizon band changed");
                Read(azimuth, 0); float oldHorizon=Y(old),softHorizon=Y(actual);
                if(softHorizon>=oldHorizon*.6f)throw new Exception("Strong planetary horizon color still painted into local fog");
                Console.WriteLine($"PASS reduced fog horizon color, azimuth={azimuth}: sky={oldHorizon:F4}, fog={softHorizon:F4}");
                Read(azimuth, -5); float nearOld = Y(old), nearActual = Y(actual);
                Read(azimuth, -45); float farOld = Y(old), farActual = Y(actual);
                if (Math.Abs(nearOld - farOld) > 1e-5f) throw new Exception("Fixture did not reproduce repeated horizon streak");
                if (farActual >= farOld * .35f || farActual >= nearActual) throw new Exception("Solar halo still extends vertically across ground");
                Console.WriteLine($"PASS real LUT ground halo, azimuth={azimuth}: old -5/-45={nearOld:F4}/{farOld:F4}; corrected={nearActual:F4}/{farActual:F4}");
                Read(azimuth, 0); float horizonY = Y(actual); Read(azimuth, -.001f);
                if (Math.Abs(Y(actual) - horizonY) > .001f) throw new Exception("Fog correction jumps at horizon");
                Read(azimuth + 180, -45); for (int c = 0; c < 3; c++) if (actual[c] != old[c]) throw new Exception("Off-sun diffuse fog changed");
                Read(azimuth, -90);
                Console.WriteLine("PASS colors outside horizon band preserved; horizon continuous and nadir finite");
            }
            if (GL.GetError() != ErrorCode.NoError) throw new Exception("Ground fog probe GL error");
        }
        finally
        {
            GL.ActiveTexture(TextureUnit.Texture1); GL.BindTexture(TextureTarget.Texture2D, old1); GL.BindSampler(1, sampler1);
            GL.BindBufferBase(BufferRangeTarget.UniformBuffer, 10, oldUbo); GL.BindBuffer(BufferTarget.UniformBuffer, genericUbo);
            foreach (int texture in new[] { tt, mt, lut, a, b }) GL.DeleteTexture(texture);
            foreach (int shader in new[] { trans, multiple, sky, program }) GL.DeleteProgram(shader);
            GL.DeleteBuffer(ubo); GL.DeleteFramebuffer(fbo); GL.DeleteVertexArray(vao);
        }
    }
}
