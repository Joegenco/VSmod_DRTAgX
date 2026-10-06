using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using DRTAgX;
using OpenTK.Graphics.OpenGL4;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;

/// <summary>Run the actual HDR owner, resources and shaders against synthetic scene pixels.</summary>
internal static class HdrPipelineProbe
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    internal static void Run(string directory)
    {
        ProbeAssets.Api([]);
        using var saved = new HdrPassState();
        using var shaders = new HdrShaderFixture(directory);
        int scene = GL.GenTexture(), glow = GL.GenTexture();
        int incomingProgram = ProbeShader.Program(
            (ShaderType.VertexShader, "#version 430 core\nvoid main(){gl_Position=vec4(0);}"),
            (ShaderType.FragmentShader, "#version 430 core\nout vec4 color;void main(){color=vec4(1);}"));
        try {
            Fill(scene, 64, 32, .7f); Fill(glow, 64, 32, 0f);
            var primary = new FrameBufferRef { Width = 64, Height = 32, ColorTextureIds = [scene, glow] };
            var buffers = new List<FrameBufferRef> { primary };
            var entity = (EntityPlayer)RuntimeHelpers.GetUninitializedObject(typeof(EntityPlayer));
            var player = HdrFixturePlayer.Create(entity);
            if (!ReferenceEquals(player.Entity, entity)) throw new Exception("Fixture player entity was not populated");
            var world = SurfaceApiProxy.Make<IClientWorldAccessor>((method, _) => method.Name == "get_Player" ? player : throw new NotSupportedException(method.Name));
            var render = SurfaceApiProxy.Make<IRenderAPI>((method, _) => method.Name switch {
                "get_FrameBuffers" => buffers, "get_CurrentActiveShader" => shaders.Active,
                _ => throw new NotSupportedException(method.Name)
            });
            var events = SurfaceApiProxy.Make<IClientEventAPI>((_, _) => null);
            var logger = SurfaceApiProxy.Make<ILogger>((_, _) => null);
            var api = SurfaceApiProxy.Make<ICoreClientAPI>((method, _) => method.Name switch {
                "get_Render" => render, "get_World" => world, "get_Shader" => shaders.Api,
                "get_Event" => events, "get_Logger" => logger, _ => throw new NotSupportedException(method.Name)
            });
            using var owner = new HdrPostProcessor(api);
            var resources = (HdrMipResources)typeof(HdrPostProcessor).GetField("_resources", Private).GetValue(owner);
            var config = new AgxConfig { BloomEnabled = false, AutoExposureEnabled = true };
            Check(Draw(() => owner.Render(config, .1f)) == 2 && owner.Ready, "exposure-only uses two fullscreen draws");
            float[] meter = Read(resources.Meter, resources.MeterLod, 2);
            float ev = Read(owner.ExposureTexture, 0, 1)[0];
            // Compare with the pre-cleanup six-draw schedule using the same
            // production methods/shaders, a reset exposure and identical input.
            resources.HasExposure = false;
            typeof(HdrPostProcessor).GetField("_bloomActive", Private).SetValue(owner, true);
            Check(Draw(() => {
                using var guard = new HdrPassState();
                GL.BindVertexArray((int)typeof(HdrPostProcessor).GetField("_triangleVao", Private).GetValue(owner));
                Call(owner, "RenderPyramid", scene, glow, 64, 32, true);
                Call(owner, "AdaptExposure", config, .1f);
            }) == 6, "reference exposure schedule uses six fullscreen draws");
            Equal(meter, Read(resources.Meter, resources.MeterLod, 2), "meter matches the previous schedule exactly");
            Equal([ev], Read(owner.ExposureTexture, 0, 1), "auto EV matches the previous schedule exactly");
            Check(Math.Abs(ev + 1f) < .002f, "authored .35 target meters constant .7 input at -1 EV");
            for (int i = 1; i < HdrMipResources.LevelCount; i++) Fill(resources.Scene[i], resources.Width[i], resources.Height[i], 3f);
            Check(Draw(() => owner.Render(config, .1f)) == 2 && owner.BloomTexture == 0, "disabled bloom remains inactive");
            for (int i = 1; i < HdrMipResources.LevelCount; i++)
                Check(Read(resources.Scene[i], 0, resources.Width[i] * resources.Height[i] * 4)[0] == 3f, "exposure skips deeper RGB level " + i);
            config.BloomEnabled = true;
            Check(Draw(() => owner.Render(config, .1f)) == 17 && owner.BloomTexture != 0, "bloom re-enable regenerates all levels and adaptation");
            for (int i = 1; i < HdrMipResources.LevelCount; i++)
                Check(Math.Abs(Read(resources.Scene[i], 0, resources.Width[i] * resources.Height[i] * 4)[0] - .7f) < .003f, "bloom refreshes deeper RGB level " + i);
            config.AutoExposureEnabled = false;
            Check(Draw(() => owner.Render(config, .1f)) == 16 && owner.ExposureTexture == 0, "bloom-only retains sixteen draws");
            config.BloomEnabled = false;
            Check(Draw(() => owner.Render(config, .1f)) == 0 && owner.Ready, "both disabled performs no fullscreen draws");
            config.AutoExposureEnabled = true;
            IShaderProgram incoming = null;
            incoming = SurfaceApiProxy.Make<IShaderProgram>((method, _) => {
                if (method.Name == "Use") { shaders.Active = incoming; GL.UseProgram(incomingProgram); return null; }
                if (method.Name == "Stop") { shaders.Active = null; GL.UseProgram(0); return null; }
                throw new NotSupportedException(method.Name);
            });
            foreach (bool managed in new[] { false, true })
            foreach (string failure in new[] { "Use", "Uniform", "Stop" }) {
                shaders.Active = managed ? incoming : null;
                GL.UseProgram(0); shaders.Failure = "hdr_downsample:" + failure;
                owner.Render(config, .1f);
                Check(!owner.Ready && ReferenceEquals(shaders.Active, managed ? incoming : null) && GL.GetInteger(GetPName.CurrentProgram) == 0,
                    "HDR failure restores managed/raw shader after " + failure);
                shaders.Failure = null; owner.Render(config, .1f);
                Check(owner.Ready, "HDR recovers on the next frame after " + failure);
            }
            HdrFinalInputsProbe.Run(owner, shaders, config);
            // Both quality modes meter the same half-resolution extracted scene and retain .95 bloom gain.
            config.BloomEnabled = true; config.AutoExposureEnabled = true;
            FrameQuality.Publish(config);
            owner.Render(config, .1f);
            float[] normalMeter = Read(resources.Meter, resources.MeterLod, 2);
            config.PerformanceMode = true; FrameQuality.Publish(config);
            Check(Draw(() => owner.Render(config, .1f)) == 15, "Performance uses four scales plus independent meter/adaptation");
            Check(resources.ActiveLevels == 4 && resources.Width[0] == 16 && resources.MeterWidth == 32, "Performance quarter bloom retains half-resolution meter");
            Equal(normalMeter, Read(resources.Meter, resources.MeterLod, 2), "mode toggle preserves the metering input exactly");
            Check(Math.Abs(Read(owner.ExposureTexture, 0, 1)[0] + 1f) < .002f, "Performance retains EV convergence");
            Check(Math.Abs(Read(owner.BloomTexture, 0, 4)[0] - .7f * .95f) < .004f, "Performance bloom preserves constant radiance gain");
            for (int toggle = 0; toggle < 10; ++toggle)
            {
                config.PerformanceMode = !config.PerformanceMode; FrameQuality.Publish(config);
                owner.Render(config, .1f);
                Check(owner.Ready && resources.ActiveLevels == (config.PerformanceMode ? 4 : 5), "repeated HDR mode recreation " + toggle);
            }
            config.PerformanceMode = false; FrameQuality.Publish(config); owner.Render(config, .1f);
            Check(resources.Width[0] == 32 && resources.ActiveLevels == 5, "Normal restores five scales and sizing");
            if (GL.GetError() != ErrorCode.NoError) throw new Exception("HDR pipeline GL error");
            Console.WriteLine("PASS actual HDR pipeline: unchanged meter/EV, four eliminated draws, bloom toggles and partial native-call recovery");
        }
        finally { GL.UseProgram(0); GL.DeleteProgram(incomingProgram); GL.DeleteTexture(scene); GL.DeleteTexture(glow); }
    }

    private static void Call(object target, string method, params object[] arguments) => target.GetType().GetMethod(method, Private).Invoke(target, arguments);
    private static int Draw(Action action) {
        int query = GL.GenQuery(); GL.BeginQuery(QueryTarget.PrimitivesGenerated, query);
        try { action(); }
        finally { GL.EndQuery(QueryTarget.PrimitivesGenerated); }
        GL.GetQueryObject(query, GetQueryObjectParam.QueryResult, out int triangles); GL.DeleteQuery(query);
        return triangles;
    }
    private static void Fill(int texture, int width, int height, float value) {
        float[] pixels = new float[width * height * 4]; Array.Fill(pixels, value);
        GL.ActiveTexture(TextureUnit.Texture0); GL.BindTexture(TextureTarget.Texture2D, texture);
        GL.BindBuffer(BufferTarget.PixelUnpackBuffer, 0);
        GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba16f, width, height, 0, PixelFormat.Rgba, PixelType.Float, pixels);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
    }
    private static float[] Read(int texture, int level, int components) {
        GL.ActiveTexture(TextureUnit.Texture0); GL.BindTexture(TextureTarget.Texture2D, texture);
        // GetTexImage writes the entire mip, even when an assertion only reads its first pixel.
        GL.GetTexLevelParameter(TextureTarget.Texture2D,level,GetTextureParameter.TextureWidth,out int width);
        GL.GetTexLevelParameter(TextureTarget.Texture2D,level,GetTextureParameter.TextureHeight,out int height);
        float[] result = new float[checked(width * height * components)];
        GL.GetTexImage(TextureTarget.Texture2D, level, components == 1 ? PixelFormat.Red : components == 2 ? PixelFormat.Rg : PixelFormat.Rgba, PixelType.Float, result);
        return result;
    }
    private static void Equal(float[] expected, float[] actual, string name) {
        if (expected.Length != actual.Length) throw new Exception(name);
        for (int i = 0; i < expected.Length; i++) if (expected[i] != actual[i]) throw new Exception(name);
    }
    private static void Check(bool condition, string name) { if (!condition) throw new Exception(name); }
}
