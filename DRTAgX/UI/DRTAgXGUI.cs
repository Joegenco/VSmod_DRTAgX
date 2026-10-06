using System;
using Vintagestory.API.Client;
using Vintagestory.Client.NoObf;

namespace DRTAgX
{
    public class AgxGuiDialog : GuiDialog
    {

        // EDIT GUI COPY HERE: titles and hover descriptions stay together.
        // Layout and callbacks below use these entries, including developer-only controls.
        private readonly record struct Option(string Title, string Description);
        private const string DialogTitle = "DRT AgX";
        private const string ResetText = "Reset page";
        private const string ResetHint = "Restore this page's defaults. Changes are saved immediately.";
        private const string RowResetText = "Reset";
        private const string RowResetHint = "Restore this option's default.";
        private const string DoneText = "Done";
        private const string DoneHint = "Close settings. Your changes are already saved.";
        private const string EvUnit = " EV";
        private const string SecondsUnit = " s";
        private const string LightsUnit = " lights";
        private static readonly string[] PageTitles = { "Lighting", "Shadows", "Color", "Developer" };
        private static readonly string[] PageDescriptions = {
            "Brightness, adaptation and glow.",
            "Shadow style and performance.",
            "Fine-tune the AgX display range and placed-light colors.",
            "Diagnostics. Alt+1–7: views; Alt+8/9: timings."
        };
        private static readonly Option ExposureOption = new("Exposure", "Brighten or darken the image. +1 EV doubles exposure; -1 EV halves it. Also works with auto exposure.");
        private static readonly Option AutoOption = new("Auto exposure", "Adapt brightness to the scene, like eyes adjusting between outdoors and a cave. Off uses your Exposure setting alone.");
        private static readonly Option AdaptOption = new("Adapt time", "Time in seconds for brightness to adjust. Higher values make transitions slower and smoother.");
        private static readonly Option AutoMinOption = new("Min auto EV", "Limit how much auto exposure can darken a bright scene. A lower value allows more darkening.");
        private static readonly Option AutoMaxOption = new("Max auto EV", "Limit how much auto exposure can brighten a dark scene. A higher value makes caves and nights easier to see.");
        private static readonly Option BloomOption = new("Bloom", "Add a soft glow around bright surfaces. Also requires Bloom enabled in the game's graphics settings.");
        private static readonly Option BloomStrengthOption = new("Bloom strength", "Scale the glow: 100% keeps the default look. Higher values spread more bright light into the image.");
        private static readonly Option MovingOption = new("Moving shadows", "Cast terrain shadows from held, dropped and moving lights. Turning this off saves GPU work.");
        private static readonly Option MovingCountOption = new("Shadowed lights", "Maximum moving lights casting shadows at once. More lights cost more GPU time; 1 is the default.");
        private static readonly Option PlacedOption = new("Placed shadows", "Use cached terrain shadows and directional lighting for placed torches, lanterns and other light blocks.");
        private static readonly Option AllPlacedPassesOption = new("PLS all passes", "Cast and receive placed-light shadows across all terrain passes, including grass, no-cull geometry, decor, transparent blocks and liquids. Grass shades by shadow occlusion with stable self-shadowing. When disabled, away-facing grass receives one quarter of facing placed light. Animated entities are excluded from casting. Medium performance impact. Changing this rebuilds cached maps. Wind shadows remain stable as foliage moves.");
        private static readonly Option GridOption = new("32 cell shadow", "Use a world-aligned grid of 32 shadow cells per block for placed lights, moving lights and the sun. Off gives continuous shadow sampling. Shadow map resolution stays the same.");
        private static readonly Option CullingOption = new("PLS view culling", "PLS means placed light shadows. Skip light volumes outside the view to save work. Off keeps nearby lights active in every direction, within the same range and cache budget, and can reduce FPS.");
        private static readonly Option ContactOption = new("Contact shadows", "Add small screen-space sun shadows near foliage. Only visible geometry can contribute; costs extra GPU time.");
        private static readonly Option MinEvOption = new("Dark range", "AgX lower exposure bound. Lower values preserve more dark detail; higher values deepen the darkest tones.");
        private static readonly Option MaxEvOption = new("Bright range", "AgX upper exposure bound. Higher values retain a wider highlight range; lower values compress highlights sooner.");
        private static readonly Option GreyOption = new("Mid grey", "Reference brightness for the display transform. 0.18 is the standard starting point; changes shift the tonal balance.");
        private static readonly Option PlsSaturationOption = new("PLS saturation", "Color intensity of placed lights. 100% uses original colors; 0% makes the light white. Changes apply immediately.");
        private static readonly Option DebugOption = new("Debug view", "Inspect moving shadow visibility, projection type or facing, and placed cache state, coverage, visibility or lighting. Normal restores the game image.");
        private static readonly Option PlacedTimingOption = new("Placed timing", "Log placed-light CPU/GPU timing and cache counters to client-main.log. Alt+8 toggles this too.");
        private static readonly Option MovingTimingOption = new("Moving timing", "Log moving-light CPU/GPU timing to client-main.log. Alt+9 toggles this too.");
        private static readonly string[] DebugCodes = { "0", "1", "2", "3", "4", "5", "6", "7" };
        private static readonly string[] DebugTitles = { "Normal", "Moving visibility", "Moving projection", "Moving facing", "Placed cache", "Placed coverage", "Placed visibility", "Placed lighting" };

