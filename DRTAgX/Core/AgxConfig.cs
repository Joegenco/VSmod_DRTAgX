namespace DRTAgX
{
    /// <summary>
    /// User and runtime configuration settings for DRTAgX display transforms,
    /// HDR exposure metering, bloom intensity, and dynamic/static shadow budgets.
    /// Serialized to and loaded from 'DRTAgX.json' in ModConfig.
    /// </summary>
    public class AgxConfig
    {
        public const int DefaultMovingLightCount = 2;

        /// <summary>Defaults release used to validate this saved configuration.</summary>
        // Leave absent JSON metadata null. An initializer with the current
        // release would incorrectly certify an unversioned legacy file.
        public string? ConfigVersion { get; set; }

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
        public int DynamicShadowLightCount { get; set; } = DefaultMovingLightCount;

        /// <summary>Enables world-anchored static shadow atlas caching for placed light sources (e.g. torches, lanterns).</summary>
        public bool StaticLightShadows { get; set; } = true;

        /// <summary>Snaps placed, moving and sun shadows to 32 cells per block on the receiving surface.</summary>
        // Retain the serialized name so existing sun-grid preferences become the shared style choice.
        public bool SunShadowGridEnabled { get; set; } = true;

        /// <summary>Enables experimental screen-space contact shadow tracing for foliage and fine geometry.</summary>
        public bool ContactShadows { get; set; } = false;

        /// <summary>Validate persisted values before GUI conversion and shader upload.</summary>
        internal bool Normalize()
        {
            // JSON can contain NaN/Infinity or manually entered values outside
            // supported controls. Keep each shader parameter finite and ordered;
            // valid settings pass through unchanged and do not trigger a save.
            var changed = false;

            AgxMinEv = Bound(AgxMinEv, AgxGuiDialog.MinEvFloor, AgxGuiDialog.MinEvCeil, AgxGuiDialog.DefaultMinEv);
            AgxMaxEv = Bound(AgxMaxEv, AgxGuiDialog.MaxEvFloor, AgxGuiDialog.MaxEvCeil, AgxGuiDialog.DefaultMaxEv);
            GreyPoint = Bound(GreyPoint, AgxGuiDialog.GreyPointMin, AgxGuiDialog.GreyPointMax,
                AgxGuiDialog.DefaultGreyPoint);
            Exposure = Bound(Exposure, AgxGuiDialog.ExposureMin, AgxGuiDialog.ExposureMax,
                AgxGuiDialog.DefaultExposure);
            AutoEvAdaptationSpeed = Bound(AutoEvAdaptationSpeed, AgxGuiDialog.SpeedMin, AgxGuiDialog.SpeedMax,
                AgxGuiDialog.DefaultAdaptSpeed);
            AutoEvMinStops = Bound(AutoEvMinStops, AgxGuiDialog.AutoEvMinFloor, AgxGuiDialog.AutoEvMinCeil,
                AgxGuiDialog.DefaultAutoEvMinStops);
            AutoEvMaxStops = Bound(AutoEvMaxStops, AgxGuiDialog.AutoEvMaxFloor, AgxGuiDialog.AutoEvMaxCeil,
                AgxGuiDialog.DefaultAutoEvMaxStops);
            BloomStrength = Bound(BloomStrength, 0, 2, 1);
            if (DynamicShadowLightCount is 1 or 2 or 4 or 6 or 10) return changed;
            DynamicShadowLightCount = DefaultMovingLightCount;
            return true;

            float Bound(float value, float min, float max, float fallback)
            {
                var next = float.IsFinite(value) ? System.Math.Clamp(value, min, max) : fallback;
                changed |= !value.Equals(next);
                return next;
            }
        }
    }
}