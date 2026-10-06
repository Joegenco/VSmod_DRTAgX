using System;
using System.IO;
using DRTAgX;
using OpenTK.Graphics.OpenGL4;

// Exercise maintained production GLSL against a real GL context. These probes
// protect HDR identity, native filtering and the atmosphere-to-shaft handoff.
internal static class VolumetricFogProbe
{
    internal static void Run(string deferredPath)
    {
        using var state = new ShadowGlState();
        string assets = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(deferredPath)!, "../.."));
        string composite = File.ReadAllText(Path.Combine(assets, "sheydermod/shaders/vfscatter_composite.fsh"));
        string final = File.ReadAllText(Path.Combine(assets, "game/shaders/final.fsh"));
        int begin = final.IndexOf("// >>> SheyderMod: VolumetricFog composite", StringComparison.Ordinal);
        string block = final[begin..final.IndexOf("// <<< SheyderMod", begin, StringComparison.Ordinal)];
        string vertex = "#version 430 core\nout vec2 texcoord;void main(){vec2 p=vec2((gl_VertexID<<1)&2,gl_VertexID&2);texcoord=p;gl_Position=vec4(p*2.-1.,0,1);}";
        int finalProgram = ProbeShader.Program((ShaderType.VertexShader, vertex), (ShaderType.FragmentShader,
            "#version 430 core\nin vec2 texcoord;out vec4 outColor;uniform vec4 scene,drtAtmosphereScreen;\n" + composite +
            "\nvoid main(){vec4 color=scene;vec2 texCoord=texcoord;\n" + block + "\noutColor=color;}"));
        string scatterSource = ProbeShader.DeferredSource(Path.Combine(assets, "sheydermod/shaders/vfscatter.fsh"));
        int scatterProgram = ProbeShader.Program((ShaderType.VertexShader, vertex), (ShaderType.FragmentShader, scatterSource));
        int[] units = { 1, 6, 7, 12, 13 }, textures = new int[5], samplers = new int[5];
        for (int i = 0; i < units.Length; ++i)
        {
            GL.ActiveTexture(TextureUnit.Texture0 + units[i]);
            textures[i] = GL.GetInteger(GetPName.TextureBinding2D);
            samplers[i] = GL.GetInteger(GetPName.SamplerBinding);
            GL.BindSampler(units[i], 0);
        }
        GL.GetInteger(GetIndexedPName.UniformBufferBinding, 10, out int oldUbo);
        int genericUbo = GL.GetInteger(GetPName.UniformBufferBinding);
        int output = GL.GenTexture(), scatter = GL.GenTexture(), depth = GL.GenTexture(), shadow = GL.GenTexture();
        int previous = GL.GenTexture(), current = GL.GenTexture(), fbo = GL.GenFramebuffer(), vao = GL.GenVertexArray(), ubo = GL.GenBuffer();
        float[] scene = { 2.4f, 1.2f, .6f, .37f }, pixel = new float[4];
        void Texture(int unit, int texture, float[] rgba)
        {
            GL.ActiveTexture(TextureUnit.Texture0 + unit); GL.BindTexture(TextureTarget.Texture2D, texture);
            float[] data = new float[4 * 4 * 4];
            for (int i = 0; i < data.Length; i++) data[i] = rgba[i % 4];
            GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba32f, 4, 4, 0, PixelFormat.Rgba, PixelType.Float, data);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.LinearMipmapLinear);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
            GL.GenerateMipmap(GenerateMipmapTarget.Texture2D);
        }
        void Read() { GL.DrawArrays(PrimitiveType.Triangles, 0, 3); GL.ReadPixels(0, 0, 1, 1, PixelFormat.Rgba, PixelType.Float, pixel); }
        void Equal(float[] expected, string label)
        {
            for (int c = 0; c < 4; c++)
                if (!float.IsFinite(pixel[c]) || Math.Abs(pixel[c] - expected[c]) > 2e-5f)
                    throw new Exception($"{label}: channel {c}, {pixel[c]} vs {expected[c]}");
            Console.WriteLine("PASS " + label);
        }
        try
        {
            GL.ActiveTexture(TextureUnit.Texture0); GL.BindTexture(TextureTarget.Texture2D, output);
            GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba32f, 1, 1, 0, PixelFormat.Rgba, PixelType.Float, IntPtr.Zero);
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, fbo);
            GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, output, 0);
            GL.DrawBuffer(DrawBufferMode.ColorAttachment0); GL.ReadBuffer(ReadBufferMode.ColorAttachment0);
            if (GL.CheckFramebufferStatus(FramebufferTarget.Framebuffer) != FramebufferErrorCode.FramebufferComplete) throw new Exception("Fog probe framebuffer");
            GL.BindVertexArray(vao); GL.Viewport(0, 0, 1, 1); GL.Disable(EnableCap.DepthTest); GL.Disable(EnableCap.Blend);
            GL.Disable(EnableCap.ScissorTest); GL.ColorMask(true, true, true, true);
            Texture(6, scatter, new[] { .8f, .6f, .4f, 1 }); Texture(7, depth, new[] { .5f, 0, 0, 1 });
            GL.UseProgram(finalProgram);
            GL.Uniform1(GL.GetUniformLocation(finalProgram, "vfScatterTex"), 6); GL.Uniform1(GL.GetUniformLocation(finalProgram, "vfDepthTex"), 7);
            GL.Uniform4(GL.GetUniformLocation(finalProgram, "scene"), scene[0], scene[1], scene[2], scene[3]);
            GL.Uniform1(GL.GetUniformLocation(finalProgram, "vf_Enabled"), 1f);
            foreach (float blur in new[] { 0f, 1f, 5f, 8f })
            {
                GL.Uniform1(GL.GetUniformLocation(finalProgram, "vf_BlurRadius"), blur); Read();
                Equal(new[] { 3.2f, 1.8f, 1f, .37f }, $"volumetric HDR additive/no shaping, native blur={blur}");
            }
            GL.Uniform1(GL.GetUniformLocation(finalProgram, "vf_Enabled"), 0f); Read(); Equal(scene, "disabled volumetric fog preserves HDR scene/alpha");
            GL.Uniform1(GL.GetUniformLocation(finalProgram, "vf_Enabled"), 1f);
            GL.Uniform4(GL.GetUniformLocation(finalProgram, "drtAtmosphereScreen"), 1f, 1f, 1f, 0f); Read(); Equal(scene, "owned-volume gate prevents duplicate fog");
            GL.Uniform4(GL.GetUniformLocation(finalProgram, "drtAtmosphereScreen"), 1f, 1f, 0f, 0f);
            Texture(6, scatter, new[] { 0f, 0, 0, 1 }); Read(); Equal(scene, "enabled zero scatter preserves HDR highlights");
            Texture(6, scatter, new[] { .8f, .6f, .4f, 0 }); Read(); Equal(scene, "unmarched scatter alpha preserves scene");

            // A real, fully lit march of 20 blocks: native onset/density/steps
            // remain unchanged. The LUT blend/exposure is the only color input.
            Texture(12, previous, new[] { .8f, .6f, .4f, 1 }); Texture(13, current, new[] { 1.6f, 1.2f, .8f, 1 });
            GL.ActiveTexture(TextureUnit.Texture1); GL.BindTexture(TextureTarget.Texture2D, shadow);
            GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.DepthComponent32f, 1, 1, 0, PixelFormat.DepthComponent, PixelType.Float, new[] { 1f });
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureCompareMode, (int)TextureCompareMode.CompareRefToTexture);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureCompareFunc, (int)DepthFunction.Lequal);
            GL.UseProgram(scatterProgram);
            GL.Uniform1(GL.GetUniformLocation(scatterProgram, "depthTexture"), 7); GL.Uniform1(GL.GetUniformLocation(scatterProgram, "shadowMapFar"), 1);
            GL.Uniform1(GL.GetUniformLocation(scatterProgram, "drtSkyViewPrevious"), 12); GL.Uniform1(GL.GetUniformLocation(scatterProgram, "drtSkyViewCurrent"), 13);
            float[] inverse = { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 20, 1 };
            float[] shadowSpace = { .0078125f, 0, 0, 0, 0, .0078125f, 0, 0, 0, 0, .0078125f, 0, 0, 0, 0, 1 };
            GL.UniformMatrix4(GL.GetUniformLocation(scatterProgram, "vf_InvViewProj"), 1, false, inverse);
            GL.UniformMatrix4(GL.GetUniformLocation(scatterProgram, "vf_ToShadowSpaceFar"), 1, false, shadowSpace);
            GL.Uniform3(GL.GetUniformLocation(scatterProgram, "vf_ShadowRayStart"), .5f, .5f, .2f);
            GL.Uniform1(GL.GetUniformLocation(scatterProgram, "vf_InvSteps"), 1f / 12); GL.Uniform1(GL.GetUniformLocation(scatterProgram, "vf_Intensity"), .2f);
            float[] frame = new float[AtmosphereRenderer.FrameFloatCount]; frame[11] = .25f; frame[13] = 1; frame[24] = 1; frame[27] = 1;
            GL.UniformBlockBinding(scatterProgram, GL.GetUniformBlockIndex(scatterProgram, "DrtAtmosphere"), 10);
            GL.BindBuffer(BufferTarget.UniformBuffer, ubo); GL.BindBufferBase(BufferRangeTarget.UniformBuffer, 10, ubo);
            void Upload() => GL.BufferData(BufferTarget.UniformBuffer, frame.Length * 4, frame, BufferUsageHint.DynamicDraw);
            Upload(); Read(); Equal(new[] { 1.08f, .81f, .54f, 1 }, "real Sheyder march inherits HDR daytime halo and LUT history blend");
            // Shaft visibility follows each ray rather than camera-cell sunlight.
            // Shared weather shelter and Sheyder's native activation remain separate.
            frame[119] = 1; frame[117] = 0; Upload(); Read();
            Equal(new[] { 1.08f, .81f, .54f, 1 }, "camera sunlight zero does not suppress ray-visible shafts");
            frame[117] = .25f; Upload(); Read();
            Equal(new[] { 1.08f, .81f, .54f, 1 }, "partial camera shelter preserves ray-visible shafts");
            frame[117] = 1; Upload(); Read();
            Equal(new[] { 1.08f, .81f, .54f, 1 }, "fully exposed camera retains existing shafts");
            Texture(12, previous, new[] { 2f, .4f, .1f, 1 }); Texture(13, current, new[] { 2f, .4f, .1f, 1 });
            frame[13] = 0; Upload(); Read();
            float t = MathF.Sin(6 * MathF.PI / 180) / (MathF.Sin(6 * MathF.PI / 180) + MathF.Sin(3 * MathF.PI / 180));
            float exposure = .5f + 2.5f * t * t * (3 - 2 * t);
            Equal(new[] { 2 * exposure * .18f, .4f * exposure * .18f, .1f * exposure * .18f, 1 }, "real Sheyder march inherits warm twilight color/exposure without daytime gain");
            frame[24] = 0; Upload(); GL.Uniform3(GL.GetUniformLocation(scatterProgram, "vf_SunDir"), 0f, 0f, 1f); Read();
            Equal(new[] { .27f, .2295f, .162f, 1 }, "native volumetric color fallback when sky LUT unavailable");

            // Real far-plane reconstruction must produce a valid, bounded march
            // even though the sky and merged OIT clouds have no terrain receiver.
            const float near = .3f;
            void SkyProjection(float far) {
                float pa = -(far + near) / (far - near), pb = -2 * far * near / (far - near);
                float[] projectionInverse = { 1,0,0,0, 0,1,0,0, 0,0,0,1/pb, 0,0,-1,pa/pb };
                GL.UniformMatrix4(GL.GetUniformLocation(scatterProgram, "vf_InvViewProj"), 1, false, projectionInverse);
            }
            Texture(7, depth, new[] { 1f, 0, 0, 1 });
            GL.Uniform3(GL.GetUniformLocation(scatterProgram, "vf_SunDir"), 0f, 0f, -1f);
            GL.Uniform3(GL.GetUniformLocation(scatterProgram, "vf_ShadowRayStart"), .5f, .5f, .75f);
            GL.Uniform1(GL.GetUniformLocation(scatterProgram, "vf_MaxRange"), 80f);
            GL.Uniform1(GL.GetUniformLocation(scatterProgram, "vf_DistantDensity"), 2f);
            float[] skyScatter = { .54f, .459f, .324f, 1 };
            foreach (float far in new[] { 1500f, 50000f }) {
                SkyProjection(far); Read(); Equal(skyScatter, $"sky ray at far plane {far} has bounded fog/density and valid alpha");
            }
            // A real liquid receiver still clips the sky ray before the onset.
            SkyProjection(1500);
            float liquidPa = -(1500 + near) / (1500 - near), liquidPb = -2 * 1500 * near / (1500 - near);
            float liquidDepth = (-liquidPa + liquidPb / 4 + 1) * .5f;
            Texture(12, previous, new[] { liquidDepth, 0f, 0, 1 });
            GL.Uniform1(GL.GetUniformLocation(scatterProgram, "vf_LiquidDepth"), 12);
            GL.Uniform1(GL.GetUniformLocation(scatterProgram, "vf_HasWaterDepth"), 1f);
            Read(); Equal(new[] { 0f, 0, 0, 1 }, "near liquid clips background shafts before native onset");
            GL.Uniform1(GL.GetUniformLocation(scatterProgram, "vf_HasWaterDepth"), 0f);

            // Use the actual generated scatter, then composite two completed
            // backgrounds: clear sky and a cloud already merged with that sky.
            Read(); Texture(6, scatter, (float[])pixel.Clone());
            GL.UseProgram(finalProgram);
            foreach (float[] background in new[] { new[] { .1f,.3f,.8f,1 }, new[] { .65f,.6f,.55f,.42f } }) {
                GL.Uniform4(GL.GetUniformLocation(finalProgram, "scene"), background[0], background[1], background[2], background[3]);
                Read(); Equal(new[] { background[0]+skyScatter[0], background[1]+skyScatter[1], background[2]+skyScatter[2], background[3] },
                    "generated VF adds over completed sky/cloud scene with preserved alpha");
            }
            GL.UseProgram(scatterProgram);
            GL.Uniform1(GL.GetUniformLocation(scatterProgram, "vf_Intensity"), 0f); Read(); Equal(new[] { 0f, 0, 0, 1 }, "native intensity zero produces no shafts");
            GL.Uniform1(GL.GetUniformLocation(scatterProgram, "vf_Intensity"), .2f);
            GL.ActiveTexture(TextureUnit.Texture1); GL.BindTexture(TextureTarget.Texture2D, shadow);
            GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.DepthComponent32f, 1, 1, 0, PixelFormat.DepthComponent, PixelType.Float, new[] { 0f });
            Read(); Equal(new[] { 0f, 0, 0, 1 }, "native far-CSM occlusion still removes blocked shafts");
            if (GL.GetError() != ErrorCode.NoError) throw new Exception("Volumetric fog probe GL error");
        }
        finally
        {
            for (int i = 0; i < units.Length; ++i)
            {
                GL.ActiveTexture(TextureUnit.Texture0 + units[i]); GL.BindTexture(TextureTarget.Texture2D, textures[i]); GL.BindSampler(units[i], samplers[i]);
            }
            GL.BindBufferBase(BufferRangeTarget.UniformBuffer, 10, oldUbo); GL.BindBuffer(BufferTarget.UniformBuffer, genericUbo);
            GL.DeleteBuffer(ubo); GL.DeleteVertexArray(vao); GL.DeleteFramebuffer(fbo);
            foreach (int texture in new[] { output, scatter, depth, shadow, previous, current }) GL.DeleteTexture(texture);
            GL.DeleteProgram(finalProgram); GL.DeleteProgram(scatterProgram);
        }
    }
}