        public override string ToggleKeyCombinationCode => "drtagxsettings";
        private readonly AgxConfig _config;
        private readonly Action _onSaveConfig;
        internal Func<int>? DebugViewGetter { get; set; }
        internal Action<int>? DebugViewSetter { get; set; }
        private int _page;
        private bool _developerMode;
        private int _shownDebugView;
        private bool _shownPlacedTiming, _shownMovingTiming;
        private int _rowY;
        private const int DialogWidth = 490;
        private const int RowHeight = 38;
        private static readonly int[] ShadowLightSteps = { 1, 2, 4, 6, 10 };

        // Default Constants for Resets
        public const float DefaultMinEv = -9.5f;
        public const float DefaultMaxEv = 3.2f;
        public const float DefaultGreyPoint = 0.18f;
        public const float DefaultPlsSaturation = 1.25f;
        public const float DefaultExposure = 0.0f;
        public const float DefaultAdaptSpeed = 2.4f;
        public const float DefaultAutoEvMinStops = -2.0f;
        public const float DefaultAutoEvMaxStops = 2.0f;

        // Configuration Boundaries
        public const float MinEvFloor = -11.5f;
        public const float MinEvCeil = -7.5f;
        public const float MaxEvFloor = 1.0f;
        public const float MaxEvCeil = 5.0f;

        public const float GreyPointMin = 0.01f;
        public const float GreyPointMax = 1.0f;
        public const float PlsSaturationMin = 0.0f;
        public const float PlsSaturationMax = 2.0f;

        public const float ExposureMin = -1.5f;
        public const float ExposureMax = 1.5f;

        public const float SpeedMin = 0.1f;
        public const float SpeedMax = 10.0f;

        public const float AutoEvMinFloor = -3.0f;
        public const float AutoEvMinCeil = -0.01f;

        public const float AutoEvMaxFloor = 0.01f;
        public const float AutoEvMaxCeil = 4.0f;

        public AgxGuiDialog(ICoreClientAPI capi, AgxConfig config, Action onSaveConfig) : base(capi)
        {
            _config = config;
            _onSaveConfig = onSaveConfig;
            SetupDialog();
        }


