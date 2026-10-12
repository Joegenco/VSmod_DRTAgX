using System;
using System.Linq.Expressions;
using System.Reflection;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.Client.NoObf;

namespace DRTAgX;

/// <summary>Keeps the supported deferred path enabled outside native DeveloperMode.</summary>
internal sealed class SheyderDeferredPolicy : IDisposable
{
    private const string PatchId = "drtagx.sheyder-deferred-policy";
    private readonly Harmony _harmony = new(PatchId);
    private readonly ISettingsClass<bool>? _booleans;
    private readonly ISettingsClass<int>? _integers;
    private readonly Func<object>? _currentConfig;
    private readonly Func<object, bool>? _enable;
    private static SheyderDeferredPolicy? _active;

    internal SheyderDeferredPolicy(ICoreClientAPI api)
    {
        object? system = api.ModLoader.GetModSystem("SheyderMod.SheyderModSystem");
        if (system == null) return;
        try
        {
            var systemType = system.GetType();
            var configField = AccessTools.Field(systemType, "_config")
                              ?? throw new MissingFieldException(systemType.FullName, "_config");
            var deferred = configField.FieldType.GetProperty("Deferred")
                           ?? throw new MissingMemberException(configField.FieldType.FullName, "Deferred");

            // Resolve private dependency contracts once. The frame prefix uses
            // compiled delegates, without reflection, scratch arrays or allocation.
            _currentConfig = Expression.Lambda<Func<object>>(
                    Expression.Convert(Expression.Field(Expression.Constant(system), configField), typeof(object)))
                .Compile();
            var config = Expression.Parameter(typeof(object));
            var section = Expression.Variable(deferred.PropertyType);
            var enabled = Expression.Property(section, "Enabled");
            _enable = Expression.Lambda<Func<object, bool>>(Expression.Block(new[] { section },
                    Expression.Assign(section,
                        Expression.Property(Expression.Convert(config, configField.FieldType), deferred)),
                    Expression.Condition(
                        Expression.OrElse(Expression.Equal(section, Expression.Constant(null, section.Type)), enabled),
                        Expression.Constant(false),
                        Expression.Block(Expression.Assign(enabled, Expression.Constant(true)),
                            Expression.Constant(true)))), config)
                .Compile();
            _booleans = api.Settings.Bool;
            _integers = api.Settings.Int;
            _active = this;

            var renderer = systemType.Assembly.GetType("SheyderMod.Features.Deferred.DeferredRenderer", true)!;
            // Patch substantive native boundaries instead of inline-prone bool
            // accessors. GUI changes/reset/reload invalidate after assigning _config.
            _harmony.Patch(RequiredMethod(systemType, "InvalidateRendererStatics"),
                prefix: Hook(nameof(BeforeConfigUse)));
            _harmony.Patch(RequiredMethod(renderer, "UpdatePerFrame", typeof(float)),
                prefix: Hook(nameof(BeforeConfigUse)));
            // DeveloperMode revocation must repair the live object immediately,
            // including native settings writes that suppress watcher notifications.
            _harmony.Patch(RequiredMethod(_booleans.GetType(), "Set", typeof(string), typeof(bool), typeof(bool)),
                postfix: Hook(nameof(AfterBooleanSetting)));
            _harmony.Patch(RequiredMethod(_booleans.GetType(), "set_Item", typeof(string), typeof(bool)),
                postfix: Hook(nameof(AfterBooleanSetting)));
            EnforceConfig(_currentConfig());
            if (!ClientSettings.DeveloperMode) EnsureSsaoPrerequisite();
        }
        catch
        {
            // Startup failures roll back every owned patch before propagating.
            Dispose();
            throw;
        }
    }

    private static MethodInfo RequiredMethod(Type type, string name, params Type[] arguments) =>
        AccessTools.Method(type, name, arguments) ?? throw new MissingMethodException(type.FullName, name);

    private static HarmonyMethod Hook(string name) => new(typeof(SheyderDeferredPolicy), name);

    internal static bool EnforceConfig(object config)
    {
        // Normalize owns missing sections. Only Enabled is corrected here;
        // PerformanceMode, Debug and every other saved preference remain native.
        var owner = _active;
        return owner != null && !ClientSettings.DeveloperMode && owner._enable!(config);
    }

    private static void BeforeConfigUse()
    {
        // Native UpdatePerFrame executes on the render thread. Mutation stays
        // on this scalar config flag; the original renderer owns all GL work.
        var owner = _active;
        if (owner != null && !ClientSettings.DeveloperMode) owner._enable!(owner._currentConfig!());
    }

    private static void AfterBooleanSetting(object __instance, string __0, bool __1)
    {
        var owner = _active;
        if (owner != null && ReferenceEquals(__instance, owner._booleans) && __0 == "developerMode" && !__1)
        {
            owner._enable!(owner._currentConfig!());
            owner.EnsureSsaoPrerequisite();
        }
    }

    private void EnsureSsaoPrerequisite()
    {
        // SSAO=0 removes native deferred attachments. Recreate the supported
        // minimum on mode revocation; leave higher levels and GTAO preferences.
        if (_integers!.Get("ssaoQuality") < 1) _integers.Set("ssaoQuality", 1, true);
    }

    public void Dispose()
    {
        _harmony.UnpatchAll(PatchId);
        if (ReferenceEquals(_active, this)) _active = null;
    }
}