#nullable enable
using System;
using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using DRTAgX;
using Vintagestory.API.Client;
using Vintagestory.API.Common;

// A fixture implements the already captured Sheyder ABI; the actual production
// bridge resolves it and Harmony patches its late render entry point.
internal static class VolumetricBridgeProbe
{
    public class ApiProxy : DispatchProxy
    {
        internal System.Func<MethodInfo, object?>? Handler;
        protected override object? Invoke(MethodInfo? method, object?[]? args) => Handler!(method!);
    }
    private static T Proxy<T>(System.Func<MethodInfo, object?> handler) where T : class
    {
        T proxy = DispatchProxy.Create<T, ApiProxy>();
        ((ApiProxy)(object)proxy).Handler = handler;
        return proxy;
    }
    private sealed class SystemFixture : ModSystem
    {
        private readonly IEnumerable _renderers;
        internal SystemFixture(object renderer) => _renderers = new[] { renderer };
    }
    // Real quality owner must leave fog's original getter/config/allocation alone.
    internal static void VerifyNativeQuality()
    {
        var config = new SheyderMod.Features.VolumetricFog.ConfigFixture();
        config.VolumetricFog.StepCount = 23;
        config.VolumetricFog.Smoothing = 2.56f;
        var renderer = new SheyderMod.Features.VolumetricFog.VolumetricFogRenderer(config);
        var system = new SystemFixture(renderer);
        var loader = Proxy<IModLoader>(method => method.Name == "GetModSystem" ? system : null);
        int warnings = 0;
        var logger = Proxy<ILogger>(method => { if (method.Name == "Warning") ++warnings; return null; });
        var api = Proxy<ICoreClientAPI>(method => method.Name switch
        {
            "get_ModLoader" => loader, "get_Logger" => logger, _ => throw new NotSupportedException(method.Name)
        });
        var field = typeof(SheyderMod.Features.VolumetricFog.VolumetricFogRenderer).GetField("_getConfig", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var getter = (Func<SheyderMod.Features.VolumetricFog.ConfigFixture>)field.GetValue(renderer)!;
        bool previousPerformance = FrameQuality.Current.Performance;
        var quality = new AgxConfig();
        try
        {
            using var owner = new SheyderQualityAdapter(api);
            for (int i = 0; i < 12; ++i)
            {
                quality.PerformanceMode = (i & 1) != 0; FrameQuality.Publish(quality);
                if (!ReferenceEquals(field.GetValue(renderer), getter) || !ReferenceEquals(getter(), config) ||
                    getter().VolumetricFog.StepCount != 23 || getter().VolumetricFog.Smoothing != 2.56f)
                    throw new Exception("Quality mode modifies native fog settings/getter");
                var allocation = HarmonyLib.AccessTools.Method(renderer.GetType(), "EnsureScatterFramebuffer");
                var patches = HarmonyLib.Harmony.GetPatchInfo(allocation);
                if (patches != null)
                    foreach (var patch in patches.Transpilers)
                        if (patch.owner == "drtagx.quality.sheyder") throw new Exception("Quality mode modifies native fog allocation");
            }
            if (warnings != 0) throw new Exception("Native quality fixture emitted warnings");
        }
        finally { quality.PerformanceMode = previousPerformance; FrameQuality.Publish(quality); }
        Console.WriteLine("PASS native fog quality: twelve Normal/Performance transitions preserve configured 23 steps, smoothing, getter identity and original half-resolution allocation");
    }

    internal static void Run()
    {
        ProbeAssets.Api([]); // Resolve optional native API dependencies.
        var config = new SheyderMod.Features.VolumetricFog.ConfigFixture();
        var renderer = new SheyderMod.Features.VolumetricFog.VolumetricFogRenderer(config);
        var system = new SystemFixture(renderer);
        var loader = Proxy<IModLoader>(method => method.Name == "GetModSystem" ? system : throw new NotSupportedException(method.Name));
        var logger = Proxy<ILogger>(_ => null);
        var api = Proxy<ICoreClientAPI>(method => method.Name switch
        {
            "get_ModLoader" => loader, "get_Logger" => logger, _ => throw new NotSupportedException(method.Name)
        });
        var bridge = new AtmosphereSheyderBridge(api);
        try
        {
            if (!bridge.LegacyAvailable) throw new Exception("Captured Sheyder entry point not resolved");
            foreach (bool staleReplacement in new[] { false, true })
            {
                bridge.ReplacementReady = staleReplacement;
                int calls = SheyderMod.Features.VolumetricFog.VolumetricFogRenderer.Calls;
                SheyderMod.Features.VolumetricFog.VolumetricFogRenderer.InvokeLate();
                if (SheyderMod.Features.VolumetricFog.VolumetricFogRenderer.Calls != calls + 1) throw new Exception("Native fog pass still suppressed");
            }
            Console.WriteLine("PASS real Harmony fog bridge: native late pass executes even with stale replacement flag");
            config.VolumetricFog.Intensity = .05f;
            if (Math.Abs(bridge.Strength - .5f) > 1e-6f) throw new Exception("Native intensity snapshot stale");
            config.VolumetricFog.Enabled = false;
            if (bridge.Strength != 0f) throw new Exception("Disabled native fog setting ignored");
            Console.WriteLine("PASS fog bridge retains live Sheyder enabled/intensity settings");
            // Exercise the retained compatibility fallback using the already
            // installed prefix: suppress only while an owned replacement exists.
            typeof(AtmosphereSheyderBridge).GetProperty("LegacyAvailable", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(bridge, false);
            int before = SheyderMod.Features.VolumetricFog.VolumetricFogRenderer.Calls;
            bridge.ReplacementReady = true;
            SheyderMod.Features.VolumetricFog.VolumetricFogRenderer.InvokeLate();
            if (SheyderMod.Features.VolumetricFog.VolumetricFogRenderer.Calls != before) throw new Exception("Duplicate compatibility fog pass");
            bridge.ReplacementReady = false;
            SheyderMod.Features.VolumetricFog.VolumetricFogRenderer.InvokeLate();
            if (SheyderMod.Features.VolumetricFog.VolumetricFogRenderer.Calls != before + 1) throw new Exception("Failed replacement did not restore native pass");
            Console.WriteLine("PASS fog compatibility fallback suppresses only its active replacement");
        }
        finally { bridge.Dispose(); }
        int old = SheyderMod.Features.VolumetricFog.VolumetricFogRenderer.Calls;
        SheyderMod.Features.VolumetricFog.VolumetricFogRenderer.InvokeLate();
        if (SheyderMod.Features.VolumetricFog.VolumetricFogRenderer.Calls != old + 1) throw new Exception("Fog bridge disposal left native pass suppressed");
        Console.WriteLine("PASS fog bridge disposal unpatches original late pass");
    }
}

namespace SheyderMod.Features.VolumetricFog
{
    public sealed class FogFixture { public bool Enabled { get; set; } = true; public float Intensity { get; set; } = .1f; public int StepCount { get; set; } = 16; public float Smoothing { get; set; } = 5; }
    public sealed class ConfigFixture { public FogFixture VolumetricFog { get; set; } = new(); }
    internal sealed class VolumetricFogRenderer
    {
        private readonly Func<ConfigFixture> _getConfig;
        internal static int Calls;
        internal static VolumetricFogRenderer? _instance;
        // Retain captured renderer ABI fields for lifecycle fixtures.
        internal int _scatterTexId=0, _depthTexId=0, _scatterWidth=0, _scatterHeight=0;
        // Mirror only the captured production fields used by the native uniform hook.
        internal int _liquidDepthTexId = 0;
        internal readonly float[] _invProj = { 1,0,0,0, 0,1,0,0, 0,0,0,0, 0,0,1,1 };
        [MethodImpl(MethodImplOptions.NoInlining)]
        private void ApplyScatterUniforms(Vintagestory.Client.NoObf.ShaderProgram prog) { }
        internal void Uniforms(Vintagestory.Client.NoObf.ShaderProgram prog) => ApplyScatterUniforms(prog);
        internal VolumetricFogRenderer(ConfigFixture config) { _getConfig = () => config; _instance = this; }
        [MethodImpl(MethodImplOptions.NoInlining)]
        private void EnsureScatterFramebuffer() { }
        internal void Allocated() => EnsureScatterFramebuffer();
        [MethodImpl(MethodImplOptions.NoInlining)]
        private void DisposeScatterFramebuffer() { }
        internal void Released() => DisposeScatterFramebuffer();
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void RenderScatterAndBindFinal() => ++Calls;
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void InvokeLate() => RenderScatterAndBindFinal();
    }
}
