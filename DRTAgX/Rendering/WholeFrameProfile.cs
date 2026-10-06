using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using HarmonyLib;
using Newtonsoft.Json;
using Vintagestory.API.Client;

namespace DRTAgX;

/// <summary>Observe-only Release profiling, explicitly enabled through DRTAGX_PROFILE_DIR.</summary>
internal sealed class WholeFrameProfile : IDisposable
{
    private const string PatchId = "drtagx.frame.profile";
    private static WholeFrameProfile? _current;
    private readonly Harmony _harmony = new(PatchId);
    private readonly ICoreClientAPI _api;
    private readonly string _directory;
    private readonly Dictionary<MethodBase, int> _methods = new();
    private readonly List<string> _names = new();
    private readonly List<FrameProfileSamples> _samples = new();
    private readonly int[] _stages = new int[64];
    private readonly FrameProfileSamples _scene = new(), _pacing = new();
    private long _started, _lastFrame, _warmLastFrame, _collectionStarted;
    private int _sceneSlot = -1;
    private bool _collecting, _finished, _inventoried;
    private int _frameNumber;
    private readonly int _passStride = Environment.GetEnvironmentVariable("DRTAGX_PROFILE_DETAIL") == "full" ? 1 : 8;
    private readonly bool _sceneOnly = Environment.GetEnvironmentVariable("DRTAGX_PROFILE_DETAIL") == "scene";
    private object? _startContext;
    private int _longFrameGaps;

    internal readonly struct Token
    {
        internal readonly int Scope, Slot;
        internal readonly long Ticks;
        internal Token(int scope, int slot, long ticks = 0) { Scope = scope; Slot = slot; Ticks = ticks; }
    }

    internal static WholeFrameProfile? Create(ICoreClientAPI api)
    {
        string? directory = Environment.GetEnvironmentVariable("DRTAGX_PROFILE_DIR");
        if (string.IsNullOrWhiteSpace(directory)) return null;
        // Diagnostics obey the same write boundary as the mod source and game data.
        string path = Path.GetFullPath(directory);
        string workspace = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../.."));
        string data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VintagestoryData");
        string? allowed = Environment.GetEnvironmentVariable("DRTAGX_WORKSPACE");
        if (allowed != null) workspace = Path.GetFullPath(allowed);
        bool Inside(string root) => path.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        if (!Inside(workspace) && !Inside(data)) { api.Logger.Warning("[DRTAgX Profile] Output directory outside authorized roots; disabled."); return null; }
        return new WholeFrameProfile(api, path);
    }

