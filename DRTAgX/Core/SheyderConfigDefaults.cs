using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using HarmonyLib;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace DRTAgX;

/// <summary>Versioned Sheyder defaults, including its GUI resets and typed config saves.</summary>
internal sealed class SheyderConfigDefaults : IDisposable
{
    private const string Filename = "sheydermod.json";
    private const string ResourceName = "DRTAgX.Defaults.sheydermod.json";
    private const string VersionKey = "ConfigVersion";
    private readonly Harmony _harmony = new("drtagx.sheyder-defaults");
    private readonly Dictionary<Type, string> _constructorDefaults = new();
    private readonly ICoreClientAPI _api;
    private readonly Type? _configType;
    private readonly MethodInfo? _normalize;
    private readonly JObject _defaults;
    private readonly string _version;
    private static SheyderConfigDefaults? _active;
    [ThreadStatic] private static bool _populatingDefaults;

    internal SheyderConfigDefaults(ICoreClientAPI api)
    {
        _api = api;
        object? system = api.ModLoader.GetModSystem("SheyderMod.SheyderModSystem");
        using Stream resource = typeof(SheyderConfigDefaults).Assembly.GetManifestResourceStream(ResourceName)
                                ?? throw new InvalidOperationException("Missing shipped Sheyder defaults.");
        using var reader = new StreamReader(resource);
        _defaults = JObject.Parse(reader.ReadToEnd());
        _version = ConfigDefaultsVersion.Current + "|sheydermod:" + api.ModLoader.GetMod("sheydermod")?.Info.Version;
        if (system == null) return;

        // Captured Sheyder 1.1.3 owns both config types and GUI panels. Patch only
        // its verified load/save/constructor boundaries; nothing runs per frame.
        Type systemType = system.GetType();
        _configType = systemType.Assembly.GetType("SheyderMod.Core.ConfigLoader", true)!;
        Type storeType = systemType.Assembly.GetType("SheyderMod.Core.ModConfig", true)!;
        _normalize = AccessTools.Method(storeType, "Normalize", new[] { _configType });
        MethodInfo load = AccessTools.Method(storeType, "Load", new[] { typeof(ICoreClientAPI) })
                          ?? throw new MissingMethodException(storeType.FullName, "Load");
        MethodInfo save = AccessTools.Method(systemType, "SaveConfig", new[] { typeof(ICoreClientAPI) })
                          ?? throw new MissingMethodException(systemType.FullName, "SaveConfig");
        _active = this;
        try
        {
            InstallConstructorDefaults();
            _harmony.Patch(load, prefix: new HarmonyMethod(typeof(SheyderConfigDefaults), nameof(BeforeLoad)));
            _harmony.Patch(save, prefix: new HarmonyMethod(typeof(SheyderConfigDefaults), nameof(BeforeSave)));

            // DRT starts after Sheyder: a reset must reach its already-built GUI,
            // renderer closures and block-light gates, not just the JSON file.
            object config = Load(out bool reset);
            if (reset)
            {
                object current = AccessTools.Field(systemType, "_config")!.GetValue(system)!;
                foreach (PropertyInfo property in _configType.GetProperties())
                    if (property.CanRead && property.CanWrite)
                        property.SetValue(current, property.GetValue(config));
                AccessTools.Method(systemType, "InvalidateRendererStatics")!.Invoke(system, null);
            }
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    private void InstallConstructorDefaults()
    {
        // Cover the aggregate loader too: native child constructors may already
        // have been inlined when Sheyder created its initial config before DRT.
        Add(_configType!, _defaults);
        foreach (JProperty section in _defaults.Properties())
        {
            PropertyInfo property = _configType!.GetProperty(section.Name)
                                    ?? throw new MissingMemberException(_configType.FullName, section.Name);
            Add(property.PropertyType, section.Value);
            if (section.Name == "Water" && section.Value["Cubemap"] is JObject cubemap)
                Add(property.PropertyType.GetProperty("Cubemap")!.PropertyType, cubemap);
        }

        void Add(Type type, JToken defaults)
        {
            _constructorDefaults.Add(type, defaults.ToString(Formatting.None));
            ConstructorInfo constructor = AccessTools.Constructor(type, Type.EmptyTypes)
                                          ?? throw new MissingMethodException(type.FullName, ".ctor");
            // GUI reset buttons, slider default values and Normalize all create
            // these same objects, so their defaults must agree with new files.
            _harmony.Patch(constructor,
                postfix: new HarmonyMethod(typeof(SheyderConfigDefaults), nameof(AfterConstructor)));
        }
    }

    private static void AfterConstructor(object __instance)
    {
        if (_populatingDefaults || _active == null ||
            !_active._constructorDefaults.TryGetValue(__instance.GetType(), out var json)) return;
        // Populating a nested value may construct another patched config type.
        // Its values come from this same JSON; avoid recursively reapplying it.
        _populatingDefaults = true;
        try
        {
            JsonConvert.PopulateObject(json, __instance);
        }
        finally
        {
            _populatingDefaults = false;
        }
    }

    private object Load(out bool changed)
    {
        JObject? saved;
        try
        {
            saved = _api.LoadModConfig<JObject>(Filename);
        }
        catch (Exception ex)
        {
            _api.Logger.Warning("[DRTAgX] Invalid Sheyder config will use shipped defaults: " + ex.Message);
            saved = null;
        }

        var reset = saved == null || saved[VersionKey]?.Type != JTokenType.String
                                  || !string.Equals((string?)saved[VersionKey], _version, StringComparison.Ordinal);
        JObject selected = reset ? (JObject)_defaults.DeepClone() : saved!;
        changed = reset || ConfigNumericSafety.Repair(selected, _defaults);
        object config;
        try
        {
            config = selected.ToObject(_configType!)
                     ?? throw new JsonSerializationException("Sheyder configuration is empty.");
        }
        catch (JsonException ex) when (!reset)
        {
            // Keep native Sheyder's recovery from malformed typed settings. A
            // valid stamp certifies the defaults version, not the JSON payload.
            _api.Logger.Warning("[DRTAgX] Invalid Sheyder values will use shipped defaults: " + ex.Message);
            config = _defaults.ToObject(_configType!)!;
            changed = reset = true;
        }

        config = _normalize?.Invoke(null, new[] { config }) ?? config;
        // Normal gameplay owns the deferred path, including matching-version
        // files last saved while the developer-only diagnostic toggle was off.
        changed |= SheyderDeferredPolicy.EnforceConfig(config);
        if (changed)
        {
            Store(config);
            _api.Logger.Notification(reset
                ? "[DRTAgX] Reset Sheyder settings to defaults for " + _version + "."
                : "[DRTAgX] Repaired invalid Sheyder configuration values.");
        }

        return config;
    }

    private void Store(object config)
    {
        SheyderDeferredPolicy.EnforceConfig(config);
        // Sheyder's typed loader does not declare ConfigVersion. Serialize its
        // actual live settings first, then add the stamp on every native save.
        var saved = JObject.Parse(JsonUtil.ToString(config));
        saved[VersionKey] = _version;
        _api.StoreModConfig(saved, Filename);
    }

    private static bool BeforeLoad(ref object __result)
    {
        if (_active == null) return true;
        __result = _active.Load(out _);
        return false;
    }

    private static bool BeforeSave(object __instance)
    {
        if (_active == null) return true;
        try
        {
            object config = AccessTools.Field(__instance.GetType(), "_config")!.GetValue(__instance)!;
            _active.Store(config);
            _active._api.ShowChatMessage("[SheyderMod] Settings Saved.");
        }
        catch (Exception ex)
        {
            _active._api.Logger.Error("[DRTAgX] Failed to save Sheyder settings: " + ex.Message);
        }

        return false;
    }

    public void Dispose()
    {
        _harmony.UnpatchAll(_harmony.Id);
        if (ReferenceEquals(_active, this)) _active = null;
    }
}