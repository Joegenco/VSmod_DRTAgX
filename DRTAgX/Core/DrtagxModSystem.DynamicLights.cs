using System;
using OpenTK.Graphics.OpenGL4;
using Vintagestory.API.Client;
using Vintagestory.API.MathTools;
using Vintagestory.Client.NoObf;

namespace DRTAgX;

// Native lights are published in Before, while the opaque camera is updated
// afterward. Convert between those two exact engine matrices each frame.
public partial class DrtagxModSystem
{
    private readonly float[][] _lightPositions = MakeLightVectors();
    private readonly float[][] _lightColors = MakeLightVectors();
    private readonly MovingLightSources _movingSources = new();
    private readonly MovingLightUniforms _movingUniforms = new();
    private readonly MovingLightShadowRenderer _terrainShadowMaps = new();
    private readonly StaticTerrainShadowMaps _staticTerrainMaps;
    private readonly StaticTerrainShadowMaps _performanceTerrainMaps;
    private StaticTerrainShadowMaps _publishedPlacedMaps;
    private readonly StaticLightTileBindings _staticTileBindings = new();
    private StaticLightSources? _staticLightSources;
    private PlacedLightGeometryBridge? _placedGeometryBridge;
    private ShadowTextureReleaseRenderer? _shadowTextureRelease;
    private PlacedLightTerrainBindings? _terrainPlacedBindings;
    private bool _lightNonzeroLogged, _lightUnavailableLogged, _staticUnavailableLogged;
    private bool _staticWasEnabled;
    private int _shadowDebugView;
    private bool _debugHotkeysRegistered;
    private readonly double[] _publishedModelView = new double[16];
    private readonly double[] _inversePublished = new double[16];
    private readonly double[] _lightReprojection = new double[16];
    private bool _hasPublishedModelView;
    private NativeLightMatrixCaptureRenderer? _lightMatrixCapture;
    private readonly Vec3d _placedCameraWorld = new();
    private readonly double[] _placedInverseView = new double[16];
    private IShaderProgram? _staticDebugShader;
    private StaticDebugLocations? _staticDebugLocations;

    // Uniform locations belong to a link generation, not just a reusable GL
    // program name. Reload/disposal clears this owner-held, render-thread cache.
    private sealed class StaticDebugLocations(int program)
    {
        internal readonly int Program = program;
        internal readonly int View = GL.GetUniformLocation(program, "drtDebugView");
        internal readonly int Contact = GL.GetUniformLocation(program, "drtContactShadowsEnabled");
        internal readonly int Grid = GL.GetUniformLocation(program, "drtShadowGridEnabled");
        internal readonly int State = GL.GetUniformLocation(program, "drtStaticDebugState");
        internal readonly int Counts = GL.GetUniformLocation(program, "drtStaticDebugCounts");
    }

    private void ResetStaticDebugUniforms()
    {
        _staticDebugShader = null;
        _staticDebugLocations = null;
    }

    public DrtagxModSystem()
    {
        _staticTerrainMaps = new StaticTerrainShadowMaps(_terrainShadowMaps);
        _performanceTerrainMaps = new StaticTerrainShadowMaps(_terrainShadowMaps) { TargetFaceSize = 48 };
        _publishedPlacedMaps = _staticTerrainMaps;
    }

    private void StartDynamicOcclusion(ICoreClientAPI api)
    {
        _staticLightSources = new StaticLightSources(api);
        _terrainPlacedBindings = new PlacedLightTerrainBindings(_staticTerrainMaps, _staticTileBindings) { Api = api };
        _placedGeometryBridge = new PlacedLightGeometryBridge(api, _staticLightSources);
        api.Event.ReloadShader += ReloadStaticLightShaders;
        // PlayerEffects publishes lights at Before 0.1. Capture its matrix
        // before the engine rebuilds the opaque camera later in this frame.
        _lightMatrixCapture = new NativeLightMatrixCaptureRenderer(CapturePublishedLightMatrix);
        api.Event.RegisterRenderer(_lightMatrixCapture, EnumRenderStage.Before, "drtagx_light_matrix_capture");
        // Native terrain draws at Opaque 0.37, before Sheyder's deferred relight.
        api.Event.RegisterRenderer(this, EnumRenderStage.Opaque, "drtagx_terrain_point_lights");
        _shadowTextureRelease = new ShadowTextureReleaseRenderer(_terrainShadowMaps, _staticTileBindings);
        api.Event.RegisterRenderer(_shadowTextureRelease, EnumRenderStage.Opaque, "drtagx_shadow_texture_release");
    }

