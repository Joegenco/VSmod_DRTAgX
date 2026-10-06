using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using DRTAgX;
using HarmonyLib;
using OpenTK.Graphics.OpenGL4;
using Vintagestory.API.Client;
using Vintagestory.Client.NoObf;

/// <summary>Production allocation and empty native terrain rasterization with foreign GL state.</summary>
internal static class ShadowSafetyProbe
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    internal static void Run()
    {
        ProbeAssets.Api([]);
        MovingAllocation(); StaticClearDepth();
    }

    private static void MovingAllocation()
    {
        using var original = new HdrPassState();
        using var owner = new MovingLightShadowRenderer();
        Array.Fill((int[])Get(owner, "_handSources"), -1);
        int unpack = GL.GenBuffer();
        var render = SurfaceApiProxy.Make<IRenderAPI>((method, _) => method.Name switch {
            "get_FrameWidth" => 1920, "get_FrameHeight" => 1080, _ => throw new NotSupportedException(method.Name)
        });
        var logger = SurfaceApiProxy.Make<Vintagestory.API.Common.ILogger>((_, _) => null);
        var api = SurfaceApiProxy.Make<ICoreClientAPI>((method, _) => method.Name switch {
            "get_Render" => render, "get_Logger" => logger, _ => throw new NotSupportedException(method.Name)
        });
        try {
            GL.BindBuffer(BufferTarget.PixelUnpackBuffer, unpack);
            GL.BufferData(BufferTarget.PixelUnpackBuffer, 4, IntPtr.Zero, BufferUsageHint.StaticDraw);
            int draw = GL.GetInteger(GetPName.DrawFramebufferBinding), read = GL.GetInteger(GetPName.ReadFramebufferBinding);
            int texture = GL.GetInteger(GetPName.TextureBinding2D), active = GL.GetInteger(GetPName.ActiveTexture);
            foreach (int capacity in new[] { 2, 3 }) {
                if (!(bool)Call(owner, "Ensure", api, capacity)) throw new Exception("Moving atlas allocation failed with foreign PBO");
                if (GL.GetInteger(GetPName.PixelUnpackBufferBinding) != unpack || GL.GetInteger(GetPName.DrawFramebufferBinding) != draw ||
                    GL.GetInteger(GetPName.ReadFramebufferBinding) != read || GL.GetInteger(GetPName.TextureBinding2D) != texture || GL.GetInteger(GetPName.ActiveTexture) != active)
                    throw new Exception("Moving atlas allocation leaked foreign bindings");
                if (GL.GetError() != ErrorCode.NoError) throw new Exception("Moving atlas read a tiny unpack PBO as texture pixels");
            }
            Console.WriteLine("PASS moving shadow allocation/resize: a four-byte foreign unpack PBO, framebuffer and texture bindings remain intact");
        }
        finally { GL.DeleteBuffer(unpack); }
    }

    private static void StaticClearDepth()
    {
        using var original = new ShadowGlState();
        using var terrain = new MovingLightShadowRenderer();
        using var maps = new StaticTerrainShadowMaps(terrain);
        // Empty native pool arrays exercise real field readers, pass setup,
        // layer clears and restoration without populating a synthetic scene graph.
        var world = (ClientMain)RuntimeHelpers.GetUninitializedObject(typeof(ClientMain));
        var renderer = (ChunkRenderer)RuntimeHelpers.GetUninitializedObject(typeof(ChunkRenderer));
        MeshDataPoolManager[][] passes = [[], [], [], [], [], []];
        AccessTools.Field(typeof(ClientMain), "chunkRenderer").SetValue(world, renderer);
        AccessTools.Field(typeof(ChunkRenderer), "poolsByRenderPass").SetValue(renderer, passes);
        AccessTools.Field(typeof(ChunkRenderer), "textureIds").SetValue(renderer, Array.Empty<int>());
        int program = ProbeShader.Program(
            (ShaderType.VertexShader, "#version 430 core\nuniform mat4 mvpMatrix;uniform vec3 origin;void main(){gl_Position=mvpMatrix*vec4(origin,1);}"),
            (ShaderType.FragmentShader, "#version 430 core\nuniform sampler2D tex2d;void main(){gl_FragDepth=texture(tex2d,vec2(.5)).r;}"));
        IShaderProgram active = null, shader = null;
        shader = SurfaceApiProxy.Make<IShaderProgram>((method, _) => {
            switch (method.Name) {
                case "get_ProgramId": return program;
                case "get_Disposed": case "get_LoadError": return false;
                case "Use": GL.UseProgram(program); active = shader; return null;
                case "Stop": GL.UseProgram(0); active = null; return null;
                default: throw new NotSupportedException(method.Name);
            }
        });
        var render = SurfaceApiProxy.Make<IRenderAPI>((method, _) => method.Name switch {
            "GetEngineShader" => shader, "get_CurrentActiveShader" => active, _ => throw new NotSupportedException(method.Name)
        });
        var logger = SurfaceApiProxy.Make<Vintagestory.API.Common.ILogger>((_, _) => null);
        var api = SurfaceApiProxy.Make<ICoreClientAPI>((method, _) => method.Name switch {
            "get_Render" => render, "get_World" => world, "get_Logger" => logger, _ => throw new NotSupportedException(method.Name)
        });
        int texture = GL.GenTexture(), framebuffer = GL.GenFramebuffer();
        try {
            GL.BindTexture(TextureTarget.Texture2DArray, texture);
            GL.TexStorage3D(TextureTarget3d.Texture2DArray, 1, SizedInternalFormat.DepthComponent24,
                StaticTerrainShadowMaps.FaceSize, StaticTerrainShadowMaps.FaceSize, 12);
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, framebuffer);
            GL.FramebufferTextureLayer(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment, texture, 0, 6);
            GL.DrawBuffer(DrawBufferMode.None); GL.ReadBuffer(ReadBufferMode.None);
            typeof(StaticTerrainShadowMaps).GetProperty("Capacity", Private).SetValue(maps, 1);
            Set(maps, "_texture", texture); Set(maps, "_framebuffer", framebuffer);
            Set(maps, "_bake", new PlacedLightBake { Active = true, Slot = 0 });
            GL.ClearDepth(.23); double clearDepth = GL.GetDouble(GetPName.DepthClearValue);
            var light = new StaticTerrainShadowMaps.ActiveLight(new StaticLightSources.Source(0, 0, 0, 0, 0, 20), 1, 1, 0);
            if (!(bool)Call(maps, "RenderSource", api, light) || maps.FacesBakedThisFrame != 2)
                throw new Exception("Empty native static bake failed");
            if (GL.GetDouble(GetPName.DepthClearValue) != clearDepth || active != null)
                throw new Exception("Static bake leaked entry clear depth or managed shader");
            GL.BindFramebuffer(FramebufferTarget.ReadFramebuffer, framebuffer);
            for (int layer = 6; layer < 8; layer++) {
                GL.FramebufferTextureLayer(FramebufferTarget.ReadFramebuffer, FramebufferAttachment.DepthAttachment, texture, 0, layer);
                float[] pixel = new float[1]; GL.ReadPixels(0, 0, 1, 1, PixelFormat.DepthComponent, PixelType.Float, pixel);
                if (pixel[0] != 1f) throw new Exception("Static bake inherited foreign clear depth");
            }
            if (GL.GetError() != ErrorCode.NoError) throw new Exception("Static clear depth GL error");
            Console.WriteLine("PASS static native bake: both staging faces clear to far depth 1; incoming .23 clear value and shader state restored");
        }
        finally {
            GL.UseProgram(0); GL.DeleteProgram(program);
            // The production maps own the injected texture/FBO and dispose them.
        }
    }
    private static object Get(object target, string field) => target.GetType().GetField(field, Private).GetValue(target);
    private static void Set(object target, string field, object value) => target.GetType().GetField(field, Private).SetValue(target, value);
    private static object Call(object target, string method, params object[] arguments) => target.GetType().GetMethod(method, Private).Invoke(target, arguments);
}