        private void SetupDialog()
        {
            // Rebuild only on page changes or developer-mode changes, never while dragging a slider.
            SingleComposer?.Dispose();
            _developerMode = ClientSettings.DeveloperMode;
            _page = Math.Clamp(_page, 0, _developerMode ? 3 : 2);
            ElementBounds dialog = ElementStdBounds.AutosizedMainDialog.WithAlignment(EnumDialogArea.CenterMiddle);
            ElementBounds background = ElementBounds.Fill.WithFixedPadding(GuiStyle.ElementToDialogPadding);
            background.BothSizing = ElementSizing.FitToChildren;
            background.WithChildren(ElementBounds.Fixed(0, 0, DialogWidth, 432));
            dialog.WithChildren(background);
            var tabs = new GuiTab[_developerMode ? 4 : 3];
            for (int i = 0; i < tabs.Length; i++) tabs[i] = new GuiTab { Name = PageTitles[i], DataInt = i };
            SingleComposer = capi.Gui.CreateCompo("AgxSettingsDialog", dialog)
                .AddShadedDialogBG(background)
                .AddDialogTitleBar(DialogTitle, OnTitleBarCloseClicked)
                .BeginChildElements(background)
                .AddHorizontalTabs(tabs, ElementBounds.Fixed(0, 38, DialogWidth, 30),
                    page => { _page = page; SetupDialog(); }, CairoFont.WhiteSmallText(), CairoFont.WhiteSmallText(), "pages")
                .AddStaticText(PageDescriptions[_page], CairoFont.WhiteSmallText(),
                    ElementBounds.Fixed(0, 78, DialogWidth, 25));
            _rowY = 112;
            if (_page == 0)
            {
                AddSwitchRow(new Option("Performance mode", "Lower effect detail for faster rendering. Disables placed-light and contact shadows, retaining native block lighting. Your individual settings return in Normal mode."),
                    "switchPerformanceMode", _config.PerformanceMode,
                    enabled => { _config.PerformanceMode = enabled; SaveAndRefresh(); });
                AddSwitchRow(AutoOption, "switchAutoExposure", _config.AutoExposureEnabled,
                    enabled => { _config.AutoExposureEnabled = enabled; SaveAndRefresh(); });
                AddSliderRow(ExposureOption, "sliderExposure", OnExposureChanged, () => _config.Exposure = DefaultExposure);
                AddSliderRow(AdaptOption, "sliderAutoExp", OnAdaptSpeedChanged, () => _config.AutoEvAdaptationSpeed = DefaultAdaptSpeed);
                AddSliderRow(AutoMinOption, "sliderAutoEvMin", OnAutoEvMinChanged, () => _config.AutoEvMinStops = DefaultAutoEvMinStops);
                AddSliderRow(AutoMaxOption, "sliderAutoEvMax", OnAutoEvMaxChanged, () => _config.AutoEvMaxStops = DefaultAutoEvMaxStops);
                AddSwitchRow(BloomOption, "switchBloom", _config.BloomEnabled,
                    enabled => { _config.BloomEnabled = enabled; SaveAndRefresh(); });
                AddSliderRow(BloomStrengthOption, "sliderBloomStrength",
                    value => { _config.BloomStrength = value / 100f; SaveAndRefresh(); return true; },
                    () => _config.BloomStrength = 1f);
            }
            else if (_page == 1)
            {
                AddSwitchRow(GridOption, "switchSunGrid", _config.SunShadowGridEnabled, OnSunShadowGridChanged);
                AddSwitchRow(MovingOption, "switchCsm", _config.DynamicLightShadows, OnDynamicShadowsChanged);
                AddSliderRow(MovingCountOption, "sliderCsmCount", OnShadowLightCountChanged, () => _config.DynamicShadowLightCount = 1);
                AddSwitchRow(PlacedOption, "switchStaticLights", _config.StaticLightShadows, OnStaticLightShadowsChanged);
                AddSwitchRow(AllPlacedPassesOption, "switchPlacedAllPasses", _config.PlacedLightAllTerrainPasses,
                    enabled => { _config.PlacedLightAllTerrainPasses = enabled; SaveAndRefresh(); });
                AddSwitchRow(CullingOption, "switchPlacedCulling", _config.PlacedLightViewCulling,
                    enabled => { _config.PlacedLightViewCulling = enabled; SaveAndRefresh(); });
                AddSwitchRow(ContactOption, "switchContactShadows", _config.ContactShadows, OnContactShadowsChanged);
            }
            else if (_page == 2)
            {
                AddSliderRow(MinEvOption, "sliderMinEv", OnMinEvChanged, () => _config.AgxMinEv = DefaultMinEv);
                AddSliderRow(MaxEvOption, "sliderMaxEv", OnMaxEvChanged, () => _config.AgxMaxEv = DefaultMaxEv);
                AddSliderRow(GreyOption, "sliderGreyPoint", OnGreyPointChanged, () => _config.GreyPoint = DefaultGreyPoint);
                AddSliderRow(PlsSaturationOption, "sliderPlsSaturation",
                    value => { _config.PlsSaturation = value / 100f; SaveAndRefresh(); return true; },
                    () => _config.PlsSaturation = DefaultPlsSaturation);
            }
            else
            {
                _shownDebugView = Math.Clamp(DebugViewGetter?.Invoke() ?? 0, 0, 7);
                _shownPlacedTiming = PlacedLightGpuProfile.Enabled;
                _shownMovingTiming = MovingLightGpuProfile.Enabled;
                SingleComposer.AddStaticText(DebugOption.Title, CairoFont.WhiteSmallText(), ElementBounds.Fixed(0, _rowY, 145, 25))
                    .AddDropDown(DebugCodes, DebugTitles, _shownDebugView,
                        (code, selected) => { if (ClientSettings.DeveloperMode) DebugViewSetter?.Invoke(int.Parse(code)); },
                        ElementBounds.Fixed(150, _rowY, 285, 30), "debugView")
                    .AddHoverText(DebugOption.Description, CairoFont.WhiteSmallText(), 360,
                        ElementBounds.Fixed(0, _rowY, 435, 30));
                _rowY += RowHeight;
                AddSwitchRow(PlacedTimingOption, "placedTiming", _shownPlacedTiming, _ => PlacedLightGpuProfile.Toggle(capi));
                AddSwitchRow(MovingTimingOption, "movingTiming", _shownMovingTiming, _ => MovingLightGpuProfile.Toggle(capi));
            }
            SingleComposer
                .AddSmallButton(ResetText, ResetPage, ElementBounds.Fixed(0, 398, 130, 28))
                .AddHoverText(ResetHint, CairoFont.WhiteSmallText(), 330, ElementBounds.Fixed(0, 398, 130, 28))
                .AddSmallButton(DoneText, () => { TryClose(); return true; }, ElementBounds.Fixed(DialogWidth - 100, 398, 100, 28))
                .AddHoverText(DoneHint, CairoFont.WhiteSmallText(), 300, ElementBounds.Fixed(DialogWidth - 100, 398, 100, 28))
                .EndChildElements()
                .Compose();
            SingleComposer.GetHorizontalTabs("pages").SetValue(_page, false);
            RefreshSliderValues();
        }

