using System;
using System.IO;
using System.Linq;
using System.Reflection;
using DRTAgX;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;
using Vintagestory.Client.NoObf;

/// <summary>Numerical GPU verification; deliberately no screenshots or timing measurements.</summary>
internal static class AtmosphereProbe
{
    internal static void Run(string directory)
    {
        using var window = new GameWindow(GameWindowSettings.Default, new NativeWindowSettings
        { StartVisible = false, ClientSize = new Vector2i(16, 16), API = ContextAPI.OpenGL,
          APIVersion = new Version(4, 3), Profile = ContextProfile.Core });
        window.MakeCurrent();
        GL.LoadBindings(new OpenTK.Windowing.GraphicsLibraryFramework.GLFWBindingsContext());
        // Reflection of public signatures only; never inspect/decompile engine bodies.
        foreach (var method in typeof(ClientPlatformWindows).GetMethods().Where(x => x.Name == "RenderMesh"))
            Console.WriteLine("Native mesh signature: " + method);
        foreach (string name in new[] { "Use", "Stop" })
        {
            var method = typeof(ShaderProgramBase).GetMethod(name, Type.EmptyTypes);
            Console.WriteLine($"Native shader signature: {method?.DeclaringType?.Name}.{method}");
        }

        var api = ProbeAssets.AtmosphereApi(directory);
        var state = new AtmosphereComputeState();
        int oldProgram = GL.GetInteger(GetPName.CurrentProgram), oldActive = GL.GetInteger(GetPName.ActiveTexture);
        int oldUbo = GL.GetInteger(GetPName.UniformBufferBinding), oldPack = GL.GetInteger(GetPName.PixelPackBufferBinding);
        int[] saved = new int[4];
        GL.GetInteger(GetPName.Viewport, saved);
        state.Capture();
        int trans = AtmosphereCompute.Load(api, "drtagx_atmosphere_transmittance");
        int multiple = AtmosphereCompute.Load(api, "drtagx_atmosphere_multiscatter");
        int sky = AtmosphereCompute.Load(api, "drtagx_atmosphere_skyview");
        int volume = AtmosphereCompute.Load(api, "drtagx_fog_volume");
        int transTexture = AtmosphereCompute.Texture(256, 64), multipleTexture = AtmosphereCompute.Texture(32, 32);
        int skyTexture = AtmosphereCompute.Texture(192, 108), volumeTexture = AtmosphereCompute.Texture(64, 36, 32);
        AtmosphereCompute.Dispatch2D(trans, transTexture, 256, 64);
        GL.ActiveTexture(TextureUnit.Texture0);
        GL.BindTexture(TextureTarget.Texture2D, transTexture);
        GL.UseProgram(multiple);
        GL.Uniform1(GL.GetUniformLocation(multiple, "transmittanceTable"), 0);
        AtmosphereCompute.Dispatch2D(multiple, multipleTexture, 32, 32);
        GL.UseProgram(sky);
        GL.Uniform1(GL.GetUniformLocation(sky, "transmittanceTable"), 0);
        GL.Uniform1(GL.GetUniformLocation(sky, "multiscatterTable"), 1);
        GL.ActiveTexture(TextureUnit.Texture1);
        GL.BindTexture(TextureTarget.Texture2D, multipleTexture);
        GL.Uniform4(GL.GetUniformLocation(sky, "sunDirection"), 0f, 1f, 0f, 1f);
        GL.Uniform4(GL.GetUniformLocation(sky, "moonDirection"), 0f, -1f, 0f, 0f);
        GL.Uniform1(GL.GetUniformLocation(sky, "cameraAltitude"), 1f);
        AtmosphereCompute.Dispatch2D(sky, skyTexture, 192, 108);
        CheckTexture(transTexture, 256 * 64, true);
        CheckTexture(multipleTexture, 32 * 32, false);
        CheckTexture(skyTexture, 192 * 108, false);
        VerifyTwilight(sky, skyTexture, transTexture);
        Console.WriteLine("PASS all atmosphere compute stages compile; LUT output is finite, nonnegative, HDR-capable");

        float[] frame = new float[AtmosphereRenderer.FrameFloatCount];
        frame[3] = 1; frame[4] = 0.01f; frame[8] = 512; frame[9] = 200;
        frame[26] = 512; frame[32] = frame[33] = 16;
        frame[36] = frame[41] = frame[46] = frame[51] = 1;
        frame[52] = frame[57] = frame[62] = frame[67] = 1;
        frame[76] = frame[81] = frame[86] = frame[91] = 1;
        int buffer = GL.GenBuffer();
        GL.BindBuffer(BufferTarget.UniformBuffer, buffer);
        GL.BufferData(BufferTarget.UniformBuffer, frame.Length * 4, frame, BufferUsageHint.DynamicDraw);
        GL.BindBufferBase(BufferRangeTarget.UniformBuffer, 10, buffer);
        string common = File.ReadAllText(Path.Combine(directory, "drtagx_fog_transport.ash"));
        // Validate the currently authored band, including user tuning, without
        // changing production fog to match the historical 85-99% test fixture.
        var band = System.Text.RegularExpressions.Regex.Match(common,
            @"smoothstep\(([0-9.]+) \* distance, ([0-9.]+) \* distance, horizontalDistance\)");
        Assert(band.Success, "authored boundary band is discoverable");
        double startBand = double.Parse(band.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        double endBand = double.Parse(band.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);
        string Distance(double fraction) => (512 * fraction).ToString("R", System.Globalization.CultureInfo.InvariantCulture);
        int math = ProbeShader.Program((ShaderType.ComputeShader, "#version 430 core\nlayout(local_size_x=1) in;\n" + common + $$"""

            layout(std430,binding=11) buffer Results { vec4 values[]; };
            void main() {
                values[0] = vec4(drtAirTransmittance(vec3(0),vec3(0),true),
                    drtAirTransmittance(vec3(0),vec3(0,0,10),true),
                    drtAirTransmittance(vec3(0),vec3(0,0,100),true),
                    drtAirTransmittance(vec3(0),vec3(0,0,200),true));
                values[1] = vec4(drtBoundaryTransmittance({{Distance(startBand)}}),drtBoundaryTransmittance({{Distance((startBand+endBand)*.5)}}),
                    drtBoundaryTransmittance({{Distance(endBand)}}),drtBoundaryTransmittance({{Distance(endBand+.1)}}));
                DrtFogTransport a = DrtFogTransport(vec3(.5),vec3(1,0,0));
                DrtFogTransport b = DrtFogTransport(vec3(.25),vec3(0,0,1));
                values[2] = vec4(drtComposeTransport(a,b).scatter,drtComposeTransport(a,b).transmittance.x);
                values[3] = vec4(drtWaterTransport(10,vec3(.2),false).transmittance,
                    drtApplyTransport(vec4(8,4,2,.25),a).a);
                values[4] = vec4(drtHeightIntegral(100,0,0,200),drtHeightIntegral(100,0,100,200),
                    drtFlatIntegral(100,0,-100,-50,-.01),drtFlatIntegral(100,0,100,50,.01));
            }
            """));
        GL.UniformBlockBinding(math, GL.GetUniformBlockIndex(math, "DrtAtmosphere"), 10);
        int results = GL.GenBuffer();
        GL.BindBuffer(BufferTarget.ShaderStorageBuffer, results);
        GL.BufferData(BufferTarget.ShaderStorageBuffer, 20 * 4, IntPtr.Zero, BufferUsageHint.DynamicRead);
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 11, results);
        GL.UseProgram(math);
        GL.DispatchCompute(1, 1, 1);
        GL.MemoryBarrier(MemoryBarrierFlags.BufferUpdateBarrierBit);
        float[] data = new float[20];
        GL.GetBufferSubData(BufferTarget.ShaderStorageBuffer, IntPtr.Zero, data.Length * 4, data);
        Assert(Math.Abs(data[0]-1) < 1e-6 && data[1]>data[2] && data[2]>data[3], "monotonic extinction / zero distance");
        Assert(data[4]==1 && Math.Abs(data[5]-.0625f)<1e-6 && data[6]<1e-6 && data[7]==0,
            "authored boundary: start/mid/end preserve smooth transmission squared twice");
        Assert(data[8]==1 && data[9]==0 && data[10]==.5 && data[11]==.125, "ordered segment composition");
        Assert(data[12]<data[13] && data[13]<data[14] && data[15]==.25, "spectral absorption / alpha preservation");
        Assert(Math.Abs(data[16]-100)<1e-4 && data[17]<100 && data[18]>0 && data[18]==data[19], "height integral / signed flat regions");
        Console.WriteLine("PASS production transport: zero distance, monotonic extinction, boundary, ordered media, water spectrum, material alpha, signed height fog");

        // Dispatch the real volume against an empty (fully visible) CSM.
        GL.UseProgram(volume);
        GL.UniformBlockBinding(volume, GL.GetUniformBlockIndex(volume, "DrtAtmosphere"), 10);
        GL.Uniform1(GL.GetUniformLocation(volume, "farShadow"), 0);
        GL.Uniform1(GL.GetUniformLocation(volume, "shaftStrength"), 1f);
        GL.Uniform3(GL.GetUniformLocation(volume, "cameraInShadow"), 2f, 2f, 2f);
        GL.BindImageTexture(0, volumeTexture, 0, true, 0, TextureAccess.WriteOnly, SizedInternalFormat.R16f);
        GL.DispatchCompute(8, 9, 1);
        GL.MemoryBarrier(MemoryBarrierFlags.TextureUpdateBarrierBit);
        GL.ActiveTexture(TextureUnit.Texture0);
        GL.BindTexture(TextureTarget.Texture3D, volumeTexture);
        float[] fogVolume = new float[64 * 36 * 32];
        GL.GetTexImage(TextureTarget.Texture3D, 0, PixelFormat.Red, PixelType.Float, fogVolume);
        Assert(fogVolume.All(x => float.IsFinite(x) && Math.Abs(x-1)<0.001f), "unshadowed volume retains analytic scatter");
        Console.WriteLine("PASS volume dispatch, dimensions, finite cumulative visibility and analytic fallback outside CSM");

        // A real comparison texture makes every in-cascade sample occluded.
        // Use WorldFog alone to catch omission from volume scattering weights.
        int shadow = GL.GenTexture();
        GL.BindTexture(TextureTarget.Texture2D, shadow);
        GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.DepthComponent24, 1, 1, 0,
            PixelFormat.DepthComponent, PixelType.Float, new float[] { 0.2f });
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureCompareMode, (int)TextureCompareMode.CompareRefToTexture);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureCompareFunc, (int)DepthFunction.Lequal);
        frame[4] = 0; frame[28] = .8f; frame[29] = 1; frame[31] = .01f;
        GL.BindBuffer(BufferTarget.UniformBuffer, buffer);
        GL.BufferSubData(BufferTarget.UniformBuffer, IntPtr.Zero, frame.Length * 4, frame);
        GL.UseProgram(volume);
        GL.UniformMatrix4(GL.GetUniformLocation(volume, "toShadow"), 1, false, new float[16]);
        GL.Uniform3(GL.GetUniformLocation(volume, "cameraInShadow"), .5f, .5f, .5f);
        GL.DispatchCompute(8, 9, 1);
        GL.MemoryBarrier(MemoryBarrierFlags.TextureUpdateBarrierBit);
        GL.GetTexImage(TextureTarget.Texture3D, 0, PixelFormat.Red, PixelType.Float, fogVolume);
        Assert(fogVolume.All(x => float.IsFinite(x) && Math.Abs(x) < .001f), "WorldFog-only shadowed volume");
        GL.Uniform1(GL.GetUniformLocation(volume, "shaftStrength"), 0f);
        GL.DispatchCompute(8, 9, 1);
        GL.MemoryBarrier(MemoryBarrierFlags.TextureUpdateBarrierBit);
        GL.GetTexImage(TextureTarget.Texture3D, 0, PixelFormat.Red, PixelType.Float, fogVolume);
        Assert(fogVolume.All(x => Math.Abs(x-1) < .001f), "zero shaft strength preserves analytical scatter");
        Console.WriteLine("PASS actual CSM comparison, WorldFog-only shadow extinction and disabled shaft strength");
        GL.DeleteTexture(shadow);

        VerifySkyComposition(directory, frame, buffer);
        AtmosphereBindingsProbe.Run(directory, buffer);

        GL.BindBufferBase(BufferRangeTarget.UniformBuffer, 10, 0);
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 11, 0);
        GL.BindBuffer(BufferTarget.ShaderStorageBuffer, 0);
        state.Dispose();
        Assert(GL.GetInteger(GetPName.CurrentProgram)==oldProgram && GL.GetInteger(GetPName.ActiveTexture)==oldActive &&
            GL.GetInteger(GetPName.UniformBufferBinding)==oldUbo && GL.GetInteger(GetPName.PixelPackBufferBinding)==oldPack,
            "production compute guard restores native program/textures/buffers");
        Assert(GL.GetError()==ErrorCode.NoError, "OpenGL error after all checks");
        Console.WriteLine("PASS production compute state restoration; GL NoError");
        AmbientSkyProbe.Run(directory);
        CloudBalanceProbe.Run(directory);
        LunarBalanceProbe.Run(directory);
        HorizonCompositionProbe.Run(directory);
        FogRadianceProbe.Run(directory);
        CelestialOcclusionProbe.Run(directory);
        foreach(int program in new[]{trans,multiple,sky,volume,math}) GL.DeleteProgram(program);
        foreach(int texture in new[]{transTexture,multipleTexture,skyTexture,volumeTexture}) GL.DeleteTexture(texture);
        GL.DeleteBuffer(buffer); GL.DeleteBuffer(results);
    }

    private static void VerifyTwilight(int program, int texture, int transmittance)
    {
        // Read numerical LUT samples toward/away from a low sun. No images.
        foreach (float elevation in new[] { 90f, 5f, 0f, -3f, -8f })
        {
            float angle = elevation * MathF.PI / 180f;
            GL.ActiveTexture(TextureUnit.Texture0);
            GL.BindTexture(TextureTarget.Texture2D, transmittance);
            GL.UseProgram(program);
            GL.Uniform4(GL.GetUniformLocation(program, "sunDirection"), MathF.Cos(angle), MathF.Sin(angle), 0f, 1f);
            AtmosphereCompute.Dispatch2D(program, texture, 192, 108);
            GL.MemoryBarrier(MemoryBarrierFlags.TextureUpdateBarrierBit);
            GL.BindTexture(TextureTarget.Texture2D, texture);
            float[] data = new float[192 * 108 * 4];
            GL.GetTexImage(TextureTarget.Texture2D, 0, PixelFormat.Rgba, PixelType.Float, data);
            int toward = (54 * 192 + 96) * 4, away = (54 * 192) * 4;
            Console.WriteLine($"Sky sun={elevation:F0}deg toward=({data[toward]:F6},{data[toward+1]:F6},{data[toward+2]:F6}) away=({data[away]:F6},{data[away+1]:F6},{data[away+2]:F6})");
            Assert(data.All(float.IsFinite), "finite dawn/dusk transport");
            if (elevation is 0f or 5f)
                Assert(data[toward] > data[toward + 1] * 1.5f && data[toward + 1] > data[toward + 2], "warm orange/red low-sun scattering");
            if (elevation != -3f) continue;
            float red = data[toward];
            // A dark ground must not extinguish the still illuminated atmosphere.
            GL.ActiveTexture(TextureUnit.Texture0); GL.BindTexture(TextureTarget.Texture2D, transmittance);
            GL.Uniform4(GL.GetUniformLocation(program, "sunDirection"), MathF.Cos(angle), MathF.Sin(angle), 0f, 0f);
            AtmosphereCompute.Dispatch2D(program, texture, 192, 108);
            GL.MemoryBarrier(MemoryBarrierFlags.TextureUpdateBarrierBit);
            GL.BindTexture(TextureTarget.Texture2D, texture);
            GL.GetTexImage(TextureTarget.Texture2D, 0, PixelFormat.Rgba, PixelType.Float, data);
            Assert(data[toward] > red * 0.8f, "twilight persists with zero ground-light strength");
            GL.ActiveTexture(TextureUnit.Texture0); GL.BindTexture(TextureTarget.Texture2D, transmittance);
            GL.Uniform4(GL.GetUniformLocation(program, "sunDirection"), -MathF.Cos(angle), MathF.Sin(angle), 0f, 0f);
            AtmosphereCompute.Dispatch2D(program, texture, 192, 108);
            GL.MemoryBarrier(MemoryBarrierFlags.TextureUpdateBarrierBit);
            GL.BindTexture(TextureTarget.Texture2D, texture);
            float[] dawn = new float[data.Length];
            GL.GetTexImage(TextureTarget.Texture2D, 0, PixelFormat.Rgba, PixelType.Float, dawn);
            Assert(Math.Abs(dawn[away] - data[toward]) < 0.00001f, "sunrise and sunset use the same transport");
        }
        Console.WriteLine("PASS warm sunrise/sunset scattering and twilight without ground illumination");
    }

    private static void VerifySkyComposition(string directory, float[] frame, int buffer)
    {
        // Numerical fragment probe: the actual maintained sky overlay must
        // apply weather once to clear atmosphere and the native star base.
        string sampling = File.ReadAllText(Path.Combine(directory, "drtagx_atmosphere_sampling.fsh"));
        string transport = File.ReadAllText(Path.Combine(directory, "drtagx_fog_transport.ash"));
        string sky = File.ReadAllText(Path.Combine(directory, "drtagx_atmospheric_sky.fsh"));
        sampling = sampling.Replace("#include drtagx_fog_transport.ash", transport);
        sky = sky.Replace("#include drtagx_atmosphere_sampling.fsh", sampling);
        int program = ProbeShader.Program((ShaderType.VertexShader, """
            #version 430 core
            void main() { gl_Position=vec4((gl_VertexID==1 ? 3.0 : -1.0), (gl_VertexID==2 ? 3.0 : -1.0),0,1); }
            """), (ShaderType.FragmentShader, "#version 430 core\n" + sky + """

            out vec4 result;
            uniform vec3 probeDirection;
            uniform int backgroundProbe;
            void main() {
                if (backgroundProbe != 0) { result=vec4(drtSkyBackground(probeDirection),1); return; }
                vec4 c,g;
                getSkyColorAt(probeDirection,vec3(0,1,0),0,0,0,c,g);
                float t=drtAirCelestialVisibility(probeDirection);
                vec3 clear=drtClearSkyRadiance(probeDirection);
                vec3 base=clear+vec3(.1); // Full-night atmosphere + test stars.
                vec3 overlay=drtSkyOverlayRadiance(c.rgb,probeDirection,0,c.a);
                vec3 actual=overlay*c.a+base*(1-c.a);
                vec3 expected=drtSkyBackground(probeDirection)+vec3(.1)*t*drtStarVisibility();
                // The fixture must receive the current elevation-dependent sky gain once.
                vec3 gainError=abs(clear-vec3(.3,.4,.5)*DRT_SKY_EXPOSURE*DRT_SKY_DISPLAY_GAIN);
                result=vec4(abs(actual-expected)+gainError,t);
            }
            """));
        int lut = GL.GenTexture();
        GL.ActiveTexture(TextureUnit.Texture0); GL.BindTexture(TextureTarget.Texture2D, lut);
        GL.TexImage2D(TextureTarget.Texture2D,0,PixelInternalFormat.Rgba32f,1,1,0,PixelFormat.Rgba,PixelType.Float,new float[]{.3f,.4f,.5f,1});
        GL.TexParameter(TextureTarget.Texture2D,TextureParameterName.TextureMinFilter,(int)TextureMinFilter.Nearest);
        GL.TexParameter(TextureTarget.Texture2D,TextureParameterName.TextureMagFilter,(int)TextureMagFilter.Nearest);
        GL.UseProgram(program);
        GL.UniformBlockBinding(program,GL.GetUniformBlockIndex(program,"DrtAtmosphere"),10);
        GL.Uniform1(GL.GetUniformLocation(program,"drtSkyViewPrevious"),0);
        GL.Uniform1(GL.GetUniformLocation(program,"drtSkyViewCurrent"),0);
        // Other active sampler types must occupy a different unit even on
        // disabled branches. This is a GL program validation contract.
        GL.Uniform1(GL.GetUniformLocation(program,"drtFogVolume"),1);
        int output=AtmosphereCompute.Texture(1,1), fbo=GL.GenFramebuffer(), vao=GL.GenVertexArray();
        // Creating the render target changes the active texture binding. Restore
        // the independent fixture so this probe never samples its own output.
        GL.ActiveTexture(TextureUnit.Texture0); GL.BindTexture(TextureTarget.Texture2D, lut);
        GL.BindFramebuffer(FramebufferTarget.Framebuffer,fbo);
        GL.FramebufferTexture2D(FramebufferTarget.Framebuffer,FramebufferAttachment.ColorAttachment0,TextureTarget.Texture2D,output,0);
        Assert(GL.CheckFramebufferStatus(FramebufferTarget.Framebuffer)==FramebufferErrorCode.FramebufferComplete,"sky probe framebuffer");
        GL.BindVertexArray(vao); GL.Viewport(0,0,1,1);
        frame[0]=.2f; frame[1]=.25f; frame[2]=.3f; frame[13]=1; frame[24]=1; frame[27]=1; frame[28]=0;
        foreach(var direction in new[]{Vector3.UnitX,Vector3.UnitY})
        foreach(float density in new[]{0f,.00005f,.1f}) {
            frame[4]=density;
            GL.Uniform3(GL.GetUniformLocation(program,"probeDirection"),direction);
            GL.BindBuffer(BufferTarget.UniformBuffer,buffer);
            GL.BufferSubData(BufferTarget.UniformBuffer,IntPtr.Zero,frame.Length*4,frame);
            GL.DrawArrays(PrimitiveType.Triangles,0,3);
            float[] pixel=new float[4]; GL.ReadPixels(0,0,1,1,PixelFormat.Rgba,PixelType.Float,pixel);
            if (pixel.Take(3).Any(x=>x>=.0001f)) Console.WriteLine($"Sky composition direction={direction} density={density} error=({string.Join(',',pixel)})");
            Assert(pixel.Take(3).All(x=>x<.0001f),"single sky weather/straight-alpha composition");
            Assert(density<.001f || pixel[3]<.00001f,"dense fog conceals overhead and horizon stars");
        }
        // Mild daytime haze must retain blue chroma, rather than neutral fog
        // at the new sky exposure. Its energy stays anchored to native fog.
        GL.TexSubImage2D(TextureTarget.Texture2D,0,0,0,1,1,PixelFormat.Rgba,PixelType.Float,new float[]{.08f,.2f,.65f,1});
        GL.Uniform1(GL.GetUniformLocation(program,"backgroundProbe"),1);
        GL.Uniform3(GL.GetUniformLocation(program,"probeDirection"),Vector3.UnitX);
        frame[0]=frame[1]=frame[2]=.2f;frame[13]=1;
        foreach(float density in new[]{.0004f,.1f}) {
            frame[4]=density;GL.BindBuffer(BufferTarget.UniformBuffer,buffer);
            GL.BufferSubData(BufferTarget.UniformBuffer,IntPtr.Zero,frame.Length*4,frame);
            GL.DrawArrays(PrimitiveType.Triangles,0,3);
            var pixel=new float[4];GL.ReadPixels(0,0,1,1,PixelFormat.Rgba,PixelType.Float,pixel);
            if(density<.001f) {
                Assert(pixel[2]>2*pixel[0] && pixel[2]>pixel[1],"day haze follows blue atmosphere instead of gray native fog");
                // Dry-air extinction now follows the calibrated continuous
                // density response; sky exposure/chroma energy stays unchanged.
                float sigma=density*(.05f+7.23933f*density);
                float t=MathF.Exp(-sigma*8000);
                float clearY=6*(.08f*.2126f+.2f*.7152f+.65f*.0722f);
                float actualY=pixel[0]*.2126f+pixel[1]*.7152f+pixel[2]*.0722f;
                Assert(Math.Abs(actualY-(1.2f*(1-t)+clearY*t))<.001f,"day haze chroma preserves native fog luminance and 2x sky gain");
            } else Assert(Math.Abs(pixel[0]-1.2f)<.001f && Math.Abs(pixel[2]-pixel[0])<.001f,
                "dense daytime weather retains neutral native fog");
        }
        Console.WriteLine("PASS vibrant blue daytime haze at retained luminance; dense weather stays gray");
        frame[0]=.2f;frame[1]=.25f;frame[2]=.3f;
        // Strong horizon haze must carry sunset color; fully dense weather
        // must converge to the native diffuse fog color at the new exposure.
        GL.TexSubImage2D(TextureTarget.Texture2D,0,0,0,1,1,PixelFormat.Rgba,PixelType.Float,new float[]{.5f,.15f,.03f,1});
        GL.Uniform1(GL.GetUniformLocation(program,"backgroundProbe"),1);
        GL.Uniform3(GL.GetUniformLocation(program,"probeDirection"),Vector3.UnitX);
        frame[13]=0;
        foreach(float density in new[]{.0004f,.1f}) {
            frame[4]=density;
            GL.BindBuffer(BufferTarget.UniformBuffer,buffer);
            GL.BufferSubData(BufferTarget.UniformBuffer,IntPtr.Zero,frame.Length*4,frame);
            GL.DrawArrays(PrimitiveType.Triangles,0,3);
            float[] pixel=new float[4]; GL.ReadPixels(0,0,1,1,PixelFormat.Rgba,PixelType.Float,pixel);
            if(density<.001f) Assert(pixel[0]>pixel[2],"hazy sunset retains warm horizon tint");
            else {
                // Compare chromaticity: dense weather retains the native diffuse fog color,
                // independently of the user's separate twilight sky exposure gain.
                Assert(Math.Abs(pixel[0]/pixel[1]-.8f)<.001f && Math.Abs(pixel[2]/pixel[1]-1.2f)<.001f,
                    "dense twilight fog retains native diffuse chromaticity");
            }
        }
        GL.BindFramebuffer(FramebufferTarget.Framebuffer,0); GL.BindVertexArray(0);
        GL.DeleteFramebuffer(fbo); GL.DeleteVertexArray(vao); GL.DeleteTexture(output); GL.DeleteTexture(lut); GL.DeleteProgram(program);
        Console.WriteLine("PASS current sky exposure; clear/mild/dense composition; warm hazy sunset and diffuse dense fog; stars attenuated once");
        VerifySolarSpread(directory, sky);
    }

    private static void VerifySolarSpread(string directory, string sky)
    {
        string math=File.ReadAllText(Path.Combine(directory,"drtagx_atmosphere_math.ash"));
        int program=ProbeShader.Program((ShaderType.ComputeShader,"#version 430 core\n"+sky+math+"""
            layout(local_size_x=1) in;
            layout(std430,binding=11) buffer Result { vec4 value; };
            void main() {
                float integral=0;
                for(int i=0;i<4096;i++) integral+=drtMiePhase(-1+2*(float(i)+.5)/4096);
                value=vec4(integral*(4*DRT_PI/4096),drtMiePhase(cos(radians(5.0)))/drtMiePhase(1),
                    drtSolarExtraction(vec3(0,1,0),vec3(0,1,0),1),
                    drtSolarExtraction(vec3(sin(radians(2.0)),cos(radians(2.0)),0),vec3(0,1,0),1));
            }
            """));
        int result=GL.GenBuffer(); GL.BindBuffer(BufferTarget.ShaderStorageBuffer,result);
        GL.BufferData(BufferTarget.ShaderStorageBuffer,16,IntPtr.Zero,BufferUsageHint.DynamicRead);
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer,11,result);
        GL.UseProgram(program); GL.DispatchCompute(1,1,1); GL.MemoryBarrier(MemoryBarrierFlags.BufferUpdateBarrierBit);
        float[] value=new float[4]; GL.GetBufferSubData(BufferTarget.ShaderStorageBuffer,IntPtr.Zero,16,value);
        Assert(Math.Abs(value[0]-1)<.01f && value[1]<.5f,"Mie phase conserves angular energy with narrow forward core");
        Assert(value[2]==1 && value[3]==0,"godray extraction stays within one degree of the sun");
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer,11,0); GL.DeleteBuffer(result); GL.DeleteProgram(program);
        Console.WriteLine("PASS solar scatter: normalized Mie energy, narrower forward lobe and compact extraction mask");
    }

    private static void Assert(bool condition, string message) { if(!condition) throw new Exception(message); }
    private static void CheckTexture(int texture, int pixels, bool transmittance)
    {
        GL.MemoryBarrier(MemoryBarrierFlags.TextureUpdateBarrierBit);
        GL.ActiveTexture(TextureUnit.Texture0);
        GL.BindTexture(TextureTarget.Texture2D, texture);
        float[] values = new float[pixels*4];
        GL.GetTexImage(TextureTarget.Texture2D,0,PixelFormat.Rgba,PixelType.Float,values);
        Assert(values.All(x=>float.IsFinite(x) && x>=0 && (!transmittance || x<=1)), "LUT radiance/transmittance bounds");
        Assert(values.Where((_,i)=>i%4!=3).Any(x=>x>0), "LUT has nonzero radiance/transmittance");
    }
}