    private WholeFrameProfile(ICoreClientAPI api, string directory)
    {
        _api = api; _directory = directory; _current = this;
        Directory.CreateDirectory(directory);
        Hook("Vintagestory.Client.NoObf.ClientMain", "MainRenderLoop", "native.scene-submission");
        Hook("Vintagestory.Client.NoObf.ClientPlatformWindows", "BlitPrimaryToDefault", "presentation.copy");
        if (_sceneOnly)
        {
            // Four scene timestamp commands per frame in the ordinary detailed probe become two.
            // No per-effect method/stage patches are installed in this overhead-control mode.
            api.Logger.Notification("[DRTAgX Profile] Scene-only overhead control: 30-second warmup, 60-second capture.");
            return;
        }
        Hook("Vintagestory.Client.NoObf.ClientPlatformWindows", "RenderPostprocessingEffects", "native.postprocessing (includes GTAO)");
        Hook("Vintagestory.Client.NoObf.ClientPlatformWindows", "RenderFinalComposition", "final (includes shafts and owned HDR)");
        string sheyder = "SheyderMod.Features.";
        Hook(sheyder + "Deferred.DeferredRenderer", "OnPrepare", "deferred.prepare");
        Hook(sheyder + "Deferred.DeferredRenderer", "OnRelight", "deferred.whole-relight");
        Hook(sheyder + "GTAO.GtaoRenderer", "RenderGtaoPass", "GTAO.total");
        Hook(sheyder + "GTAO.GtaoRenderer", "BlurPass", "GTAO.denoise (each direction)");
        Hook(sheyder + "VolumetricFog.VolumetricFogRenderer", "RenderScatterAndBindFinal", "volumetric.scatter");
        Hook(sheyder + "WaterShader.WaterSsrRenderer", "OnRenderFrame", "water.total");
        Hook(sheyder + "WaterShader.WaterSsrRenderer", "RenderGenPass", "water.generate");
        Hook(sheyder + "WaterShader.WaterSsrRenderer", "RenderTaaPass", "water.TAA");
        Hook(sheyder + "WaterShader.WaterSsrRenderer", "BlurPass", "water.blur (each direction)");
        Hook(sheyder + "WaterShader.WaterSsrRenderer", "Composite", "water.composite");
        Hook(sheyder + "WaterShader.EnvCubemap.WaterEnvCubemap", "Capture", "water.cubemap (all submitted faces)");
        Hook("DRTAgX.HdrPostProcessor", "Render", "HDR.total");
        Hook("DRTAgX.HdrPostProcessor", "RenderPyramid", "HDR.downsample");
        Hook("DRTAgX.HdrPostProcessor", "BlurPyramid", "HDR.blur");
        Hook("DRTAgX.HdrPostProcessor", "CombineBloom", "HDR.combine");
        Hook("DRTAgX.HdrPostProcessor", "AdaptExposure", "HDR.adaptation");
        Hook("DRTAgX.MovingLightShadowRenderer", "Render", "moving.shadows");
        Hook("DRTAgX.StaticTerrainShadowMaps", "Prepare", "placed.cache-prepare");
        Hook("DRTAgX.StaticTerrainShadowMaps", "RenderSource", "placed.bake");
        Hook("DRTAgX.StaticLightTileBindings", "Prepare", "placed.tiles");
        Hook("DRTAgX.AtmosphereRenderer", "OnRenderFrame", "atmosphere.total");
        Hook("DRTAgX.AtmosphereSkyResources", "Update", "atmosphere.LUT-update-check");
        Hook("DRTAgX.AtmosphereAmbientResources", "Render", "atmosphere.ambient");
        string[] native = { "SystemRenderTerrain", "SystemRenderEntities", "SystemRenderDecals", "SystemRenderSkyColor", "SystemRenderNightSky", "SystemRenderSunMoon" };
        foreach (string name in native) Hook("Vintagestory.Client.NoObf." + name, "OnRenderFrame3D", "native." + name);
        // Loaded type metadata identifies optional cloud providers without inspecting engine binaries.
        foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type[] types;
            try { types = assembly.GetTypes(); } catch (ReflectionTypeLoadException) { continue; }
            foreach (Type type in types)
                if (type.Name.Contains("CloudRenderer", StringComparison.Ordinal) && typeof(IRenderer).IsAssignableFrom(type))
                    Hook(type.FullName!, "OnRenderFrame", "clouds." + type.FullName);
        }
        Array.Fill(_stages, -1);
        MethodInfo? stages = AccessTools.Method("Vintagestory.Client.NoObf.ClientEventManager:TriggerRenderStage");
        if (stages != null)
        {
            foreach (EnumRenderStage stage in Enum.GetValues<EnumRenderStage>()) _stages[(int)stage] = Add("stage." + stage);
            _harmony.Patch(stages, prefix: new HarmonyMethod(typeof(WholeFrameProfile), nameof(StageBegin)), finalizer: new HarmonyMethod(typeof(WholeFrameProfile), nameof(End)));
        }
        api.Logger.Notification("[DRTAgX Profile] Enabled: 30-second warmup, 60-second capture; inclusive scopes must not be added.");
    }

    private int Add(string name) { _names.Add(name); _samples.Add(new FrameProfileSamples()); return _samples.Count - 1; }

    private void Hook(string type, string method, string name)
    {
        MethodInfo? target = AccessTools.Method(type + ":" + method);
        if (target == null) { _api.Logger.Notification("[DRTAgX Profile] Unavailable scope: {0}", name); return; }
        HookTarget(target, name);
    }

    private void HookTarget(MethodInfo target, string name)
    {
        // Reflection may return an inherited member reflected through a derived type. Harmony needs its declaration.
        if (target.ReflectedType != target.DeclaringType && target.DeclaringType != null)
            foreach (var declared in target.DeclaringType.GetMethods(BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
                if (declared.MetadataToken == target.MetadataToken) { target = declared; break; }
        if (_methods.ContainsKey(target)) return;
        if (target.IsAbstract || target.GetMethodBody() == null) return;
        try
        {
            _harmony.Patch(target, prefix: new HarmonyMethod(typeof(WholeFrameProfile), nameof(Begin)) { priority = Priority.First },
                finalizer: new HarmonyMethod(typeof(WholeFrameProfile), nameof(End)) { priority = Priority.Last });
            _methods[target] = Add(name);
        }
        catch (Exception ex) { _api.Logger.Notification("[DRTAgX Profile] Scope unavailable {0}: {1}", name, ex.Message); }
    }

    private void ObserveCallbacks()
    {
        var rows = new List<object>();
        object world = _api.World;
        object? events = null;
        foreach (FieldInfo field in world.GetType().GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public))
            if (field.FieldType.Name == "ClientEventManager") { events = field.GetValue(world); break; }
        if (events != null && AccessTools.Field(events.GetType(), "renderersByStage")?.GetValue(events) is Array stages)
            for (int i = 0; i < stages.Length; ++i)
            {
                if (stages.GetValue(i) is not IEnumerable handlers) continue;
                foreach (object handler in handlers)
                {
                    object? renderer = AccessTools.Field(handler.GetType(), "Renderer")?.GetValue(handler);
                    if (renderer == null) continue;
                    MethodInfo? method = AccessTools.Method(renderer.GetType(), "OnRenderFrame");
                    if (AccessTools.Field(renderer.GetType(), "action")?.GetValue(renderer) is Delegate action) method = action.Method;
                    string? name = AccessTools.Field(handler.GetType(), "ProfilingName")?.GetValue(handler) as string;
                    rows.Add(new { Stage = i, Name = name, Type = renderer.GetType().FullName, Method = method?.ToString(), Owner = method?.DeclaringType?.FullName });
                    // Resolve actual registered delegates rather than infer method names from shader assets.
                    if (method != null && method.DeclaringType != typeof(WholeFrameProfile))
                        HookTarget(method, "callback." + method.DeclaringType?.Name + "." + method.Name);
                }
            }
        File.WriteAllText(Path.Combine(_directory, "callbacks.json"), JsonConvert.SerializeObject(rows, Formatting.Indented));
    }

    // Prefix/finalizer pairs observe calls without changing return values or exceptions.
    private static void Begin(MethodBase __originalMethod, out Token __state)
    {
        var owner = _current;
        __state = new Token(-1, -1);
        if (owner == null || owner._finished) return;
        string method = __originalMethod.Name;
        if (method == "MainRenderLoop") owner.Frame();
        if (owner._sceneOnly) return;
        if (!owner._collecting) return;
        int scope = owner._methods[__originalMethod];
        __state = new Token(scope, owner._frameNumber % owner._passStride == 0 ? owner._samples[scope].Begin(owner._frameNumber) : -1, Stopwatch.GetTimestamp());
    }

    private static void StageBegin(EnumRenderStage stage, out Token __state)
    {
        var owner = _current;
        __state = new Token(-1, -1);
        if (owner?. _collecting != true) return;
        // Stage names are initialized once; enum values are matched without boxing or string allocation.
        int scope = (uint)stage < (uint)owner._stages.Length ? owner._stages[(int)stage] : -1;
        if (scope >= 0) __state = new Token(scope, owner._frameNumber % owner._passStride == 0 ? owner._samples[scope].Begin(owner._frameNumber) : -1, Stopwatch.GetTimestamp());
    }

    private static void End(MethodBase __originalMethod, Token __state)
    {
        var owner = _current;
        if (owner == null) return;
        if (__state.Scope >= 0)
        {
            if (__state.Slot >= 0) owner._samples[__state.Scope].End(__state.Slot);
            else owner._samples[__state.Scope].AddCpu((Stopwatch.GetTimestamp() - __state.Ticks) * 1000.0 / Stopwatch.Frequency, owner._frameNumber);
        }
        if (__originalMethod.Name == "BlitPrimaryToDefault" && owner._sceneSlot >= 0)
        {
            owner._scene.End(owner._sceneSlot); owner._sceneSlot = -1;
        }
    }

    private void Frame()
    {
        long now = Stopwatch.GetTimestamp();
        if (_started == 0) _started = now;
        // A cold shader reload can block the first render loop. Warm up for
        // thirty continuous seconds after that stall, then collect a full
        // sixty seconds rather than using an absolute startup deadline.
        if (!_collecting && _warmLastFrame != 0 && now - _warmLastFrame > Stopwatch.Frequency)
            _started = now;
        _warmLastFrame = now;
        ++_frameNumber;
        double elapsed = (now - _started) / (double)Stopwatch.Frequency;
        if (!_inventoried && elapsed >= 15)
        {
            try { FrameResourceInventory.Write(_api, _directory); if (!_sceneOnly) ObserveCallbacks(); }
            catch (Exception ex) { _api.Logger.Warning("[DRTAgX Profile] Inventory unavailable; rendering retained: " + ex.Message); }
            _inventoried = true;
        }
        if (!_collecting && elapsed >= 30)
        {
            foreach (var samples in _samples) samples.Clear();
            _scene.Clear(); _pacing.Clear(); _collecting = true;
            _collectionStarted = now;
            _startContext = CaptureContext();
        }
        if (_collecting && now - _collectionStarted >= 60L * Stopwatch.Frequency)
        {
            _collecting = false; _finished = true;
            _scene.Poll();
            var scopes = new Dictionary<string, object>();
            for (int i = 0; i < _names.Count; ++i) { _samples[i].Poll(); scopes[_names[i]] = _samples[i].Summary(); }
            // Report allocation and serialization occur once after the measured interval.
            File.WriteAllText(Path.Combine(_directory, "frame-profile.json"), JsonConvert.SerializeObject(new {
                Resolution = new { _api.Render.FrameWidth, _api.Render.FrameHeight }, Scene = _scene.Summary(),
                RequestedMode = Environment.GetEnvironmentVariable("DRTAGX_PROFILE_MODE"),
                ProfileDetail = _sceneOnly ? "scene" : _passStride == 1 ? "full" : "sparse",
                StartContext = _startContext, EndContext = CaptureContext(), LongFrameGaps = _longFrameGaps,
                Camera = _api.World.Player.Entity.CameraPos.ToString(), Utc = DateTime.UtcNow,
                CollectionSeconds = (now - _collectionStarted) / (double)Stopwatch.Frequency,
                FramePacing = _pacing.Summary(), PassSamplingStride = _passStride, Scopes = scopes,
                Notes = "Inclusive method/stage intervals overlap. Scene timestamps may include CPU starvation. Pacing includes present/simulation; presentation.copy is not swap duration. Startup and report writes excluded."
            }, Formatting.Indented));
            _api.Logger.Notification("[DRTAgX Profile] Capture complete: {0}", _directory);
            return;
        }
        if (!_collecting) return;
        if (_lastFrame != 0)
        {
            double gap = (now - _lastFrame) * 1000.0 / Stopwatch.Frequency;
            _pacing.AddCpu(gap, _frameNumber);
            if (gap > 250) ++_longFrameGaps; // Suspension/focus/pacing evidence; never silently trim tails.
        }
        _lastFrame = now;
        _sceneSlot = _scene.Begin(_frameNumber);
    }

    private object CaptureContext()
    {
        // Allocate only at the interval boundaries. These are observed values, not saved-setting edits.
        var calendar = _api.World.Calendar;
        var ambient = _api.Ambient;
        var sun = calendar.SunPositionNormalized;
        var qualityType = AccessTools.TypeByName("DRTAgX.FrameQuality");
        var quality = qualityType?.GetProperty("Current", BindingFlags.Static | BindingFlags.NonPublic)?.GetValue(null);
        bool? deferred = null;
        object? sheyder = _api.ModLoader.GetModSystem("SheyderMod.SheyderModSystem");
        if (sheyder == null) deferred = false;
        else if (AccessTools.Field(sheyder.GetType(), "_renderers")?.GetValue(sheyder) is IEnumerable renderers)
            foreach (object renderer in renderers)
                if (renderer.GetType().FullName == "SheyderMod.Features.Deferred.DeferredRenderer")
                    deferred = AccessTools.Field(renderer.GetType(), "_mode")?.GetValue(renderer) is int mode ? mode == 1 : null;
        return new {
            Camera = _api.World.Player.Entity.CameraPos.ToString(),
            View = (float[])_api.Render.CameraMatrixOriginf.Clone(),
            _api.Render.FrameWidth, _api.Render.FrameHeight,
            SunDirection = new { sun.X, sun.Y, sun.Z }, calendar.SunLightStrength, calendar.MoonLightStrength,
            ambient.BlendedCloudDensity, ambient.BlendedFogDensity, ambient.BlendedFogMin,
            ViewDistance = _api.Settings.Int.Get("viewDistance", 0),
            ShadowMapQuality = _api.Settings.Int.Get("shadowMapQuality", 0),
            SsaoQuality = _api.Settings.Int.Get("ssaoQuality", 0),
            // Native SSAO also creates four Primary attachments; MRT presence alone cannot prove relighting.
            Deferred = deferred,
            PrimaryMrtPresent = _api.Render.FrameBuffers[0]?.ColorTextureIds?.Length >= 4,
            Quality = quality?.GetType().GetProperty("Performance")?.GetValue(quality),
            QualityGeneration = qualityType?.GetProperty("Generation", BindingFlags.Static | BindingFlags.NonPublic)?.GetValue(null)
        };
    }

    public void Dispose()
    {
        _harmony.UnpatchAll(PatchId);
        foreach (var sample in _samples) sample.Dispose();
        _scene.Dispose(); _pacing.Dispose();
        if (ReferenceEquals(_current, this)) _current = null;
    }
}
