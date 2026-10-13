using System;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.Client.NoObf;

[assembly: ModInfo(
    name: "DRT AgX",
    modID: "drtagx",
    Version = "2.1.1",
    Description =
        "Client-side AgX tone mapping, HDR, atmospheric lighting and cached placed-light shadows for Vintage Story.",
    Authors = new[] { "Joegen" },
    Side = "Client"
)]

namespace DRTAgX
{
    /// <summary>
    /// Core mod lifecycle coordinator for DRTAgX.
    /// Manages client-side initialization, configuration persistence, input hotkeys,
    /// HDR render target allocations, dynamic light shadow matrices, and placed-light caching.
    /// </summary>
    public partial class DrtagxModSystem : ModSystem, IRenderer
    {
        private const string ConfigFileName = "DRTAgX.json";

        private ICoreClientAPI? _clientApi;
        private AgxGuiDialog? _dialog;
        public AgxConfig Config { get; private set; } = new AgxConfig();

        private readonly HdrSceneBuffers _hdrPrimaryBuffer = new HdrSceneBuffers();
        private HdrFinalHook? _hdrFinal;
        private SmaaHdrCompatibility? _smaaHdr;
        private readonly SurfaceLightBindings _surfaceLights = new();
        private DeferredForwardCompositionBridge? _deferredComposition;
        private AtmosphereRenderer? _atmosphere;
        private RainSplashLightingCompatibility? _rainSplashLighting;
        private ParticleLightCompatibility? _particleLighting;
        private AnimatedBlockLightBindings? _animatedBlockLights;
        private SheyderSunGeometryCompatibility? _sunGeometry;
        private RainCurtainsCompatibility? _rainCurtains;
        private GhostChunksCompatibility? _ghostChunks;
        private CelestialSunBindings? _celestialSun;
        private SheyderQualityAdapter? _sheyderQuality;
        private SheyderConfigDefaults? _sheyderDefaults;
        private SheyderDeferredPolicy? _sheyderDeferred;
        private GraphicsSettingsPolicy? _graphicsSettings;
        private WaterQualityScheduling? _waterQuality;
        private GtaoResolutionAdapter? _gtaoResolution;
        private PlacedLightQualityShaders? _placedQualityShaders;
        private readonly AoResourceFormats _aoFormats = new();

        // Standard early execution order so we don't clobber active passes
        public double RenderOrder => 0.38;
        public int RenderRange => 0;

        // Force this ModSystem to load and execute AFTER SheyderMod and other systems
        public override double ExecuteOrder() => 1.0;

        public override bool ShouldLoad(EnumAppSide forSide)
        {
            return forSide == EnumAppSide.Client;
        }

        public override void StartClientSide(ICoreClientAPI api)
        {
            _clientApi = api;
            base.StartClientSide(api);


            // Install fixed graphics controls before shader asset injection. The
            // curated dependency defaults also update Sheyder's already-loaded config.
            _graphicsSettings = new GraphicsSettingsPolicy(api);
            LoadConfig(api);
            _sheyderDeferred = new SheyderDeferredPolicy(api);
            _sheyderDefaults = new SheyderConfigDefaults(api);

            FogAndLightAssetBridge.Apply(api);
            _placedQualityShaders = new PlacedLightQualityShaders(api);
            StartDynamicOcclusion(api);

            api.Input.RegisterHotKey("drtagxsettings", "DRT AgX Settings", GlKeys.O, HotkeyType.GUIOrOtherControls);
            api.Input.SetHotKeyHandler("drtagxsettings", OnHotKeyToggle);

            _dialog = new AgxGuiDialog(api, Config, SaveConfig)
                ;

            _hdrFinal = new HdrFinalHook(api, () => Config);
            _smaaHdr = new SmaaHdrCompatibility(api);
            _deferredComposition = new DeferredForwardCompositionBridge(api);
            _atmosphere = new AtmosphereRenderer(api);
            _celestialSun = new CelestialSunBindings(api);
            // Native splash visibility glow must not become HDR self-illumination.
            _rainSplashLighting = new RainSplashLightingCompatibility(api);
            _particleLighting = new ParticleLightCompatibility(api);
            _animatedBlockLights = new AnimatedBlockLightBindings(api);
            _sunGeometry = new SheyderSunGeometryCompatibility(api);
            // Optional weather shaders borrow the initialized atmosphere's
            // existing UBO/LUT bindings; RainCurtains retains its own draw.
            _rainCurtains = new RainCurtainsCompatibility(api);
            _ghostChunks = new GhostChunksCompatibility(api);
            _sheyderQuality = new SheyderQualityAdapter(api);
            // Sheyder owns volumetric step placement, native mip smoothing and
            // final binding; atmosphere customization is confined to radiance.
            _waterQuality = new WaterQualityScheduling(api);
            _gtaoResolution = new GtaoResolutionAdapter(api);
            _aoFormats.Start(api);


            api.Event.RegisterRenderer(this, EnumRenderStage.Before, "drtagx_uniforms");
        }

