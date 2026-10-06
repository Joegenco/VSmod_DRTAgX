using System;
using System.Reflection;
using DRTAgX;

/// <summary>Exercise the real config-view adapter, including live edits and allocation-free warm calls.</summary>
internal static class QualityPolicyProbe
{
    private sealed class Effect
    {
        public bool Enabled { get; set; }
        public int Slices { get; set; } = 4;
        public int Steps { get; set; } = 12;
        public int StepCount { get; set; } = 32;
        public int Downsample { get; set; } = 3;
        public float Intensity { get; set; } = .7f;
    }
    private sealed class Loader
    {
        public Effect Gtao { get; set; } = new();
        public Effect VolumetricFog { get; set; } = new();
        public Effect Water { get; set; } = new();
    }
    private sealed class Owner(Func<Loader> get)
    {
        internal readonly Func<Loader> _getConfig = get;
    }

    internal static void Run()
    {
        var config = new AgxConfig();
        Program.Check(!config.PerformanceMode, "Performance mode defaults off");
        FrameQuality.Publish(config);
        Program.Check(FrameQuality.Current.PlacedLights(true) && !FrameQuality.Current.PlacedLights(false), "Normal retains the saved PLS preference");
        Program.Check(FrameQuality.Current.MovingFace(96) == 96 && FrameQuality.Current.MovingLights(10) == 10, "Normal retains shadow preferences");
        config.PerformanceMode = true; FrameQuality.Publish(config);
        Program.Check(!FrameQuality.Current.PlacedLights(true) && !FrameQuality.Current.PlacedLights(false) && config.StaticLightShadows,
            "Performance suppresses PLS without changing the saved enabled preference");
        Program.Check(FrameQuality.Current.MovingFace(96) == 48 && FrameQuality.Current.MovingLights(10) == 2 && !FrameQuality.Current.ContactShadows(true), "Performance caps moving detail and contact shadows");
        Program.Check(FrameQuality.Current.MovingLights(1) == 1 && !FrameQuality.Current.ContactShadows(false), "Caps retain smaller and disabled user preferences");
        var type = typeof(SheyderQualityAdapter).GetNestedType("ConfigView", BindingFlags.NonPublic)!;
        var source = new Loader();
        // Native fog was removed from this adapter when its marching was restored.
        // VolumetricBridgeProbe covers native fog preferences and ownership.
        foreach (string effect in new[] { "Gtao", "Water" })
        {
            var owner = new Owner(() => source);
            var field = typeof(Owner).GetField("_getConfig", BindingFlags.Instance | BindingFlags.NonPublic)!;
            object view = Activator.CreateInstance(type, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null,
                new object[] { owner, field, owner._getConfig, effect }, null)!;
            var wrapped = (Func<Loader>)type.GetField("Wrapped", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(view)!;
            Loader adjusted = wrapped();
            var property = typeof(Loader).GetProperty(effect)!;
            var saved = (Effect)property.GetValue(source)!;
            var capped = (Effect)property.GetValue(adjusted)!;
            Program.Check(!ReferenceEquals(adjusted, source) && !capped.Enabled, effect + " uses a private view and keeps disabled effects disabled");
            Program.Check(saved.Steps == 12 && saved.StepCount == 32 && saved.Downsample == 3, effect + " leaves saved preferences untouched");
            if (effect == "Gtao") Program.Check(capped.Slices == 2 && capped.Steps == 3, "GTAO caps slices and steps");
            if (effect == "Water") Program.Check(capped.Steps == 4 && capped.Downsample == 6, "SSR caps steps and downsampling");
            saved.Intensity = .23f;
            Program.Check(((Effect)property.GetValue(wrapped())!).Intensity == .23f, effect + " sees live scalar edits");
            _ = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 50000; ++i) wrapped();
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 10000; ++i) wrapped();
            long allocation = GC.GetAllocatedBytesForCurrentThread() - before;
            Program.Check(allocation == 0, effect + " warm config allocation bytes=" + allocation);
            config.PerformanceMode = false; FrameQuality.Publish(config);
            Program.Check(ReferenceEquals(source, wrapped()), effect + " restores the original preference object in Normal");
            config.PerformanceMode = true; FrameQuality.Publish(config);
            Program.Check(ReferenceEquals(adjusted, wrapped()), effect + " reuses the warm view after toggling");
        }
        config.PerformanceMode = false; FrameQuality.Publish(config);
        Program.Check(FrameQuality.Current.PlacedLights(config.StaticLightShadows), "Normal restores PLS after the cap");
        int generation = FrameQuality.Generation;
        FrameQuality.Publish(config);
        Program.Check(FrameQuality.Generation == generation, "Unchanged quality does not trigger another resource transition");
        config.StaticLightShadows = false;
        for (int i = 0; i < 12; ++i)
        {
            config.PerformanceMode = (i & 1) == 0; FrameQuality.Publish(config);
            Program.Check(!FrameQuality.Current.PlacedLights(config.StaticLightShadows), "Mode toggles never enable a user-disabled PLS effect");
        }
    }
}
