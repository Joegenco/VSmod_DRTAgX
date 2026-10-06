namespace DRTAgX
{
    /// <summary>
    /// User and runtime configuration settings for DRTAgX display transforms,
    /// HDR exposure metering, bloom intensity, and dynamic/static shadow budgets.
    /// Serialized to and loaded from 'DRTAgX.json' in ModConfig.
    /// </summary>
    public class AgxConfig
    {
        /// <summary>Reduces secondary effect detail while retaining deferred lighting and saved preferences.</summary>
        public bool PerformanceMode { get; set; } = false;
        /// <summary>Minimum input EV bound for AgX logarithmic dynamic range encoding (default: -9.5 EV).</summary>
        // The GUI reset constants also define defaults for new/missing saved settings.
        public float AgxMinEv { get; set; } = AgxGuiDialog.DefaultMinEv;

        /// <summary>Maximum input EV bound for AgX logarithmic dynamic range encoding (default: 3.2 EV).</summary>
        public float AgxMaxEv { get; set; } = AgxGuiDialog.DefaultMaxEv;

        /// <summary>Scene mid-grey reference point (standard radiometric: 0.18).</summary>
        public float GreyPoint { get; set; } = AgxGuiDialog.DefaultGreyPoint;

        private float _plsSaturation = AgxGuiDialog.DefaultPlsSaturation;

        /// <summary>Placed-light saturation multiplier; 1 preserves native colors and 0 produces white light.</summary>
        public float PlsSaturation
        {
            get => _plsSaturation;
            // Keep saved/manual configuration values within the GUI's range.
            set => _plsSaturation = float.IsFinite(value)
                ? System.Math.Clamp(value, AgxGuiDialog.PlsSaturationMin, AgxGuiDialog.PlsSaturationMax)
                : AgxGuiDialog.DefaultPlsSaturation;
        }

        /// <summary>Manual exposure compensation in EV stops.</summary>
        public float Exposure { get; set; } = AgxGuiDialog.DefaultExposure;

        /// <summary>Enables temporal auto-exposure adaptation based on center-weighted HDR scene luminance.</summary>
        public bool AutoExposureEnabled { get; set; } = true;

        /// <summary>Auto-exposure temporal adaptation speed in seconds.</summary>
        public float AutoEvAdaptationSpeed { get; set; } = AgxGuiDialog.DefaultAdaptSpeed;

        /// <summary>Minimum allowed auto-exposure EV stop offset.</summary>
        public float AutoEvMinStops { get; set; } = AgxGuiDialog.DefaultAutoEvMinStops;

        /// <summary>Maximum allowed auto-exposure EV stop offset.</summary>
        public float AutoEvMaxStops { get; set; } = AgxGuiDialog.DefaultAutoEvMaxStops;

        /// <summary>Enables the HDR bloom glow around bright surfaces.</summary>
        public bool BloomEnabled { get; set; } = true;

        /// <summary>Multiplier for the existing bloom look; 1 preserves its default strength.</summary>
        public float BloomStrength { get; set; } = 1.0f;

        /// <summary>Internal version tag for auto-exposure metering defaults migration.</summary>
        public int AutoExposureMeterVersion { get; set; }

        /// <summary>Enables real-time layered depth map rendering for dynamic point lights.</summary>
        public bool DynamicLightShadows { get; set; } = true;

        /// <summary>Maximum number of dynamic point lights casting real-time shadows simultaneously (1, 2, 4, 6, 10).</summary>
        public int DynamicShadowLightCount { get; set; } = 1;

        /// <summary>Enables world-anchored static shadow atlas caching for placed light sources (e.g. torches, lanterns).</summary>
        public bool StaticLightShadows { get; set; } = true;

        /// <summary>Includes every resident terrain pass in placed-light shadow baking; animated entities remain excluded.</summary>
        public bool PlacedLightAllTerrainPasses { get; set; } = true;

        /// <summary>Snaps placed, moving and sun shadows to 32 cells per block on the receiving surface.</summary>
        // Retain the serialized name so existing sun-grid preferences become the shared style choice.
        public bool SunShadowGridEnabled { get; set; } = true;

        /// <summary>Limits placed-shadow work to light volumes reaching the view; range and cache budgets still apply when off.</summary>
        public bool PlacedLightViewCulling { get; set; } = true;

        /// <summary>Enables experimental screen-space contact shadow tracing for foliage and fine geometry.</summary>
        public bool ContactShadows { get; set; } = false;
    }
}