        private void AddSwitchRow(Option option, string key, bool value, Action<bool> changed)
        {
            SingleComposer.AddStaticText(option.Title, CairoFont.WhiteSmallText(), ElementBounds.Fixed(0, _rowY, 145, 25))
                .AddSwitch(changed, ElementBounds.Fixed(150, _rowY, 30, 25), key)
                .AddHoverText(option.Description, CairoFont.WhiteSmallText(), 360, ElementBounds.Fixed(0, _rowY, 435, 30));
            SingleComposer.GetSwitch(key).SetValue(value);
            _rowY += RowHeight;
        }

        private void AddSliderRow(Option option, string key, Func<int, bool> changed, Action reset)
        {
            SingleComposer.AddStaticText(option.Title, CairoFont.WhiteSmallText(), ElementBounds.Fixed(0, _rowY, 145, 25))
                .AddSlider(value => changed(value), ElementBounds.Fixed(150, _rowY, 270, 25), key)
                .AddHoverText(option.Description, CairoFont.WhiteSmallText(), 360, ElementBounds.Fixed(0, _rowY, 420, 30))
                .AddSmallButton(RowResetText, () => ResetValue(reset), ElementBounds.Fixed(435, _rowY, 55, 25))
                .AddHoverText(RowResetHint, CairoFont.WhiteSmallText(), 250, ElementBounds.Fixed(435, _rowY, 55, 25));
            _rowY += RowHeight;
        }

        public override void OnGuiOpened()
        {
            SetupDialog(); // Refresh values and vanilla developer-mode visibility on every open.
            base.OnGuiOpened();
        }

        public override void OnRenderGUI(float deltaTime)
        {
            if (_developerMode != ClientSettings.DeveloperMode) SetupDialog();
            if (_page == 3 && _developerMode)
            {
                // Hotkeys may change diagnostics while this page is open. Update
                // only on changes: dropdown text must not be recomposed every frame.
                if (_shownPlacedTiming != PlacedLightGpuProfile.Enabled)
                {
                    _shownPlacedTiming = PlacedLightGpuProfile.Enabled;
                    SingleComposer.GetSwitch("placedTiming").SetValue(_shownPlacedTiming);
                }
                if (_shownMovingTiming != MovingLightGpuProfile.Enabled)
                {
                    _shownMovingTiming = MovingLightGpuProfile.Enabled;
                    SingleComposer.GetSwitch("movingTiming").SetValue(_shownMovingTiming);
                }
                int view = Math.Clamp(DebugViewGetter?.Invoke() ?? 0, 0, 7);
                if (_shownDebugView != view)
                {
                    _shownDebugView = view;
                    SingleComposer.GetDropDown("debugView").SetSelectedIndex(view);
                }
            }
            base.OnRenderGUI(deltaTime);
        }

        private void SaveAndRefresh()
        {
            _onSaveConfig();
            RefreshSliderValues();
        }

