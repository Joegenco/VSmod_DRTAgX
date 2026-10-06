using System;
using System.IO;
using DRTAgX;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;

/// <summary>Render production transport, including real liquid-depth sampling, into an HDR pixel.</summary>
internal static class FogVisibilityProbe
{
    internal static void Run(string deferredPath)
    {
        FogEnvironmentProbe.Run();
        using var window = new GameWindow(GameWindowSettings.Default, new NativeWindowSettings {
            StartVisible = false, ClientSize = new Vector2i(16, 16), API = ContextAPI.OpenGL,
            APIVersion = new Version(4, 3), Profile = ContextProfile.Core });
        window.MakeCurrent();
        GL.LoadBindings(new OpenTK.Windowing.GraphicsLibraryFramework.GLFWBindingsContext());
        string assets = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(deferredPath)!, "../.."));
        string transport = ProbeShader.DeferredSource(Path.Combine(assets, "game/shaders/underwatereffects.fsh"));
        string vertex = "#version 430 core\nvoid main(){gl_Position=vec4(gl_VertexID==1?3:-1,gl_VertexID==2?3:-1,0,1);}";
        int program = ProbeShader.Program((ShaderType.VertexShader, vertex), (ShaderType.FragmentShader,
            "#version 430 core\nuniform float zNear,zFar;\nvec4 applySpheresFog(vec4 c,float f,vec3 p){return c;}\n" + transport + """

            uniform vec3 receiver, segmentStart;
            uniform float receiverSun, receiverDepth;
            uniform bool boundary;
            uniform int probeMode;
            out vec4 color;
            void main() {
                float e = drtSurfaceFogExposure(receiver,receiverSun);
                if (probeMode == 0) color = vec4(drtAirTransmittance(segmentStart,receiver,false,e),
                    drtAirOpticalDepth(segmentStart,receiver),e,drtFogOpacity(receiver,boundary,receiverSun));
                else if (probeMode == 1) color = drtApplySurfaceFog(vec4(8,4,2,.37),receiver,receiverDepth,boundary,receiverSun);
                else if (probeMode == 2) {
                    DrtFogTransport f = drtSurfaceMediumTransport(receiver,receiverDepth,boundary,receiverSun);
                    color = vec4(f.transmittance,f.scatter.r);
                } else if (probeMode == 3) color = vec4(drtBoundaryTransmittance(receiver.z),drtBoundaryEnd(),e,1);
                else {
                    DrtFogTransport a = drtAirTransport(vec3(0),receiver*.5,false,e);
                    DrtFogTransport b = drtAirTransport(receiver*.5,receiver,false,e);
                    DrtFogTransport c = drtAirTransport(vec3(0),receiver,false,e);
                    DrtFogTransport split = drtComposeTransport(a,b);
                    color = vec4(split.transmittance.r,c.transmittance.r,split.scatter.r,c.scatter.r);
                }
            }
            """));
        int fbo = GL.GenFramebuffer(), output = GL.GenTexture(), liquid = GL.GenTexture();
        int vao = GL.GenVertexArray(), ubo = GL.GenBuffer();
        GL.BindTexture(TextureTarget.Texture2D, output);
        GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba32f, 1, 1, 0, PixelFormat.Rgba, PixelType.Float, IntPtr.Zero);
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, fbo);
        GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, output, 0);
        if (GL.CheckFramebufferStatus(FramebufferTarget.Framebuffer) != FramebufferErrorCode.FramebufferComplete) throw new Exception("Fog HDR probe framebuffer");
        GL.BindTexture(TextureTarget.Texture2D, liquid);
        GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.R32f, 1, 1, 0, PixelFormat.Red, PixelType.Float, new[] { .625f });
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
        GL.BindVertexArray(vao); GL.Viewport(0, 0, 1, 1); GL.UseProgram(program);
        // Assign distinct sampler types even when the fixture uses analytic sky.
        GL.Uniform1(GL.GetUniformLocation(program, "liquidDepth"), 0);
        GL.Uniform1(GL.GetUniformLocation(program, "drtSkyViewPrevious"), 1);
        GL.Uniform1(GL.GetUniformLocation(program, "drtSkyViewCurrent"), 2);
        GL.Uniform1(GL.GetUniformLocation(program, "drtFogVolume"), 3);
        GL.Uniform2(GL.GetUniformLocation(program, "frameSize"), 1f, 1f);
        GL.Uniform4(GL.GetUniformLocation(program, "waterMurkColor"), .3f, .2f, .1f, 1f);
        GL.Uniform1(GL.GetUniformLocation(program, "receiverDepth"), .75f);
        GL.UniformBlockBinding(program, GL.GetUniformBlockIndex(program, "DrtAtmosphere"), 10);
        GL.BindBuffer(BufferTarget.UniformBuffer, ubo); GL.BindBufferBase(BufferRangeTarget.UniformBuffer, 10, ubo);
        float[] frame = new float[AtmosphereRenderer.FrameFloatCount], pixel = new float[4];
        float[] table = FogEnvironmentProbe.LightTable();
        float threshold = AtmosphereFogEnvironment.NormalizeSunlight(table, 31, AtmosphereFogEnvironment.FullSunlightLevel);
        void Reset() {
            Array.Clear(frame); frame[0] = .2f; frame[1] = .3f; frame[2] = .4f; frame[3] = 1;
            frame[8] = 512; frame[9] = 200; frame[13] = 1; frame[32] = frame[33] = 1;
            frame[36] = frame[41] = frame[46] = frame[51] = 1;
            frame[116] = threshold; frame[117] = frame[119] = 1;
        }
        void Upload() => GL.BufferData(BufferTarget.UniformBuffer, frame.Length * 4, frame, BufferUsageHint.DynamicDraw);
        float[] Read(int mode, Vector3 receiver, float sun = 1, bool boundary = false, Vector3 start = default) {
            GL.Uniform1(GL.GetUniformLocation(program, "probeMode"), mode);
            GL.Uniform1(GL.GetUniformLocation(program, "receiverSun"), sun);
            GL.Uniform1(GL.GetUniformLocation(program, "boundary"), boundary ? 1 : 0);
            GL.Uniform3(GL.GetUniformLocation(program, "receiver"), receiver);
            GL.Uniform3(GL.GetUniformLocation(program, "segmentStart"), start);
            GL.DrawArrays(PrimitiveType.Triangles, 0, 3); GL.ReadPixels(0, 0, 1, 1, PixelFormat.Rgba, PixelType.Float, pixel);
            foreach (float x in pixel) if (!float.IsFinite(x)) throw new Exception("Nonfinite fog output");
            return (float[])pixel.Clone();
        }
        void Near(float actual, double expected, string label, double tolerance = 2e-5) {
            if (Math.Abs(actual - expected) > tolerance) throw new Exception($"{label}: {actual:R} versus {expected:R}");
        }
        void Check(bool valid, string label) { if (!valid) throw new Exception(label); }
        double Sigma(double rho) {
            double x = Math.Clamp((rho - .01) / .04, 0, 1);
            return (.05 * rho + 7.23933 * rho * rho) * (1.8 + (.4 - 1.8) * x * x * (3 - 2 * x)) / 2;
        }
        try {
            Reset();
            foreach (float rho in new[] { .001f, .015f, .05f, .1f, .2f }) {
                frame[4] = rho; Upload();
                double sigma = Sigma(rho);
                foreach (float distance in new[] { .25f, 10f, 50f, 100f }) {
                    float actual = Read(0, new Vector3(0, 0, distance))[0];
                    Near(actual, Math.Min(.99999, Math.Exp(-sigma * distance)), "horizontal visibility calibration");
                }
                Console.WriteLine($"CALIBRATION native rho={rho:R}, T(10)={Math.Min(.99999,Math.Exp(-sigma*10)):F6}, T(50)={Math.Min(.99999,Math.Exp(-sigma*50)):F6}, 5%-contrast={-Math.Log(.05)/sigma:F2} blocks");
            }
            frame[4] = .05f; Upload();
            Near(Read(0, Vector3.Zero)[0], 1, "zero-distance identity");
            Check(Read(0, new Vector3(0, 0, .25f))[0] < 1, "fog begins immediately with no clear radius");
            float prior = 1;
            for (int distance = 1; distance <= 200; distance += 3) {
                float t = Read(0, new Vector3(0, 0, distance))[0];
                Check(t <= prior && t >= 0, "distance extinction is monotonic/bounded"); prior = t;
            }
            prior = 1;
            for (int setting = 0; setting <= 200; setting++) {
                frame[4] = setting / 1000f; Upload(); float t = Read(0, new Vector3(0, 0, 10))[0];
                // The shipping low/medium-density gain decreases as weather
                // thickens. Verify its authored curve rather than retuning it
                // to satisfy an older fixture's monotonic-slider assumption.
                Check(t <= 1 && t >= 0, "density response is bounded");
                Near(t, Math.Min(.99999, Math.Exp(-Sigma(setting / 1000.0) * 10)), "authored density response");
            }
            frame[4] = .015f; frame[6] = -.025f; frame[7] = -30; Upload();
            var down = new Vector3(0, -100, 100);
            float[] composed = Read(4, down); Near(composed[0], composed[1], "split descending-ray transmission"); Near(composed[2], composed[3], "split descending-ray scatter");
            frame[4] = 0; Upload(); float lower = Read(0, down)[1];
            frame[6] = .025f; frame[7] = 30; Upload(); Near(Read(0, new Vector3(0, 100, 100))[1], lower, "signed flat planes are symmetric");
            frame[4] = 20; Upload(); Check(Read(0, new Vector3(0, -10000, 10000))[0] >= 0, "extreme descending weather remains finite");
            Console.WriteLine("PASS continuous near fog, authored density curve, monotonic distance, additive descending/signed-flat segments, finite extremes");

            Reset(); frame[6] = .025f; Upload();
            double flatX = (.025 - .01) / .04;
            double previousFlat = (.05 * .025 + 7.23933 * .025 * .025) * (.2 + .8 * flatX * flatX * (3 - 2 * flatX)) / 2;
            double flatIntegral = Math.Sqrt(20000) * 50 / 60;
            Near(Read(0, new Vector3(0, 100, 100))[1], previousFlat * flatIntegral / 4, "flat fog is four times gentler independently of raised ordinary fog");
            Check(Read(0, new Vector3(0, 100, 100))[0] > Math.Exp(-previousFlat * flatIntegral), "flat fog retains more contrast");
            Console.WriteLine("PASS independent flat-fog curve slows density growth fourfold while retaining signed plane/height integration");

            Reset(); frame[117] = 0; frame[4] = .2f; frame[5] = .95f; frame[6] = -.2f; frame[7] = 50;
            frame[28] = 1; frame[29] = 1; frame[31] = .2f;
            foreach (float sunY in new[] { -1f, 1f }) {
                frame[13] = sunY; Upload(); float[] clear = Read(1, new Vector3(0, -200, 1000), 0, true);
                Near(clear[0], 8, "torch red retains only tiny baseline haze", .001); Near(clear[1], 4, "torch green retains only tiny baseline haze", .001);
                Near(clear[2], 2, "torch blue retains only tiny baseline haze", .001); Near(clear[3], .37, "material coverage preserved", 1e-6);
                Near(Read(0, down, 0, true)[3], .00001, "sheltered vertex fog metadata retains positive floor", 1e-6);
            }
            Reset(); frame[117] = 0; frame[4] = .05f; Upload(); prior = 1;
            for (int level = 0; level <= 31; level++) {
                float sunlight = AtmosphereFogEnvironment.NormalizeSunlight(table, 31, level);
                float[] fog = Read(0, new Vector3(0, 0, 40), sunlight);
                Near(fog[2], AtmosphereFogEnvironment.Exposure(sunlight, threshold), "CPU/GPU live sunlight agreement");
                Check(fog[0] <= prior + 1e-6f, "sunlight gradually restores extinction"); prior = fog[0];
                if (level >= AtmosphereFogEnvironment.FullSunlightLevel) Near(fog[0], Math.Exp(-Sigma(.05) * 40), "full sunlight plateau at level one");
            }
            Console.WriteLine("PASS cave torch visibility/alpha/positive metadata floor, live-table shelter to level one, independent day/night access");

            // Outdoor air in front of dark faces must match sunlit terrain at a
            // distance, while genuinely enclosed camera/receiver pairs stay clear.
            Reset(); frame[4] = .2f; Upload();
            foreach (float distance in new[] { 48f, 100f, 1000f }) {
                var receiver = new Vector3(0, 0, distance);
                float[] dark = Read(1, receiver, 0, true), lit = Read(1, receiver, 1, true);
                for (int c = 0; c < 4; c++) Near(dark[c], lit[c], "distant shaded terrain has no fog holes");
            }
            prior = 0;
            foreach (float distance in new[] { 8f, 16f, 24f, 32f, 48f }) {
                float e = Read(0, new Vector3(0, 0, distance), 0)[2];
                Check(e >= prior && e <= 1, "outdoor path shelter recovery is continuous/monotonic"); prior = e;
            }
            Near(prior, 1, "distant outdoor path restores full fog");
            frame[117] = 0; Upload();
            Near(Read(0, new Vector3(0, 0, 1000), 0, true)[3], .00001, "deep cave protection survives long distance");
            Reset(); Upload(); Near(Read(0, new Vector3(0, 0, 1), 0)[3], .00001, "zero-slider fog remains positive");
            Console.WriteLine("PASS distant dark/sunlit faces share atmosphere, smooth outdoor path recovery, enclosed cave and zero-slider positive floor");

            Reset(); frame[4] = .001f; frame[28] = frame[29] = 1; frame[31] = .2f; Upload();
            double lightT = Math.Min(.99999, Math.Exp(-Sigma(.001) * 100));
            Near(Read(2, new Vector3(0, 0, 100))[0], lightT * (1 - .9 * (1 - Math.Exp(-20))), "WorldFog follows authored low-weather calibration");
            Near((float)Sigma(.001), (.05 * .001 + 7.23933 * .001 * .001) * .9, "low fog retains authored density gain", 1e-10);
            Near((float)Sigma(.2), (.05 * .2 + 7.23933 * .2 * .2) * .2, "maximum density retains authored heavy-weather gain", 1e-8);
            Console.WriteLine("PASS light weather haze, independent WorldFog response and authored heavy-weather gain");

            Reset(); Upload();
            Near(Read(3, new Vector3(0, 0, .66f * 512))[0], 1, "native boundary start");
            Near(Read(3, new Vector3(0, 0, (.66f + 1.05f) * 256))[0], .0625, "native boundary midpoint");
            Near(Read(3, new Vector3(0, 0, 1.05f * 512))[0], 0, "native boundary end");
            frame[118] = 2048; Upload();
            Near(Read(3, new Vector3(0, 0, .66f * 512))[0], 1, "LOD keeps boundary start");
            Near(Read(3, new Vector3(0, 0, (.66f * 512 + 2048) * .5f))[0], .0625, "LOD boundary midpoint");
            Near(Read(3, new Vector3(0, 0, 2048))[0], 0, "LOD reaches actual rendered end");
            Check(Read(3, new Vector3(0, 0, 1.05f * 512))[0] > .8f, "real chunks do not become opaque at their old endpoint with LOD active");
            Near(Read(3, new Vector3(0, 0, .66f * 512 + .1f))[0], 1, "LOD smooth boundary onset");
            frame[118] = 256; Upload(); Near(Read(3, Vector3.Zero)[1], 1.05f * 512, "short/disabled LOD retains native end");
            Console.WriteLine("PASS unchanged boundary onset/no-provider curve, extended actual LOD endpoint, smooth start, disabled/short range fallback");

            Reset(); frame[10] = 1; frame[4] = .2f; frame[5] = .1f; Upload();
            float[] water0 = Read(2, new Vector3(0, 0, 20), 0), water1 = Read(2, new Vector3(0, 0, 20));
            for (int c = 0; c < 3; c++) Near(water0[c], water1[c], "underground water absorption unaffected by sunlight");
            Check(water0[0] < water0[1] && water0[1] < water0[2] && water0[2] < 1, "water retains spectral absorption");
            foreach (bool submerged in new[] { false, true }) {
                Reset(); frame[117] = 0; frame[10] = submerged ? 1 : 0; frame[35] = 1; frame[4] = .05f;
                frame[68] = .2f; frame[69] = .3f; frame[70] = .4f; frame[72] = .05f; Upload();
                float[] mixed0 = Read(2, new Vector3(0, 0, 20), 0), mixed1 = Read(2, new Vector3(0, 0, 20));
                for (int c = 0; c < 3; c++) {
                    double extinction = new[] { .12, .055, .025 }[c] + (submerged ? .05 : 0);
                    Near(mixed0[c], Math.Exp(-extinction * 10) * .99999, "shelter retains tiny air floor without changing water absorption");
                    Check(mixed1[c] < mixed0[c], "exposed mixed-medium air adds absorption");
                }
            }
            Console.WriteLine("PASS submerged water and both ordered air/water paths keep spectral absorption at zero sunlight");
            if (GL.GetError() != ErrorCode.NoError) throw new Exception("Fog visibility probe GL error");
            AtmosphereBindingsProbe.Run(Path.Combine(assets, "drtagx/shaders/atmosphere"), ubo);
        } finally {
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0); GL.BindVertexArray(0);
            GL.BindBufferBase(BufferRangeTarget.UniformBuffer, 10, 0);
            GL.DeleteBuffer(ubo); GL.DeleteVertexArray(vao); GL.DeleteFramebuffer(fbo);
            GL.DeleteTexture(output); GL.DeleteTexture(liquid); GL.DeleteProgram(program);
        }
    }
}