    private bool ReloadStaticLightShaders()
    {
        ResetStaticDebugUniforms();
        PlacedLightGpuProfile.Reload();
        MovingLightGpuProfile.Reload();
        _terrainShadowMaps.Reload();
        _movingUniforms.Reload();
        // Native program IDs/locations can be reused after shader reload.
        _surfaceLights.Reset();
        _terrainPlacedBindings?.Reload();
        Array.Clear(_entityPlacedPrograms);
        if (_clientApi != null) FogAndLightAssetBridge.Apply(_clientApi);
        _staticTileBindings.Reload();
        if (PlacedLightQualityShaders.QualityReload)
        {
            _staticTerrainMaps.RetainQualityDepth();
            _performanceTerrainMaps.RetainQualityDepth();
        }
        else
        {
            _staticTerrainMaps.ResetShaderLocations();
            _performanceTerrainMaps.ResetShaderLocations();
        }
        return true;
    }

    private void CapturePublishedLightMatrix()
    {
        if (_clientApi?.World is not ClientMain world) return;
        double[] matrix = world.CurrentModelViewMatrixd;
        if (matrix.Length < 16) return;
        Array.Copy(matrix, _publishedModelView, 16);
        _hasPublishedModelView = true;
        try { _movingSources.Capture(_clientApi, world); }
        catch (Exception ex) { if (!_lightUnavailableLogged) { _clientApi.Logger.Warning("[DRT AgX] Moving source snapshot unavailable: {0}", ex.Message); _lightUnavailableLogged = true; } }
    }

