using System;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.Client.NoObf;

[assembly: ModInfo(
    name: "DRT AgX",
    modID: "drtagx",
    Version = "2.0.3",
    Description = "Client-side AgX display transform for Vintage Story.",
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
        private CelestialSunBindings? _celestialSun;
        private WholeFrameProfile? _frameProfile;
        private SheyderQualityAdapter? _sheyderQuality;
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

            LoadConfig(api);

            FogAndLightAssetBridge.Apply(api);
            _placedQualityShaders = new PlacedLightQualityShaders(api);
            StartDynamicOcclusion(api);

            api.Input.RegisterHotKey("drtagxsettings", "DRT AgX Settings", GlKeys.O, HotkeyType.GUIOrOtherControls);
            api.Input.SetHotKeyHandler("drtagxsettings", OnHotKeyToggle);
            UpdateDeveloperTools(api);

            _dialog = new AgxGuiDialog(api, Config, SaveConfig)
            {
                DebugViewGetter = () => _shadowDebugView,
                DebugViewSetter = view => _shadowDebugView = ClientSettings.DeveloperMode ? Math.Clamp(view, 0, 7) : 0
            };

            _hdrFinal = new HdrFinalHook(api, () => Config);
            _smaaHdr = new SmaaHdrCompatibility(api);
            _deferredComposition = new DeferredForwardCompositionBridge(api);
            _atmosphere = new AtmosphereRenderer(api);
            _celestialSun = new CelestialSunBindings(api);
            _frameProfile = WholeFrameProfile.Create(api);
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
                if (loaded != null)
                {
                    Config = loaded;
                    bool saveConfig = false;
                    if (Config.AutoExposureMeterVersion == 0)
                    {
                        // Retain user-tuned limits, while upgrading the former voxel defaults.
                        if (Math.Abs(Config.AutoEvMinStops + 0.8f) < 0.0001f) Config.AutoEvMinStops = AgxGuiDialog.DefaultAutoEvMinStops;
                        if (Math.Abs(Config.AutoEvMaxStops - 3.0f) < 0.0001f) Config.AutoEvMaxStops = AgxGuiDialog.DefaultAutoEvMaxStops;
                        Config.AutoExposureMeterVersion = 1;
                        saveConfig = true;
                    }
                    // Use the same allowed ceiling as the GUI, preserving valid saved limits.
                    if (Config.AutoEvMaxStops > AgxGuiDialog.AutoEvMaxCeil)
                    {
                        Config.AutoEvMaxStops = AgxGuiDialog.AutoEvMaxCeil;
                        saveConfig = true;
                    }
                    if (Config.DynamicShadowLightCount is not (1 or 2 or 4 or 6 or 10))
                    {
                        Config.DynamicShadowLightCount = 1;
                        saveConfig = true;
                    }
                    if (saveConfig) SaveConfig();
                }
                else
                {
                    SaveConfig();
                }
            }
            catch
            {
                Config = new AgxConfig();
                SaveConfig();
            }
        }

        private void SaveConfig()
        {
            if (_clientApi == null) return;
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
            UpdateDeveloperTools(_clientApi);
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

            // Publish before the Opaque atmosphere snapshot for both sun-shadow paths.
            if (_atmosphere != null) _atmosphere.SunShadowGridEnabled = Config.SunShadowGridEnabled;

            _hdrFinal?.BeforeFrame(deltaTime);
            AgxShaderUniforms.Apply(_clientApi, Config);
        }

        public override void Dispose()
        {
            _placedQualityShaders?.Dispose(); _placedQualityShaders = null;
            _frameProfile?.Dispose();
            _waterQuality?.Dispose(); _waterQuality = null;
            _gtaoResolution?.Dispose(); _gtaoResolution = null;
            _frameProfile = null;
            _sheyderQuality?.Dispose();
            _sheyderQuality = null;
            _aoFormats.Dispose();
            _celestialSun?.Dispose();
            _celestialSun = null;
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