        private void LoadConfig(ICoreClientAPI api)
        {
            try
            {
                AgxConfig? loaded = api.LoadModConfig<AgxConfig>(ConfigFileName);
                Config = ConfigDefaultsVersion.ResolveAgx(loaded);
                if (ReferenceEquals(Config, loaded))
                {
                    bool saveConfig = false;
                    if (Config.AutoExposureMeterVersion == 0)
                    {
                        // Retain user-tuned limits, while upgrading the former voxel defaults.
                        if (Math.Abs(Config.AutoEvMinStops + 0.8f) < 0.0001f)
                            Config.AutoEvMinStops = AgxGuiDialog.DefaultAutoEvMinStops;
                        if (Math.Abs(Config.AutoEvMaxStops - 3.0f) < 0.0001f)
                            Config.AutoEvMaxStops = AgxGuiDialog.DefaultAutoEvMaxStops;
                        Config.AutoExposureMeterVersion = 1;
                        saveConfig = true;
                    }

                    saveConfig |= Config.Normalize();

                    if (saveConfig) SaveConfig();
                }
                else
                {
                    // Missing or older release metadata resets the whole file,
                    // including the moving-light default, before any GUI/renderers use it.
                    SaveConfig();
                    api.Logger.Notification("[DRT AgX] Configuration reset to {0} defaults.",
                        ConfigDefaultsVersion.Current);
                }
            }
            catch
            {
                Config = ConfigDefaultsVersion.CreateAgxDefaults();
                SaveConfig();
            }
        }

        private void SaveConfig()
        {
            if (_clientApi == null) return;
            Config.Normalize();
            Config.ConfigVersion = ConfigDefaultsVersion.Current;
            Config.AutoExposureMeterVersion = 1;
            try
            {
                _clientApi.StoreModConfig(Config, ConfigFileName);
            }
            catch (Exception ex)
            {
                _clientApi.Logger.Error($"[DRT AgX] Failed to save {ConfigFileName}: {ex.Message}");
            }
        }

        private bool OnHotKeyToggle(KeyCombination comb)
        {
            if (_dialog != null)
            {
                if (_dialog.IsOpened())
                {
                    _dialog.TryClose();
                }
                else
                {
                    _dialog.TryOpen();
                }
            }

            return true;
        }

        public void OnRenderFrame(float deltaTime, EnumRenderStage stage)
        {
            if (_clientApi?.Render == null) return;
            // Vanilla can change developer mode during play. Revoke diagnostics
            // before any render work, including already-enabled GPU profiling.
            if (stage == EnumRenderStage.Opaque)
            {
                RenderDynamicOcclusion(deltaTime);
                return;
            }

            _hdrPrimaryBuffer.Ensure(_clientApi);
            // GUI edits become one immutable policy before any world/shadow draws in this frame.
            FrameQuality.Publish(Config);
            _placedQualityShaders?.BeforeFrame();
            if (_placedQualityShaders?.CasterChangedThisFrame == true)
            {
                _staticTerrainMaps.ResetShaderLocations();
                _performanceTerrainMaps.ResetShaderLocations();
            }

            _aoFormats.Ensure(_clientApi);
            _surfaceLights.BindSurfaces(_clientApi);
            _placedSurfaceCache.BeforeFrame(_clientApi, _publishedPlacedMaps, Config,
                _surfaceLights.BlockCalibrationGeneration);

            // Publish before the Opaque atmosphere snapshot for both sun-shadow paths.
            if (_atmosphere != null) _atmosphere.SunShadowGridEnabled = Config.SunShadowGridEnabled;

            _hdrFinal?.BeforeFrame(deltaTime);
            AgxShaderUniforms.Apply(_clientApi, Config);
        }

        public override void Dispose()
        {
            _placedQualityShaders?.Dispose();
            _placedQualityShaders = null;
            _waterQuality?.Dispose();
            _waterQuality = null;
            _gtaoResolution?.Dispose();
            _gtaoResolution = null;
            _sheyderQuality?.Dispose();
            _sheyderQuality = null;
            _sheyderDefaults?.Dispose();
            _sheyderDefaults = null;
            _sheyderDeferred?.Dispose();
            _sheyderDeferred = null;
            _graphicsSettings?.Dispose();
            _graphicsSettings = null;
            _aoFormats.Dispose();
            _celestialSun?.Dispose();
            _celestialSun = null;
            _rainSplashLighting?.Dispose();
            _rainSplashLighting = null;
            _animatedBlockLights?.Dispose();
            _particleLighting?.Dispose();
            _particleLighting = null;
            _animatedBlockLights = null;
            _sunGeometry?.Dispose();
            _sunGeometry = null;
            _rainCurtains?.Dispose();
            _rainCurtains = null;
            _ghostChunks?.Dispose();
            _ghostChunks = null;
            _atmosphere?.Dispose();
            _atmosphere = null;
            DisposeDynamicOcclusion();
            _deferredComposition?.Dispose();
            _deferredComposition = null;
            _hdrFinal?.Dispose();
            _hdrFinal = null;
            _smaaHdr?.Dispose();
            _smaaHdr = null;
            if (_dialog != null)
            {
                _dialog.TryClose();
                _dialog.Dispose();
                _dialog = null;
            }

            if (_clientApi != null)
            {
                _clientApi.Event.UnregisterRenderer(this, EnumRenderStage.Before);
                _clientApi.Event.UnregisterRenderer(this, EnumRenderStage.Opaque);
                _clientApi = null;
            }

            base.Dispose();
        }
    }
}