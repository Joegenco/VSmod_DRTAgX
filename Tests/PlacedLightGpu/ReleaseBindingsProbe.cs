using System;
using System.Reflection;
using DRTAgX;
using OpenTK.Graphics.OpenGL4;
using Vintagestory.API.Client;
using Vintagestory.Client.NoObf;

/// <summary>Relink-generation caching and terrain SSBO subrange ownership.</summary>
internal static class ReleaseBindingsProbe
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    internal static void Run() { UniformCache(); TerrainRange(); QualityShaders(); RetainedQualityPrograms(); }

    private static void RetainedQualityPrograms()
    {
        var config = new AgxConfig(); FrameQuality.Publish(config);
        bool compiledPerformance = true, changeCaster = false;
        string casterText = "unchanged depth geometry";
        int reloads = 0;
        var stage = SurfaceApiProxy.Make<IShader>((method, _) => method.Name switch
        {
            "get_Code" => casterText,
            "get_PrefixCode" => compiledPerformance ? "#define DRT_PERFORMANCE_NO_PLS 1\n" : string.Empty,
            _ => throw new NotSupportedException(method.Name)
        });
        var program = SurfaceApiProxy.Make<IShaderProgram>((method, _) => method.Name switch
        {
            "get_VertexShader" or "get_FragmentShader" => stage,
            "get_Disposed" or "get_LoadError" => false,
            _ => throw new NotSupportedException(method.Name)
        });
        var render = SurfaceApiProxy.Make<IRenderAPI>((method, _) => method.Name == "GetEngineShader" ? program :
            throw new NotSupportedException(method.Name));
        var shaderApi = SurfaceApiProxy.Make<IShaderAPI>((method, _) =>
        {
            if (method.Name != "ReloadShaders") throw new NotSupportedException(method.Name);
            ++reloads; compiledPerformance = FrameQuality.Current.Performance;
            if (changeCaster) casterText = "edited depth geometry";
            return true;
        });
        var logger = ProbeAssets.Api([]).Logger;
        var api = SurfaceApiProxy.Make<ICoreClientAPI>((method, _) => method.Name switch
        {
            "get_Render" => render, "get_Shader" => shaderApi, "get_Logger" => logger,
            _ => throw new NotSupportedException(method.Name)
        });
        using (var owner = new PlacedLightQualityShaders(api))
        {
            owner.BeforeFrame();
            if (reloads != 1 || compiledPerformance || owner.CasterChangedThisFrame)
                throw new Exception("Retained Performance programs were not restored to Normal on a new lifetime");
            config.PerformanceMode = true; FrameQuality.Publish(config); owner.BeforeFrame();
            if (reloads != 2 || !compiledPerformance || owner.CasterChangedThisFrame)
                throw new Exception("Unchanged expanded caster code lost its retained depth contract");
            changeCaster = true; config.PerformanceMode = false; FrameQuality.Publish(config); owner.BeforeFrame();
            if (reloads != 3 || !owner.CasterChangedThisFrame)
                throw new Exception("Concurrent caster edits did not invalidate retained depth");
            owner.BeforeFrame();
            if (reloads != 3 || owner.CasterChangedThisFrame) throw new Exception("Caster edit invalidated every subsequent frame");
        }
        Console.WriteLine("PASS retained native programs: new lifetime restores Normal; identical caster text/options retain depth; concurrent shader edit invalidates once");
    }

    private static void QualityShaders()
    {
        var config = new AgxConfig(); FrameQuality.Publish(config);
        int reloads = 0, warnings = 0;
        bool failNext = false;
        var logger = ProbeAssets.Api([]).Logger;
        var shaderApi = SurfaceApiProxy.Make<IShaderAPI>((method, _) =>
        {
            if (method.Name != "ReloadShaders") throw new NotSupportedException(method.Name);
            if (!PlacedLightQualityShaders.QualityReload) throw new Exception("Quality reload did not own the boundary");
            ++reloads;
            bool success = !failNext; failNext = false; return success;
        });
        var countedLogger = SurfaceApiProxy.Make<Vintagestory.API.Common.ILogger>((method, args) =>
        {
            if (method.Name == "Warning") { ++warnings; return null; }
            return method.Invoke(logger, args);
        });
        var api = SurfaceApiProxy.Make<ICoreClientAPI>((method, _) => method.Name switch
        {
            "get_Shader" => shaderApi, "get_Logger" => countedLogger,
            _ => throw new NotSupportedException(method.Name)
        });
        using var owner = new PlacedLightQualityShaders(api);
        owner.BeforeFrame();
        if (reloads != 0 || warnings != 0) throw new Exception("Normal installed an unnecessary quality reload");
        for (int toggle = 0; toggle < 12; ++toggle)
        {
            config.PerformanceMode = !config.PerformanceMode; FrameQuality.Publish(config);
            owner.BeforeFrame();
            if (reloads != toggle + 1 || PlacedLightQualityShaders.QualityReload || !owner.CasterChangedThisFrame)
                throw new Exception("Mode change did not reload exactly once and retire its boundary");
            // Prime tiering and the allocation counter before measuring the
            // steady-state path, independently of reflection/proxy setup.
            for (int frame = 0; frame < 50000; ++frame) owner.BeforeFrame();
            if (owner.CasterChangedThisFrame) throw new Exception("Unknown caster contract invalidated depth repeatedly");
            _ = GC.GetAllocatedBytesForCurrentThread();
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int frame = 0; frame < 10000; ++frame) owner.BeforeFrame();
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            if (allocated != 0 || reloads != toggle + 1)
                throw new Exception($"Unchanged quality allocated {allocated} bytes or repeated shader reloads {reloads}/{toggle + 1}");
        }
        config.PerformanceMode = true; FrameQuality.Publish(config); failNext = true;
        owner.BeforeFrame();
        if (reloads != 14 || warnings != 1 || !config.StaticLightShadows || PlacedLightQualityShaders.QualityReload)
            throw new Exception("Specialization failure did not restore regular programs/preserve preferences");
        for (int frame = 0; frame < 10000; ++frame) owner.BeforeFrame();
        if (reloads != 14 || warnings != 1) throw new Exception("Failed specialization retried every frame");
        config.PerformanceMode = false; FrameQuality.Publish(config);
        Console.WriteLine("PASS PLS quality shaders: twelve transitions, one reload/transition, zero warm allocations, one failure fallback/diagnostic, preferences preserved");
    }

    private static void UniformCache()
    {
        using var original = new ShadowGlState();
        using var owner = new DrtagxModSystem();
        const string vertex = "#version 430 core\nvoid main(){gl_Position=vec4(0);}";
        string Fragment(int first) => "#version 430 core\n" +
            $"layout(location={first})uniform int drtDebugView;\nlayout(location={first + 1})uniform int drtContactShadowsEnabled;\n" +
            $"layout(location={first + 2})uniform int drtShadowGridEnabled;\nlayout(location={first + 3})uniform int drtStaticDebugState;\n" +
            $"layout(location={first + 4})uniform vec4 drtStaticDebugCounts;\n" +
            "out vec4 color;void main(){color=drtStaticDebugCounts+vec4(drtDebugView,drtContactShadowsEnabled,drtShadowGridEnabled,drtStaticDebugState);}";
        int program = ProbeShader.Program((ShaderType.VertexShader, vertex), (ShaderType.FragmentShader, Fragment(0)));
        int incoming = ProbeShader.Program((ShaderType.VertexShader, vertex), (ShaderType.FragmentShader, Fragment(0)));
        IShaderProgram Shader() => SurfaceApiProxy.Make<IShaderProgram>((method, _) => method.Name == "get_ProgramId" ? program : throw new NotSupportedException(method.Name));
        var shader = Shader();
        // This fixture verifies ordinary-user settings. Own and restore the
        // in-process flag rather than relying on the developer's saved default.
        bool previousDeveloperMode = ClientSettings.DeveloperMode;
        try {
            ClientSettings.DeveloperMode = false;
            owner.Config.ContactShadows = false; owner.Config.SunShadowGridEnabled = false;
            GL.UseProgram(incoming); Call(owner, "SetStaticDebugUniforms", shader, 2);
            object cached = Get(owner, "_staticDebugLocations");
            Call(owner, "SetStaticDebugUniforms", shader, 3);
            if (!ReferenceEquals(cached, Get(owner, "_staticDebugLocations"))) throw new Exception("Warm debug uniforms did not reuse locations");
            CheckUniforms(3);
            // Explicit locations change while the same GL name and shader
            // object survive. Only reload can make the cached locations valid.
            GL.GetProgram(program, GetProgramParameterName.AttachedShaders, out int count);
            int[] attached = new int[count]; GL.GetAttachedShaders(program, count, out _, attached);
            foreach (int old in attached) GL.DetachShader(program, old);
            foreach (var stage in new[] { (ShaderType.VertexShader, vertex), (ShaderType.FragmentShader, Fragment(8)) }) {
                int part = GL.CreateShader(stage.Item1); GL.ShaderSource(part, stage.Item2); GL.CompileShader(part);
                GL.GetShader(part, ShaderParameter.CompileStatus, out int compiled);
                if (compiled == 0) throw new Exception(GL.GetShaderInfoLog(part));
                GL.AttachShader(program, part); GL.DeleteShader(part);
            }
            GL.LinkProgram(program); GL.GetProgram(program, GetProgramParameterName.LinkStatus, out int linked);
            if (linked == 0) throw new Exception(GL.GetProgramInfoLog(program));
            if (!(bool)Call(owner, "ReloadStaticLightShaders") || Get(owner, "_staticDebugLocations") != null)
                throw new Exception("Shader reload retained obsolete debug locations");
            GL.UseProgram(incoming); Call(owner, "SetStaticDebugUniforms", shader, 4); CheckUniforms(4);
            cached = Get(owner, "_staticDebugLocations");
            Call(owner, "SetStaticDebugUniforms", Shader(), 5); CheckUniforms(5);
            if (ReferenceEquals(cached, Get(owner, "_staticDebugLocations"))) throw new Exception("New shader identity retained old cache");
            if (GL.GetError() != ErrorCode.NoError) throw new Exception("Debug uniform reload GL error");
            Console.WriteLine("PASS debug uniform cache: warm reuse, actual relink with the same program/shader, reload invalidation, new identity; contact/grid settings upload outside developer mode and raw program restores");

            void CheckUniforms(int expectedState) {
                if (ClientSettings.DeveloperMode) throw new Exception("Standalone fixture expected developer mode disabled");
                foreach (string name in new[] { "drtDebugView", "drtContactShadowsEnabled", "drtShadowGridEnabled", "drtStaticDebugState" }) {
                    GL.GetUniform(program, GL.GetUniformLocation(program, name), out int actual);
                    if (actual != (name == "drtStaticDebugState" ? expectedState : 0)) throw new Exception("Debug/settings upload changed " + name);
                }
                if (GL.GetInteger(GetPName.CurrentProgram) != incoming) throw new Exception("Debug upload leaked raw program");
            }
        }
        finally {
            ClientSettings.DeveloperMode = previousDeveloperMode;
            GL.UseProgram(0); GL.DeleteProgram(program); GL.DeleteProgram(incoming);
        }
    }

    private static void TerrainRange()
    {
        using var maps = new StaticTerrainShadowMaps(null);
        using var tiles = new StaticLightTileBindings();
        int oldActive = GL.GetInteger(GetPName.ActiveTexture);
        GL.ActiveTexture(TextureUnit.Texture14);
        int oldTexture = GL.GetInteger(GetPName.TextureBinding2DArray), oldSampler = GL.GetInteger(GetPName.SamplerBinding);
        int texture = GL.GenTexture(), foreign = GL.GenTexture(), sampler = GL.GenSampler();
        int source = GL.GenBuffer(), oldGeneric = GL.GetInteger(GetPName.ShaderStorageBufferBinding);
        GL.BindBuffer(BufferTarget.ShaderStorageBuffer, source); GL.BufferData(BufferTarget.ShaderStorageBuffer, 512, IntPtr.Zero, BufferUsageHint.StaticDraw);
        GL.BindBuffer(BufferTarget.ShaderStorageBuffer, oldGeneric);
        Set(maps, "_texture", texture); typeof(StaticTerrainShadowMaps).GetProperty("Ready", Private).SetValue(maps, true);
        Set(tiles, "_sourceBuffer", source); typeof(StaticLightTileBindings).GetProperty("PublishedCount", Private).SetValue(tiles, 1);
        int program = ProbeShader.Program(
            (ShaderType.VertexShader, "#version 430 core\nvoid main(){gl_Position=vec4(0);}"),
            (ShaderType.FragmentShader, "#version 430 core\nuniform int drtTerrainPlacedCount;uniform sampler2DArray drtStaticMaps;out vec4 color;void main(){color=texture(drtStaticMaps,vec3(0))*float(drtTerrainPlacedCount);}"));
        int previousProgram = GL.GetInteger(GetPName.CurrentProgram);
        try {
            using var foreignRanges = new BorrowedBufferRanges(BufferRangeTarget.ShaderStorageBuffer, 4);
            GL.ActiveTexture(TextureUnit.Texture14); GL.BindTexture(TextureTarget.Texture2DArray, foreign); GL.BindSampler(14, sampler);
            GL.ActiveTexture(TextureUnit.Texture9); GL.UseProgram(program);
            using var bindings = new PlacedLightTerrainBindings(maps, tiles) { Enabled = true };
            Call(bindings, "Bind", program);
            GL.GetInteger(GetIndexedPName.ShaderStorageBufferBinding, 4, out int bound);
            if (bound != source) throw new Exception("Terrain fixture did not borrow the source SSBO");
            GL.GetUniform(program, GL.GetUniformLocation(program, "drtTerrainPlacedCount"), out int count);
            if (count != 1) throw new Exception("Terrain fixture did not publish its source count");
            Call(bindings, "Restore"); foreignRanges.AssertRestored("PlacedLightTerrainBindings");
            if (GL.GetInteger(GetPName.ActiveTexture) != (int)TextureUnit.Texture9) throw new Exception("Terrain binder leaked active unit");
            GL.ActiveTexture(TextureUnit.Texture14);
            if (GL.GetInteger(GetPName.TextureBinding2DArray) != foreign || GL.GetInteger(GetPName.SamplerBinding) != sampler)
                throw new Exception("Terrain binder leaked texture/sampler");
            if (GL.GetError() != ErrorCode.NoError) throw new Exception("Terrain range restoration GL error");
            Console.WriteLine("PASS placed terrain binder: SSBO4 name/offset/size, distinct generic binding, array texture/sampler and active unit restored");
        }
        finally {
            GL.ActiveTexture(TextureUnit.Texture14); GL.BindTexture(TextureTarget.Texture2DArray, oldTexture); GL.BindSampler(14, oldSampler);
            GL.ActiveTexture((TextureUnit)oldActive); GL.UseProgram(previousProgram);
            GL.DeleteTexture(foreign); GL.DeleteSampler(sampler); GL.DeleteProgram(program);
            // Production map/tile owners dispose the injected texture/buffer.
        }
    }
    private static object Get(object target, string field) => target.GetType().GetField(field, Private).GetValue(target);
    private static void Set(object target, string field, object value) => target.GetType().GetField(field, Private).SetValue(target, value);
    private static object Call(object target, string method, params object[] arguments) => target.GetType().GetMethod(method, Private).Invoke(target, arguments);
}