    private void RenderDynamicOcclusion(float deltaTime)
    {
        // Retire any native Use without Stop before rewriting source buffers
        // or capturing the GL state for this frame's shadow/deferred passes.
        _terrainPlacedBindings?.Restore();
        bool placedLights = FrameQuality.Current.PlacedLights(Config.StaticLightShadows);
        if (_terrainPlacedBindings != null)
        {
            // Wind directionality uses published sources in both PLS modes;
            // the binding's mode uniform controls the additional depth receiving.
            _terrainPlacedBindings.Enabled = placedLights;
            _terrainPlacedBindings.GridEnabled = Config.SunShadowGridEnabled;
        }
        ICoreClientAPI? api = _clientApi;
        if (api?.World?.Player?.Entity == null) return;
        if (api.World is not ClientMain placedWorld) return;
        double[] placedView = placedWorld.CurrentModelViewMatrixd;
        float[] placedProjection = api.Render.CurrentProjectionMatrix;
        PlacedLightView.CameraFromView(placedView, _placedInverseView, _placedCameraWorld);
        // Collect placed emitters from loaded chunks without touching the
        // finished dynamic-light uniforms or terrain shadow path.
        bool staticSourcesAvailable = true;
        try { _staticLightSources?.Update(_placedCameraWorld, placedView, placedProjection); }
        catch (Exception ex)
        {
            staticSourcesAvailable = false;
            if (!_staticUnavailableLogged)
            {
                api.Logger.Warning("[DRT AgX] Static-light source scan unavailable: " + ex.Message);
                _staticUnavailableLogged = true;
            }
        }

        IShaderProgram? native = api.Render.GetEngineShader(EnumShaderProgram.Chunkopaque);
        IShaderProgram? deferred = api.Shader.GetProgramByName("deferredlighting");
        if (deferred == null || deferred.Disposed || deferred.LoadError)
        {
            UploadEntityPlacedNormals(api, _placedCameraWorld, false);
            return;
        }
        _surfaceLights.SetSunGridCamera(_placedCameraWorld);
        _surfaceLights.PublishSky(native, deferred);
        if (native == null || native.Disposed || native.LoadError)
        {
            UploadEntityPlacedNormals(api, _placedCameraWorld, false);
            _staticTileBindings.Disable(deferred);
            SetStaticDebugUniforms(deferred, 8);
            return;
        }
        int quantity;
        if (_movingSources.Available)
            quantity = _movingSources.Publish(api, placedView, _lightPositions, _lightColors);
        else
        {
            // Compatibility fallback retains published native lights without
            // ever inferring hand identity from distance to the player.
            quantity = _movingUniforms.ReadNative(native, _lightPositions, _lightColors);
            if (_hasPublishedModelView)
            {
                Mat4d.Invert(_inversePublished, _publishedModelView);
                Mat4d.Multiply(_lightReprojection, placedView, _inversePublished);
                for (int i=0; i<quantity; ++i) ReprojectPublishedLight(_lightPositions[i], _lightReprojection);
            }
            for (int i=0; i<quantity; ++i) _movingSources.Kinds[i] = MovingLightKind.Entity;
        }
        _movingUniforms.Publish(deferred, quantity, _lightPositions, _lightColors, placedProjection);
        if (Config.DynamicLightShadows)
            _terrainShadowMaps.Render(api, _lightPositions, _lightColors, quantity,
                FrameQuality.Current.MovingLights(Config.DynamicShadowLightCount), deferred, _movingSources.Kinds, _movingSources.FirstPerson);
        else {
            MovingLightGpuProfile.Total.BeginShadows(MovingLightGpuProfile.Enabled);
            _terrainShadowMaps.Disable(deferred);
            MovingLightGpuProfile.Total.EndShadows();
        }
        MovingLightGpuProfile.Frame(api, _movingSources.Kinds, quantity, _terrainShadowMaps,
            Config.DynamicLightShadows, Config.DynamicShadowLightCount);

        try
        {
            var requested = FrameQuality.Current.Performance ? _performanceTerrainMaps : _staticTerrainMaps;
            var inactive = ReferenceEquals(requested, _staticTerrainMaps) ? _performanceTerrainMaps : _staticTerrainMaps;
            // Inactive depth still observes geometry edits, so returning to a warm cache is safe.
            if (_staticLightSources != null && !ReferenceEquals(inactive, _publishedPlacedMaps))
                inactive.Pause(_staticLightSources, false);
            requested.ViewCullingEnabled = Config.PlacedLightViewCulling;
            requested.AllTerrainPassesEnabled = Config.PlacedLightAllTerrainPasses;
            bool preparedReplacement = false;
            if (!ReferenceEquals(requested, _publishedPlacedMaps) && placedLights &&
                staticSourcesAvailable && _staticLightSources != null)
            {
                requested.UpdateAndRender(api, _staticLightSources, _placedCameraWorld, placedView, placedProjection, deltaTime);
                preparedReplacement = true;
                if (CompletePlacedReplacement(requested, _publishedPlacedMaps))
                {
                    _publishedPlacedMaps = requested;
                    _terrainPlacedBindings?.PublishMaps(requested);
                    _staticTileBindings.Reload(); // Publication revisions belong to their cache owner.
                }
            }
            _publishedPlacedMaps.ViewCullingEnabled = Config.PlacedLightViewCulling;
            _publishedPlacedMaps.AllTerrainPassesEnabled = Config.PlacedLightAllTerrainPasses;
            _staticTileBindings.AllTerrainPassesEnabled = Config.PlacedLightAllTerrainPasses;
            _staticTileBindings.ViewCullingEnabled = Config.PlacedLightViewCulling;
            // Preserve completed depth across toggles, but keep observing edits
            // while disabled so reenabling cannot expose obsolete geometry.
            if (!placedLights && _staticLightSources != null)
                _publishedPlacedMaps.Pause(_staticLightSources, _staticWasEnabled);
            _staticWasEnabled = placedLights;
            if (placedLights && staticSourcesAvailable && _staticLightSources != null)
            {
                if (!preparedReplacement || !ReferenceEquals(requested, _publishedPlacedMaps))
                    _publishedPlacedMaps.UpdateAndRender(api, _staticLightSources, _placedCameraWorld,
                        placedView, placedProjection, deltaTime);
                if (_publishedPlacedMaps.UpdatedThisFrame)
                    _staticLightSources.SetResidentSources(_publishedPlacedMaps.ReadyLights);
            }
            // Offscreen maps remain resident for reuse and entity normals.
            // The player's view-culling preference controls which enter relighting.
            _staticTileBindings.LightRevision = _publishedPlacedMaps.PublicationRevision;
            _staticTileBindings.PlsSaturation = Config.PlsSaturation;
            _staticTileBindings.Prepare(api, deferred, _publishedPlacedMaps.Active,
                _publishedPlacedMaps.TextureId,
                placedLights && staticSourcesAvailable && _publishedPlacedMaps.Ready,
                deltaTime);
            PlacedLightGpuProfile.CacheFrame(_staticLightSources, _publishedPlacedMaps, _staticTileBindings,
                placedLights && staticSourcesAvailable);
            PlacedLightGpuProfile.Frame(api, _staticTileBindings.PublishedCount,
                _staticTileBindings.RectangleCandidatesPerTile, _staticTileBindings.DepthCulling,
                placedLights && staticSourcesAvailable
                    ? _publishedPlacedMaps.FacesBakedThisFrame : 0, _publishedPlacedMaps.LastBakeGpuMilliseconds);
            // The status is independent of the light contribution, so a
            // silent vanilla fallback can be diagnosed on screen.
            int staticState = !placedLights ? 0 :
                !staticSourcesAvailable || _staticLightSources == null ? 7 :
                _publishedPlacedMaps.Ready && _publishedPlacedMaps.Active.Count == 0 ? 6 :
                _publishedPlacedMaps.Ready && _staticTileBindings.IsBound ?
                    _publishedPlacedMaps.CandidateCount > _publishedPlacedMaps.Capacity ? 9 : 6 :
                !_staticLightSources.IsScanComplete ? 1 :
                _staticLightSources.Sources.Count == 0 ? 2 :
                !_publishedPlacedMaps.Ready ? 4 :
                !_staticTileBindings.IsBound ? 5 :
                6;
            SetStaticDebugUniforms(deferred, staticState);
        }
        catch (Exception ex)
        {
            _staticTileBindings.Disable(deferred); // Keep vanilla block light on any cache failure.
            SetStaticDebugUniforms(deferred, 7);
            if (!_staticUnavailableLogged)
            {
                api.Logger.Warning("[DRT AgX] Static-light cache unavailable: " + ex.Message);
                _staticUnavailableLogged = true;
            }
        }

        // Forward models/items/particles reuse nearby ready placed radiance,
        // without sampling the terrain-only atlas. Clear counts on cache failure.
        UploadEntityPlacedNormals(api, _placedCameraWorld,
            placedLights && staticSourcesAvailable && _staticTileBindings.IsBound);

        if (quantity > 0 && !_lightNonzeroLogged)
        {
            api.Logger.Notification($"[DRT AgX] Published {quantity} independent moving-light sources.");
            _lightNonzeroLogged = true;
        }
    }

