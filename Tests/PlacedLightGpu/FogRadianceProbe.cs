using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DRTAgX;
using OpenTK.Graphics.OpenGL4;

/// <summary>Opaque weather must approach the displayed sky, independent of surface light or medium splits.</summary>
internal static class FogRadianceProbe
{
    internal static void Run(string directory)
    {
        var seen = new HashSet<string>();
        string Expand(string name)
        {
            if (!seen.Add(name)) return "";
            return string.Join('\n', File.ReadAllLines(Path.Combine(directory, name)).Select(line =>
                line.Trim().StartsWith("#include ") ? Expand(line.Trim()[9..]) : line));
        }
        string helpers = Expand("drtagx_atmospheric_sky.fsh") + Expand("drtagx_cloud_lighting.fsh");
        string water = Expand("drtagx_water_transport.fsh");
        int program = ProbeShader.Program((ShaderType.ComputeShader, "#version 430 core\nlayout(local_size_x=1) in;\n" + helpers + "\n" + """
            #define gl_FragCoord vec4(.5,.5,.5,1)
            const float zNear=.1, zFar=10000;
            vec4 applySpheresFog(vec4 c,float f,vec3 p){return c;}
            """ + "\n" + water + "\n" + """
            uniform vec3 direction;
            uniform float distance;
            layout(std430,binding=11) buffer Results { vec4 values[8]; };
            void main(){
                vec3 p=drtAtmosphereCamera.xyz+normalize(direction)*distance;
                values[0]=vec4(drtSkyBackground(direction),1);
                // Compare the forward air helper and completed deferred surface
                // helper with different lighting inputs, without the boundary fade.
                values[1]=drtApplySurfaceFog(vec4(.01,.02,.03,.7),p,.9,false);
                values[2]=drtApplyAirFog(vec4(10,3,1,.7),p,false);
                values[3]=drtApplyTransport(vec4(drtClearSkyRadiance(direction),1),drtSkyMediumTransport(direction));
                DrtFogTransport fog=drtSurfaceMediumTransport(p,.9,false);
                vec4 cloud=drtCloudThroughFog(vec4(3,4,5,.8),fog);
                values[4]=vec4(mix(values[1].rgb,cloud.rgb,cloud.a),1);
                vec4 c,g;getSkyColorAt(direction,vec3(0,1,0),0,1,0,c,g);
                values[5]=c;
                values[6]=drtApplyTransport(vec4(.8,.6,.4,.7),drtWaterTransport(distance,vec3(.06,.1,.14),true));
                values[7]=vec4(fog.transmittance,1);
            }
            """));
        GL.UseProgram(program);
        GL.UniformBlockBinding(program, GL.GetUniformBlockIndex(program, "DrtAtmosphere"), 10);
        foreach (var (name, unit) in new[] { ("drtSkyViewPrevious", 0), ("drtSkyViewCurrent", 0), ("drtFogVolume", 1), ("liquidDepth", 2) })
            GL.Uniform1(GL.GetUniformLocation(program, name), unit);
        GL.Uniform2(GL.GetUniformLocation(program, "frameSize"), 1f, 1f);
        int lut = Texture(0, .08f, .2f, .65f, 1), liquid = Texture(2, 1, 1, 1, 1);
        int volume = GL.GenTexture();
        GL.ActiveTexture(TextureUnit.Texture1); GL.BindTexture(TextureTarget.Texture3D, volume);
        GL.TexImage3D(TextureTarget.Texture3D, 0, PixelInternalFormat.R32f, 1, 1, 1, 0, PixelFormat.Red, PixelType.Float, new[] { 0f });
        GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
        GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
        var frame = new float[AtmosphereRenderer.FrameFloatCount];
        frame[0] = frame[1] = frame[2] = .2f; frame[3] = 1; frame[9] = 200; frame[13] = 1;
        frame[24] = frame[27] = 1; frame[26] = 10000;
        // Identity inverse matrices yield a known finite interface for the split-medium checks.
        foreach (int start in new[] { 36, 52 }) for (int i = 0; i < 4; i++) frame[start + i * 5] = 1;
        int buffer = GL.GenBuffer(); GL.BindBuffer(BufferTarget.UniformBuffer, buffer);
        GL.BufferData(BufferTarget.UniformBuffer, frame.Length * 4, frame, BufferUsageHint.DynamicDraw);
        GL.BindBufferBase(BufferRangeTarget.UniformBuffer, 10, buffer);
        int results = GL.GenBuffer(); GL.BindBuffer(BufferTarget.ShaderStorageBuffer, results);
        GL.BufferData(BufferTarget.ShaderStorageBuffer, 128, IntPtr.Zero, BufferUsageHint.DynamicRead);
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 11, results);
        int checks = 0;
        void Check(bool ok, string message) { if (!ok) throw new Exception(message); checks++; }
        void Near(float actual, float expected, string message) => Check(float.IsFinite(actual) && Math.Abs(actual - expected) < .0005f * Math.Max(1, Math.Abs(expected)), $"{message}: {actual} vs {expected}");
        float[] Run(float x, float y, float z, float distance)
        {
            GL.Uniform3(GL.GetUniformLocation(program, "direction"), x, y, z);
            GL.Uniform1(GL.GetUniformLocation(program, "distance"), distance);
            GL.BindBuffer(BufferTarget.UniformBuffer, buffer); GL.BufferSubData(BufferTarget.UniformBuffer, IntPtr.Zero, frame.Length * 4, frame);
            // This fixture reuses GPU-written UBO/SSBO bindings from earlier
            // atmosphere probes. Publish all prior writes before dispatch/readback.
            GL.MemoryBarrier(MemoryBarrierFlags.AllBarrierBits);
            GL.DispatchCompute(1, 1, 1); GL.MemoryBarrier(MemoryBarrierFlags.AllBarrierBits);
            var data = new float[32]; GL.BindBuffer(BufferTarget.ShaderStorageBuffer, results);
            GL.GetBufferSubData(BufferTarget.ShaderStorageBuffer, IntPtr.Zero, 128, data);
            Check(data.All(float.IsFinite), "finite fog/sky/cloud radiance"); return data;
        }
        try
        {
            foreach (float elevation in new[] { 90f, 5f, 0f, -3f, -30f })
            foreach (bool ready in new[] { true, false })
            foreach (bool shafts in new[] { false, true })
            foreach (int mode in new[] { 0, 1, 2, 3 })
            {
                Array.Clear(frame, 4, 4); Array.Clear(frame, 28, 4);
                frame[13] = MathF.Sin(elevation * MathF.PI / 180); frame[24] = ready ? 1 : 0; frame[25] = shafts ? 1 : 0;
                if (mode == 0) frame[4] = 1;
                if (mode == 1) frame[5] = 1;
                if (mode == 2) { frame[6] = -1; frame[7] = 60; }
                if (mode == 3) { frame[28] = frame[29] = frame[31] = 1; }
                var data = Run(1, 0, 0, 128);
                if (ready && elevation == 90 && mode == 0 && !shafts)
                    Console.WriteLine($"Dense fog GPU: sky Y={data[0]:F6}, dark terrain Y={data[4]:F6}, lit terrain Y={data[8]:F6}");
                for (int c = 0; c < 3; c++)
                {
                    if (mode == 0 && (elevation == 90 || elevation == 0 || elevation == -30))
                        Near(data[c], elevation == 90 ? 1.2f : elevation == 0 ? .47016707f : .1f, "retained daytime/horizon/night exposure at native gray");
                    foreach (int result in new[] { 1, 2, 3, 4, 5 }) Near(data[result * 4 + c], data[c], $"opaque fog equals sky (elevation={elevation}, LUT={ready}, shafts={shafts}, mode={mode}, result={result})");
                    Near(data[28 + c], 0, "opaque weather transmission");
                }
                Near(data[7], .7f, "fog preserves surface alpha");
            }
            // Ordinary haze must retain exponential visibility and linear completed-light transport.
            Array.Clear(frame, 4, 4); Array.Clear(frame, 28, 4); frame[24] = 1; frame[25] = 0; frame[13] = 1;
            foreach (float density in new[] { 0f, .0004f, .01f })
            {
                frame[4] = density; var data = Run(1, 0, 0, 100);
                // Air uses the new density response; the separate water
                // expectation below intentionally retains native density.
                float t = MathF.Exp(-density * (.05f + 7.23933f * density) * 100);
                for (int c = 0; c < 3; c++)
                {
                    Near(data[28 + c], t, "ordinary fog calibrated visibility");
                    Near(data[8 + c] - data[4 + c], (new[] { 10f, 3f, 1f }[c] - new[] { .01f, .02f, .03f }[c]) * t, "fog attenuates completed lighting once");
                    float waterT = MathF.Exp(-(new[] { .12f, .055f, .025f }[c] + density) * 100);
                    Near(data[24 + c], new[] { .8f, .6f, .4f }[c] * waterT + new[] { .06f, .1f, .14f }[c] * (1 - waterT), "native water spectral absorption/murk unchanged");
                }
            }
            // A known water interface keeps its own absorption, but the foreground air must match the sky.
            frame[4] = 0; frame[5] = 1; frame[35] = 1;
            GL.ActiveTexture(TextureUnit.Texture2); GL.BindTexture(TextureTarget.Texture2D, liquid);
            GL.TexSubImage2D(TextureTarget.Texture2D, 0, 0, 0, 1, 1, PixelFormat.Rgba, PixelType.Float, new[] { .75f, 1f, 1f, 1f });
            var split = Run(1, 0, 0, 128);
            for (int c = 0; c < 3; c++) { Near(split[4 + c], split[c], "opaque air before water matches weather sky"); Near(split[12 + c], split[c], "celestial air before water matches weather sky"); }
            // Submerged native minimum opacity converges to murk, without the displayed sky's gain.
            frame[10] = 1; frame[0] = .06f; frame[1] = .1f; frame[2] = .14f;
            frame[68] = .2f; frame[69] = .2f; frame[70] = .2f; frame[73] = 1; frame[35] = 0;
            var underwater = Run(1, 0, 0, 128);
            for (int c = 0; c < 3; c++) { Near(underwater[4 + c], frame[c], "water murk is not sky-exposed"); Near(underwater[12 + c], frame[c], "underwater sun converges to water murk"); }
            frame[10] = 0; frame[3] = 0; var disabled = Run(1, 0, 0, 128);
            Near(disabled[4], .01f, "disabled fog preserves radiance"); Near(disabled[7], .7f, "disabled fog preserves alpha");
            Console.WriteLine($"PASS fog/sky radiance: {checks} checks; day/twilight/night, LUT fallback, all weather terms, shadowed shafts, clouds, split air/water, retained water absorption");
        }
        finally
        {
            GL.BindBufferBase(BufferRangeTarget.UniformBuffer, 10, 0); GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 11, 0);
            GL.BindBuffer(BufferTarget.ShaderStorageBuffer, 0); GL.UseProgram(0);
            GL.DeleteProgram(program); GL.DeleteBuffer(buffer); GL.DeleteBuffer(results);
            foreach (int texture in new[] { lut, liquid, volume }) GL.DeleteTexture(texture);
        }
        Check(GL.GetError() == ErrorCode.NoError, "fog radiance GL NoError");
    }

    private static int Texture(int unit, params float[] pixel)
    {
        int texture = GL.GenTexture(); GL.ActiveTexture(TextureUnit.Texture0 + unit); GL.BindTexture(TextureTarget.Texture2D, texture);
        GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba32f, 1, 1, 0, PixelFormat.Rgba, PixelType.Float, pixel);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest); return texture;
    }
}
