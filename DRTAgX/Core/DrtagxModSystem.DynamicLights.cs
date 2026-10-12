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
    private readonly PlacedLightSurfaceCache _placedSurfaceCache = new();
    private readonly PlacedLightReceiverDemand _placedReceiverDemand = new();
    private PlacedLightSurfaceBindings? _placedSurfaceBindings;
    private StaticLightSources? _staticLightSources;
    private PlacedLightGeometryBridge? _placedGeometryBridge;
    private ShadowTextureReleaseRenderer? _shadowTextureRelease;
    private PlacedLightTerrainBindings? _terrainPlacedBindings;
    private bool _lightNonzeroLogged, _lightUnavailableLogged, _staticUnavailableLogged;
    private bool _staticWasEnabled;
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
        internal readonly int Contact = GL.GetUniformLocation(program, "drtContactShadowsEnabled");
        internal readonly int Grid = GL.GetUniformLocation(program, "drtShadowGridEnabled");
        internal readonly int Prefetch = GL.GetUniformLocation(program, "drtMemoPrefetchEnabled");
    }

    private void ResetStaticDebugUniforms()
    {
        _staticDebugShader = null;
        _staticDebugLocations = null;
    }

    // Called only at explicit profile boundaries on the render thread. These
    // scalar counts prove admission policy without dumping scene GPU buffers.

    public DrtagxModSystem()
    {
        _staticTerrainMaps = new StaticTerrainShadowMaps(_terrainShadowMaps);
        // Keep performance-mode depth faces at half the normal resolution for this experiment.
        _performanceTerrainMaps = new StaticTerrainShadowMaps(_terrainShadowMaps)
            { TargetFaceSize = StaticTerrainShadowMaps.FaceSize / 2 };
        _publishedPlacedMaps = _staticTerrainMaps;
    }

    private void StartDynamicOcclusion(ICoreClientAPI api)
    {
        _staticLightSources = new StaticLightSources(api);
        _placedSurfaceBindings = new PlacedLightSurfaceBindings(_placedSurfaceCache, api);
        _terrainPlacedBindings = new PlacedLightTerrainBindings(_staticTerrainMaps, _staticTileBindings)
            { Api = api, SurfaceCacheBindings = _placedSurfaceBindings };
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
        _terrainShadowMaps.Reload();
        _movingUniforms.Reload();
        // Native program IDs/locations can be reused after shader reload.
        _surfaceLights.Reset();
        _terrainPlacedBindings?.Reload();
        _placedSurfaceBindings?.Reload();
        _placedSurfaceCache.Reload();
        _placedReceiverDemand.Reload();
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
        try
        {
            _movingSources.Capture(_clientApi, world);
        }
        catch (Exception ex)
        {
            if (!_lightUnavailableLogged)
            {
                _clientApi.Logger.Warning("[DRT AgX] Moving source snapshot unavailable: {0}", ex.Message);
                _lightUnavailableLogged = true;
            }
        }
    }

    private void RenderDynamicOcclusion(float deltaTime)
    {
        // Retire any native Use without Stop before rewriting source buffers
        // or capturing the GL state for this frame's shadow/deferred passes.
        _terrainPlacedBindings?.Restore();
        // .36 prepares the receiver targets before this .38 hook. Allocation
        // does not depend on cache readiness, so first-frame depth can bootstrap
        // while unsupported/failed deferred routes retain native lighting.
        // A permanent page-worker failure also pauses needless depth baking
        // until Reload clears it; cache readiness is not a bootstrap condition.
        bool placedLights = FrameQuality.Current.PlacedLights(Config.StaticLightShadows) &&
                            DeferredReceiverResources.Current is { Ready: true } &&
                            _placedSurfaceCache is not { Failed: true };
        if (_terrainPlacedBindings != null)
        {
            // Every supported receiving pass shares the published PLS sources.
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
        try
        {
            // Opaque depth is complete at this native hook. Poll prior GPU
            // receiver demand without waiting, then submit conservative current
            // geometry before source discovery or cold shadow-map admission.
            if (placedLights)
                _placedReceiverDemand.Update(api, placedView, placedProjection, _placedCameraWorld);
            else
                // Partial regional demand belongs to an enabled PLS view. A
                // later resume must sample fresh depth before finishing a cycle.
                _placedReceiverDemand.Suspend();
            _staticLightSources?.SetReceiverDemand(_placedReceiverDemand);
            _staticLightSources?.Update(_placedCameraWorld, placedView, placedProjection);
        }
        catch (Exception ex)
        {
            staticSourcesAvailable = false;
            PausePlacedGeometry();
            _placedSurfaceCache.Suspend();
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
            PausePlacedGeometry();
            _placedSurfaceCache.Suspend();
            UploadEntityPlacedNormals(api, _placedCameraWorld, false);
            return;
        }

        _surfaceLights.SetSunGridCamera(_placedCameraWorld);
        _surfaceLights.PublishSky(native, deferred);
        if (native == null || native.Disposed || native.LoadError)
        {
            PausePlacedGeometry();
            _placedSurfaceCache.Suspend();
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
                for (int i = 0; i < quantity; ++i) ReprojectPublishedLight(_lightPositions[i], _lightReprojection);
            }

            for (int i = 0; i < quantity; ++i) _movingSources.Kinds[i] = MovingLightKind.Entity;
        }

        _movingUniforms.Publish(deferred, quantity, _lightPositions, _lightColors, placedProjection);
        if (Config.DynamicLightShadows)
            _terrainShadowMaps.Render(api, _lightPositions, _lightColors, quantity,
                FrameQuality.Current.MovingLights(Config.DynamicShadowLightCount), deferred, _movingSources.Kinds,
                _movingSources.FirstPerson);
        else
        {
            _terrainShadowMaps.Disable(deferred);
        }


        bool placedRadianceReady = false;
        try
        {
            var requested = FrameQuality.Current.Performance ? _performanceTerrainMaps : _staticTerrainMaps;
            var inactive = ReferenceEquals(requested, _staticTerrainMaps)
                ? _performanceTerrainMaps
                : _staticTerrainMaps;
            // Inactive depth still observes geometry edits, so returning to a warm cache is safe.
            if (_staticLightSources != null && !ReferenceEquals(inactive, _publishedPlacedMaps))
                inactive.Pause(_staticLightSources, false);
            bool preparedReplacement = false;
            if (!ReferenceEquals(requested, _publishedPlacedMaps) && placedLights &&
                staticSourcesAvailable && _staticLightSources != null)
            {
                requested.UpdateAndRender(api, _staticLightSources, _placedCameraWorld, placedView, placedProjection,
                    deltaTime);
                preparedReplacement = true;
                if (CompletePlacedReplacement(requested, _publishedPlacedMaps))
                {
                    _publishedPlacedMaps = requested;
                    _terrainPlacedBindings?.PublishMaps(requested);
                    _staticTileBindings.Reload(); // Publication revisions belong to their cache owner.
                }
            }

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
                    _staticLightSources.SetResidentSources(_publishedPlacedMaps.CompletedLights);
            }

            // Completed maps remain resident for reuse. Receiver demand controls
            // which source truth enters this frame's relighting table.
            if (staticSourcesAvailable)
            {
                if (_staticLightSources != null) _placedSurfaceCache.ObserveSourceChanges(_staticLightSources);
                _placedSurfaceCache.Publish(api, _publishedPlacedMaps, Config,
                    _surfaceLights.BlockCalibrationGeneration);
            }
            else _placedSurfaceCache.Suspend();

            // Shipping terrain receives paged lighting from bindings 6/7. Keep
            // the old source/grid/depth-tile work only for its explicit consumers:
            // cache-off comparisons, developer views 5..7 and Alt+8 profiling.
            // View 7 also reads the legacy tile count before drawing its handoff.
                _staticTileBindings.PublishWithoutTiles(deferred,
                    placedLights && staticSourcesAvailable && _publishedPlacedMaps.Ready &&
                    _publishedPlacedMaps.TextureId != 0);

            placedRadianceReady = placedLights && staticSourcesAvailable &&
                                  _publishedPlacedMaps.Ready && _publishedPlacedMaps.TextureId != 0;
            SetStaticDebugUniforms(deferred, 0);
        }
        catch (Exception ex)
        {
            PausePlacedGeometry();
            _placedSurfaceCache.Suspend();
            _staticTileBindings.Disable(deferred); // Keep vanilla block light on any cache failure.
            placedRadianceReady = false;
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
            placedRadianceReady);

        if (quantity > 0 && !_lightNonzeroLogged)
        {
            api.Logger.Notification($"[DRT AgX] Published {quantity} independent moving-light sources.");
            _lightNonzeroLogged = true;
        }
    }

    private void PausePlacedGeometry()
    {
        // Source.Update already drained frame notifications. Preserve those
        // edits in both depth owners before a missing shader/update can return;
        // otherwise the next source update would clear the only delivery list.
        if (_staticLightSources != null)
        {
            _staticTerrainMaps.Pause(_staticLightSources, _staticWasEnabled);
            _performanceTerrainMaps.Pause(_staticLightSources, _staticWasEnabled);
        }

        _staticWasEnabled = false;
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
            // Opaque order 0.38 publishes menu settings even outside developer mode.
            if (locations.Contact >= 0)
                GL.Uniform1(locations.Contact, FrameQuality.Current.ContactShadows(Config.ContactShadows) ? 1 : 0);
            // Moving/placed comparisons and atmospheric sun share this style preference.
            var grid = Config.SunShadowGridEnabled;
            var prefetch = true;
            if (locations.Grid >= 0) GL.Uniform1(locations.Grid, grid ? 1 : 0);
            // The optional A/B override does not mutate settings or cache
            // generations; ordinary rendering retains the shader's default on.
            if (locations.Prefetch >= 0)
                GL.Uniform1(locations.Prefetch, prefetch ? 1 : 0);
        }
        finally
        {
            GL.UseProgram(previous);
        }
    }

    private static void ReprojectPublishedLight(float[] p, double[] transform)
    {
        double x = p[0], y = p[1], z = p[2];
        p[0] = (float)(transform[0] * x + transform[4] * y + transform[8] * z + transform[12]);
        p[1] = (float)(transform[1] * x + transform[5] * y + transform[9] * z + transform[13]);
        p[2] = (float)(transform[2] * x + transform[6] * y + transform[10] * z + transform[14]);
    }

    private void DisposeDynamicOcclusion()
    {
        ResetStaticDebugUniforms();
        _movingSources.Reset();
        _surfaceLights.Reset();
        Array.Clear(_entityPlacedPrograms);
        if (_clientApi != null) _clientApi.Event.ReloadShader -= ReloadStaticLightShaders;
        _placedGeometryBridge?.Dispose();
        _placedGeometryBridge = null;
        _staticLightSources?.Dispose();
        _staticLightSources = null;
        if (_clientApi != null && _lightMatrixCapture != null)
            _clientApi.Event.UnregisterRenderer(_lightMatrixCapture, EnumRenderStage.Before);
        _lightMatrixCapture = null;
        if (_clientApi != null && _shadowTextureRelease != null)
            _clientApi.Event.UnregisterRenderer(_shadowTextureRelease, EnumRenderStage.Opaque);
        _shadowTextureRelease = null;
        _terrainPlacedBindings?.Dispose();
        _terrainPlacedBindings = null;
        _placedSurfaceBindings?.Dispose();
        _placedSurfaceBindings = null;
        _placedSurfaceCache.Dispose();
        _placedReceiverDemand.Dispose();
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
                if (ready.Source.X == light.Source.X && ready.Source.Y == light.Source.Y &&
                    ready.Source.Z == light.Source.Z)
                {
                    found = true;
                    break;
                }

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