    private void SetStaticDebugUniforms(IShaderProgram deferred, int state)
    {
        int previous = GL.GetInteger(GetPName.CurrentProgram);
        try
        {
            GL.UseProgram(deferred.ProgramId);
            if (!ReferenceEquals(_staticDebugShader, deferred) || _staticDebugLocations?.Program != deferred.ProgramId)
            {
                _staticDebugLocations = new StaticDebugLocations(deferred.ProgramId);
                _staticDebugShader = deferred;
            }
            var locations = _staticDebugLocations!;
            if (locations.View >= 0) GL.Uniform1(locations.View, ClientSettings.DeveloperMode ? _shadowDebugView : 0);
            // Opaque order 0.38 publishes menu settings even outside developer mode.
            if (locations.Contact >= 0) GL.Uniform1(locations.Contact, FrameQuality.Current.ContactShadows(Config.ContactShadows) ? 1 : 0);
            // Moving/placed comparisons and atmospheric sun share this style preference.
            if (locations.Grid >= 0) GL.Uniform1(locations.Grid, Config.SunShadowGridEnabled ? 1 : 0);
            if (locations.State >= 0) GL.Uniform1(locations.State, state);
            if (locations.Counts >= 0)
                GL.Uniform4(locations.Counts, _staticLightSources?.ScanProgress ?? 0f,
                    _staticLightSources?.Sources.Count ?? 0, _staticTerrainMaps.Active.Count,
                    _staticTerrainMaps.ValidCount);
        }
        finally { GL.UseProgram(previous); }
    }

    private static void ReprojectPublishedLight(float[] p, double[] transform)
    {
        double x = p[0], y = p[1], z = p[2];
        p[0] = (float)(transform[0] * x + transform[4] * y + transform[8] * z + transform[12]);
        p[1] = (float)(transform[1] * x + transform[5] * y + transform[9] * z + transform[13]);
        p[2] = (float)(transform[2] * x + transform[6] * y + transform[10] * z + transform[14]);
    }