        private bool ResetPage()
        {
            // Reset only the visible group; a visual choice never changes another page.
            if (_page == 0)
            {
                _config.Exposure = DefaultExposure;
                // The preset is a Lighting preference; page reset returns it to Normal.
                _config.PerformanceMode = false;
                _config.AutoExposureEnabled = true;
                _config.AutoEvAdaptationSpeed = DefaultAdaptSpeed;
                _config.AutoEvMinStops = DefaultAutoEvMinStops;
                _config.AutoEvMaxStops = DefaultAutoEvMaxStops;
                _config.BloomEnabled = true;
                _config.BloomStrength = 1f;
                _onSaveConfig();
            }
            else if (_page == 1)
            {
                _config.DynamicLightShadows = _config.StaticLightShadows = _config.SunShadowGridEnabled = true;
                _config.PlacedLightViewCulling = true;
                _config.PlacedLightAllTerrainPasses = true;
                _config.DynamicShadowLightCount = 1;
                _config.ContactShadows = false;
                _onSaveConfig();
            }
            else if (_page == 2)
            {
                _config.AgxMinEv = DefaultMinEv;
                _config.AgxMaxEv = DefaultMaxEv;
                _config.GreyPoint = DefaultGreyPoint;
                _config.PlsSaturation = DefaultPlsSaturation;
                _onSaveConfig();
            }
            else if (ClientSettings.DeveloperMode)
            {
                DebugViewSetter?.Invoke(0);
                MovingLightGpuProfile.Disable();
                PlacedLightGpuProfile.Disable();
            }
            SetupDialog();
            return true;
        }

        private void OnDynamicShadowsChanged(bool enabled)
        {
            _config.DynamicLightShadows = enabled;
            SaveAndRefresh();
        }

        private void OnStaticLightShadowsChanged(bool enabled)
        {
            _config.StaticLightShadows = enabled;
            SaveAndRefresh();
        }

        private void OnSunShadowGridChanged(bool enabled)
        {
            _config.SunShadowGridEnabled = enabled;
            SaveAndRefresh();
        }

        private void OnContactShadowsChanged(bool enabled)
        {
            _config.ContactShadows = enabled;
            SaveAndRefresh();
        }

        private bool OnShadowLightCountChanged(int value)
        {
            // Use only tested budgets, with no jumping or ambiguous intermediate counts.
            _config.DynamicShadowLightCount = ShadowLightSteps[Math.Clamp(value, 0, ShadowLightSteps.Length - 1)];
            SaveAndRefresh();
            return true;
        }

        private bool ResetValue(Action resetAction)
        {
            resetAction();
            SaveAndRefresh();
            return true;
        }