    private void RegisterShadowDebugHotkeys(ICoreClientAPI api)
    {
        if (!ClientSettings.DeveloperMode) return;
        api.Input.RegisterHotKey("drtagxmovingprofile", "DRT AgX moving-light GPU timing", GlKeys.Number9,
            HotkeyType.GUIOrOtherControls, true, false, false);
        api.Input.SetHotKeyHandler("drtagxmovingprofile", _ =>
        {
            if (!ClientSettings.DeveloperMode) return false;
            MovingLightGpuProfile.Toggle(api); return true;
        });
        api.Input.RegisterHotKey("drtagxstaticprofile", "DRT AgX placed-light GPU timing", GlKeys.Number8,
            HotkeyType.GUIOrOtherControls, true, false, false);
        api.Input.SetHotKeyHandler("drtagxstaticprofile", _ =>
        {
            if (!ClientSettings.DeveloperMode) return false;
            PlacedLightGpuProfile.Toggle(api); return true;
        });
        for (int view = 1; view <= 7; view++)
        {
            string code = $"drtagxshadowdebug{view}";
            // The installed 1.22.7 API uses Number1..Number7 for the top row.
            GlKeys key = view switch
            {
                1 => GlKeys.Number1,
                2 => GlKeys.Number2,
                3 => GlKeys.Number3,
                4 => GlKeys.Number4,
                5 => GlKeys.Number5,
                6 => GlKeys.Number6,
                _ => GlKeys.Number7
            };
            api.Input.RegisterHotKey(code, $"DRT AgX shadow debug {view}", key,
                HotkeyType.GUIOrOtherControls, true, false, false);
            int selectedView = view;
            api.Input.SetHotKeyHandler(code, _ =>
            {
                if (!ClientSettings.DeveloperMode) return false;
                _shadowDebugView = _shadowDebugView == selectedView ? 0 : selectedView;
                return true;
            });
        }
    }

    private void UpdateDeveloperTools(ICoreClientAPI api)
    {
        bool enabled = ClientSettings.DeveloperMode;
        if (enabled == _debugHotkeysRegistered) return;
        if (enabled) RegisterShadowDebugHotkeys(api);
        else
        {
            RemoveDebugHotkeys(api);
            _shadowDebugView = 0;
            MovingLightGpuProfile.Disable();
            PlacedLightGpuProfile.Disable();
        }
        _debugHotkeysRegistered = enabled;
    }

    private static void RemoveDebugHotkeys(ICoreClientAPI api)
    {
        // Remove only owned diagnostic bindings; the player settings key remains available.
        api.Input.HotKeys.Remove("drtagxmovingprofile");
        api.Input.HotKeys.Remove("drtagxstaticprofile");
        for (int view = 1; view <= 7; view++) api.Input.HotKeys.Remove($"drtagxshadowdebug{view}");
    }

    private void DisposeDynamicOcclusion()
    {
        ResetStaticDebugUniforms();
        if (_clientApi != null && _debugHotkeysRegistered) RemoveDebugHotkeys(_clientApi);
        _debugHotkeysRegistered = false;
        PlacedLightGpuProfile.Dispose();
        MovingLightGpuProfile.Dispose();
        _movingSources.Reset();
        _surfaceLights.Reset();
        Array.Clear(_entityPlacedPrograms);
        if (_clientApi != null) _clientApi.Event.ReloadShader -= ReloadStaticLightShaders;
        _placedGeometryBridge?.Dispose(); _placedGeometryBridge = null;
        _staticLightSources?.Dispose();
        _staticLightSources = null;
        if (_clientApi != null && _lightMatrixCapture != null)
            _clientApi.Event.UnregisterRenderer(_lightMatrixCapture, EnumRenderStage.Before);
        _lightMatrixCapture = null;
        if (_clientApi != null && _shadowTextureRelease != null)
            _clientApi.Event.UnregisterRenderer(_shadowTextureRelease, EnumRenderStage.Opaque);
        _shadowTextureRelease = null;
        _terrainPlacedBindings?.Dispose(); _terrainPlacedBindings = null;
        _terrainShadowMaps.Dispose();
        _staticTileBindings.Dispose();
        _staticTerrainMaps.Dispose();
        _performanceTerrainMaps.Dispose();
    }

    private static bool CompletePlacedReplacement(StaticTerrainShadowMaps next, StaticTerrainShadowMaps old)
    {
        if (!next.Ready) return false;
        // Publish only after every currently contributing emitter has a complete six-face replacement.
        foreach (var light in old.Active)
        {
            bool found = false;
            foreach (var ready in next.Active)
                if (ready.Source.X == light.Source.X && ready.Source.Y == light.Source.Y && ready.Source.Z == light.Source.Z)
                { found = true; break; }
            if (!found) return false;
        }
        return true;
    }

    private static float[][] MakeLightVectors()
    {
        float[][] vectors = new float[16][];
        for (int i = 0; i < vectors.Length; i++) vectors[i] = new float[3];
        return vectors;
    }
}