        private void RefreshSliderValues()
        {
            if (SingleComposer == null) return;
            if (_page == 0)
            {
                SingleComposer.GetSwitch("switchPerformanceMode").SetValue(_config.PerformanceMode);
                SingleComposer.GetSwitch("switchAutoExposure").SetValue(_config.AutoExposureEnabled);
                SingleComposer.GetSwitch("switchBloom").SetValue(_config.BloomEnabled);
                SetFloatSlider("sliderExposure", _config.Exposure, ExposureMin, ExposureMax, 0.1f, EvUnit);
                SetFloatSlider("sliderAutoExp", _config.AutoEvAdaptationSpeed, SpeedMin, SpeedMax, 0.1f, SecondsUnit, _config.AutoExposureEnabled);
                SetFloatSlider("sliderAutoEvMin", _config.AutoEvMinStops, AutoEvMinFloor, AutoEvMinCeil, 0.1f, EvUnit, _config.AutoExposureEnabled);
                SetFloatSlider("sliderAutoEvMax", _config.AutoEvMaxStops, AutoEvMaxFloor, AutoEvMaxCeil, 0.1f, EvUnit, _config.AutoExposureEnabled);
                var bloom = SingleComposer.GetSlider("sliderBloomStrength");
                bloom.SetValues((int)Math.Round(_config.BloomStrength * 100), 0, 200, 5, "%");
                bloom.Enabled = _config.BloomEnabled;
                bloom.ShowTextWhenResting = true;
            }
            else if (_page == 1)
            {
                SingleComposer.GetSwitch("switchCsm").SetValue(_config.DynamicLightShadows);
                SingleComposer.GetSwitch("switchStaticLights").SetValue(_config.StaticLightShadows);
                SingleComposer.GetSwitch("switchPlacedAllPasses").SetValue(_config.PlacedLightAllTerrainPasses);
                SingleComposer.GetSwitch("switchSunGrid").SetValue(_config.SunShadowGridEnabled);
                SingleComposer.GetSwitch("switchPlacedCulling").SetValue(_config.PlacedLightViewCulling);
                SingleComposer.GetSwitch("switchContactShadows").SetValue(_config.ContactShadows);
                var count = SingleComposer.GetSlider("sliderCsmCount");
                count.SetValues(Math.Max(0, Array.IndexOf(ShadowLightSteps, _config.DynamicShadowLightCount)), 0, ShadowLightSteps.Length - 1, 1);
                count.OnSliderTooltip = value => ShadowLightSteps[Math.Clamp(value, 0, ShadowLightSteps.Length - 1)] + LightsUnit;
                count.OnSliderRestingText = count.OnSliderTooltip;
                count.ShowTextWhenResting = true;
                count.Enabled = _config.DynamicLightShadows;
            }
            else if (_page == 2)
            {
                SetFloatSlider("sliderMinEv", _config.AgxMinEv, MinEvFloor, MinEvCeil, 0.05f, EvUnit);
                SetFloatSlider("sliderMaxEv", _config.AgxMaxEv, MaxEvFloor, MaxEvCeil, 0.05f, EvUnit);
                SetFloatSlider("sliderGreyPoint", _config.GreyPoint, GreyPointMin, GreyPointMax, 0.01f, "");
                // Display percentage values while storing a linear multiplier.
                var saturation = SingleComposer.GetSlider("sliderPlsSaturation");
                saturation.SetValues((int)Math.Round(_config.PlsSaturation * 100),
                    (int)(PlsSaturationMin * 100), (int)(PlsSaturationMax * 100), 5, "%");
                saturation.ShowTextWhenResting = true;
            }
        }

        private void SetFloatSlider(string key, float value, float min, float max, float step, string unit, bool enabled = true)
        {
            var slider = SingleComposer.GetSlider(key);
            int steps = (int)Math.Round((max - min) / step);
            int index = Math.Clamp((int)Math.Round((value - min) / step), 0, steps);
            // Show real values instead of internal step indices. Clamp uneven endpoints.
            slider.OnSliderTooltip = position => Math.Clamp(min + position * step, min, max).ToString(step < 0.1f ? "0.00" : "0.0") + unit;
            slider.OnSliderRestingText = slider.OnSliderTooltip;
            slider.SetValues(index, 0, steps, 1);
            slider.ShowTextWhenResting = true;
            slider.Enabled = enabled;
        }

        private bool OnMinEvChanged(int value)
        {
            float targetMin = MinEvFloor + (value * 0.05f);
            _config.AgxMinEv = Math.Min(targetMin, _config.AgxMaxEv - 0.1f);
            SaveAndRefresh();
            return true;
        }

        private bool OnMaxEvChanged(int value)
        {
            float targetMax = MaxEvFloor + (value * 0.05f);
            _config.AgxMaxEv = Math.Max(targetMax, _config.AgxMinEv + 0.1f);
            SaveAndRefresh();
            return true;
        }

        private bool OnGreyPointChanged(int value)
        {
            _config.GreyPoint = GreyPointMin + (value * 0.01f);
            SaveAndRefresh();
            return true;
        }

        private bool OnExposureChanged(int value)
        {
            _config.Exposure = ExposureMin + (value * 0.1f);
            SaveAndRefresh();
            return true;
        }

        private bool OnAdaptSpeedChanged(int value)
        {
            _config.AutoEvAdaptationSpeed = SpeedMin + (value * 0.1f);
            SaveAndRefresh();
            return true;
        }

        private bool OnAutoEvMinChanged(int value)
        {
            // Rounded slider steps must not cross the negative/positive endpoint bounds.
            _config.AutoEvMinStops = Math.Clamp(AutoEvMinFloor + (value * 0.1f), AutoEvMinFloor, AutoEvMinCeil);
            SaveAndRefresh();
            return true;
        }

        private bool OnAutoEvMaxChanged(int value)
        {
            _config.AutoEvMaxStops = Math.Clamp(AutoEvMaxFloor + (value * 0.1f), AutoEvMaxFloor, AutoEvMaxCeil);
            SaveAndRefresh();
            return true;
        }

        private void OnTitleBarCloseClicked()
        {
            TryClose();
        }
    }
}
