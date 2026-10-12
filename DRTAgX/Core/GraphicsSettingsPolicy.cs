using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Config;
using Vintagestory.Client.NoObf;

namespace DRTAgX;

/// <summary>Owns only the graphics controls superseded by DRTAgX.</summary>
internal sealed class GraphicsSettingsPolicy : IDisposable
{
    private const string PatchId = "drtagx.graphics-settings-policy";
    private readonly Harmony _harmony = new(PatchId);
    private readonly ISettingsClass<int> _integers;
    private readonly GuiElementSwitch _hiddenGodRays;
    private static GraphicsSettingsPolicy? _instance;

    internal GraphicsSettingsPolicy(ICoreClientAPI api)
    {
        _integers = api.Settings.Int;
        // Native GUI initialization also writes switch fields directly. Keep an
        // uncomposed lookup placeholder; it is never registered, drawn or interactive.
        _hiddenGodRays = new GuiElementSwitch(api, static _ => { }, ElementBounds.Fixed(0, 0, 30, 30));
        _instance = this;
        try
        {
            // Clamp before the engine sees the write, including preset changes and
            // writes that suppress watchers. This does not add any per-frame work.
            Type settings = _integers.GetType();
            _harmony.Patch(RequiredMethod(settings, "Set", typeof(string), typeof(int), typeof(bool)),
                prefix: Hook(nameof(KeepGodRaysSetting)));
            _harmony.Patch(RequiredMethod(settings, "set_Item", typeof(string), typeof(int)),
                prefix: Hook(nameof(KeepGodRaysSetting)));
            _harmony.Patch(AccessTools.PropertySetter(typeof(ClientSettings), nameof(ClientSettings.GodRayQuality))
                           ?? throw new MissingMethodException(nameof(ClientSettings.GodRayQuality)),
                prefix: Hook(nameof(KeepGodRaysProperty)));
            _harmony.Patch(AccessTools.PropertySetter(typeof(ClientSettings), nameof(ClientSettings.SSAOQuality))
                           ?? throw new MissingMethodException(nameof(ClientSettings.SSAOQuality)),
                prefix: Hook(nameof(KeepSsaoProperty)));
            _harmony.Patch(RequiredMethod(typeof(GuiCompositeSettings), "OnGraphicsOptions", typeof(bool)),
                transpiler: Hook(nameof(NativeGraphicsControls)));
            _harmony.Patch(RequiredMethod(typeof(GuiCompositeSettings), "SetGfxValues"),
                transpiler: Hook(nameof(NativeGraphicsControls)));

            Type? sheyderMenu = AccessTools.TypeByName("SheyderMod.Gui.SheyderMainMenu");
            if (sheyderMenu != null)
            {
                _harmony.Patch(RequiredMethod(sheyderMenu, "ComposeMenu"),
                    prefix: Hook(nameof(RemoveSheyderBloom)));
                _harmony.Patch(RequiredMethod(sheyderMenu, "ComposePage",
                        AccessTools.TypeByName("SheyderMod.Gui.GuiInterface")),
                    prefix: Hook(nameof(BeforeSheyderPage)));
            }

            // Include the initial write in rollback: setting watchers may throw
            // and must not leave this constructor's native hooks installed.
            _integers.Set("godRays", Math.Max(1, _integers.Get("godRays", 1)), true);
            // Native SSAO enables the Primary G-buffer attachments required by
            // Sheyder deferred. Preserve higher quality and allow developer tests.
            if (!ClientSettings.DeveloperMode && _integers.Get("ssaoQuality") < 1)
                _integers.Set("ssaoQuality", 1, true);
        }
        catch
        {
            // A changed native GUI contract must not leave a partly patched menu.
            _harmony.UnpatchAll(PatchId);
            _hiddenGodRays.Dispose();
            _instance = null;
            throw;
        }
    }

    private static MethodInfo RequiredMethod(Type type, string name, params Type[] arguments) =>
        AccessTools.Method(type, name, arguments) ?? throw new MissingMethodException(type.FullName, name);

    private static HarmonyMethod Hook(string name) => new(typeof(GraphicsSettingsPolicy), name);

    private static void KeepGodRaysSetting(object __instance, string __0, ref int __1)
    {
        // SettingsClass<int> may also back unrelated settings stores. Scope the
        // clamp to this client's exact prerequisite keys, preserving other stores.
        if (ReferenceEquals(__instance, _instance?._integers) &&
            (__0 == "godRays" || (__0 == "ssaoQuality" && !ClientSettings.DeveloperMode)))
            __1 = Math.Max(1, __1);
    }

    private static void KeepGodRaysProperty(ref int __0)
    {
        if (_instance != null) __0 = Math.Max(1, __0);
    }

    private static void KeepSsaoProperty(ref int __0)
    {
        if (_instance != null && !ClientSettings.DeveloperMode) __0 = Math.Max(1, __0);
    }

    private static IEnumerable<CodeInstruction> NativeGraphicsControls(IEnumerable<CodeInstruction> instructions)
    {
        // Only these two native graphics methods are transformed. GUI helpers
        // themselves remain unpatched, preserving other mods and our own GUI.
        foreach (CodeInstruction instruction in instructions)
        {
            if (instruction.operand is MethodInfo method &&
                (instruction.opcode == OpCodes.Call || instruction.opcode == OpCodes.Callvirt))
            {
                MethodInfo? replacement = null;
                if (method.DeclaringType == typeof(GuiComposerHelpers))
                {
                    string? name = method.Name switch
                    {
                        "AddSwitch" => nameof(AddNativeSwitch),
                        "GetSwitch" => nameof(GetNativeSwitch),
                        "AddStaticText" => nameof(AddNativeText),
                        "AddHoverText" => nameof(AddNativeHover),
                        _ => null
                    };
                    if (name != null)
                    {
                        ParameterInfo[] parameters = method.GetParameters();
                        var types = new Type[parameters.Length];
                        for (int i = 0; i < types.Length; i++) types[i] = parameters[i].ParameterType;
                        replacement = RequiredMethod(typeof(GraphicsSettingsPolicy), name, types);
                    }
                }

                if (replacement != null)
                {
                    // Keep the original instruction's labels and exception blocks.
                    instruction.opcode = OpCodes.Call;
                    instruction.operand = replacement;
                }
            }

            yield return instruction;
        }
    }

    private static bool IsSheyderBloom(object? panel) =>
        panel?.GetType().FullName == "SheyderMod.Features.Bloom.BloomSettingsPage";

    private static bool IsSheyderDeferred(object? panel) =>
        panel?.GetType().FullName == "SheyderMod.Features.Deferred.DeferredSettingsPage";

    private static bool IsHiddenSheyderPanel(object? panel) =>
        IsSheyderBloom(panel) || (IsSheyderDeferred(panel) && !ClientSettings.DeveloperMode);

    private static string SheyderEffectName(object panel) =>
        (string)AccessTools.PropertyGetter(panel.GetType(), "EffectName")!.Invoke(panel, null)!;

    // Harmony identifies the original receiver by this reserved parameter name.
    // ReSharper disable InconsistentNaming
    private static void RemoveSheyderBloom(object __instance)
    {
        // Filter the display list before Sheyder calculates rows and pagination.
        // Native layout then omits the label, toggle and Advanced button together,
        // without a blank row. Keep the shared config/reset list intact.
        var menuType = __instance.GetType();
        var sorted = (IList)AccessTools.Field(menuType, "_sorted")!.GetValue(__instance)!;
        for (var i = sorted.Count - 1; i >= 0; i--)
            if (IsHiddenSheyderPanel(sorted[i]))
                sorted.RemoveAt(i);

        if (ClientSettings.DeveloperMode)
        {
            // Sheyder builds _sorted only in its constructor. Recover Deferred
            // from the untouched reset registry when DeveloperMode is restored,
            // inserting the same panel in its native alphabetical position.
            var panels = (IList)AccessTools.Field(menuType, "_panels")!.GetValue(__instance)!;
            foreach (var panel in panels)
            {
                if (!IsSheyderDeferred(panel) || sorted.Contains(panel)) continue;
                var name = SheyderEffectName(panel);
                var index = 0;
                while (index < sorted.Count &&
                       StringComparer.OrdinalIgnoreCase.Compare(SheyderEffectName(sorted[index]!), name) <= 0)
                    index++;
                sorted.Insert(index, panel);
            }
        }

        var current = AccessTools.Field(menuType, "_current")!;
        if (IsHiddenSheyderPanel(current.GetValue(__instance))) current.SetValue(__instance, null);
    }

    private static bool BeforeSheyderPage(object __instance, object __0)
    {
        if (!IsHiddenSheyderPanel(__0)) return true;
        // An old Advanced callback or direct page request must not reopen the
        // removed submenu. Route it through the same filtered overview instead.
        AccessTools.Field(__instance.GetType(), "_current")!.SetValue(__instance, null);
        RequiredMethod(__instance.GetType(), "ComposeMenu").Invoke(__instance, null);
        return false;
    }
    // ReSharper restore InconsistentNaming

    private static GuiComposer AddNativeSwitch(GuiComposer composer, Action<bool> onToggle,
        ElementBounds bounds, string key, double size, double padding) =>
        key == "godraySwitch" ? composer : composer.AddSwitch(onToggle, bounds, key, size, padding);

    private static GuiElementSwitch GetNativeSwitch(GuiComposer composer, string key) =>
        key == "godraySwitch" && _instance != null ? _instance._hiddenGodRays : composer.GetSwitch(key);

    private static bool IsGodRaysLabel(string text) => text == Lang.Get("setting-name-godrays");
    private static bool IsGodRaysHover(string text) => text == Lang.Get("setting-hover-godrays");

    private static GuiComposer AddNativeText(GuiComposer composer, string text, CairoFont font,
        ElementBounds bounds, string key) => IsGodRaysLabel(text)
        ? composer
        : composer.AddStaticText(text, font, bounds, key);

    private static GuiComposer AddNativeText(GuiComposer composer, string text, CairoFont font,
        EnumTextOrientation orientation, ElementBounds bounds, string key) => IsGodRaysLabel(text)
        ? composer
        : composer.AddStaticText(text, font, orientation, bounds, key);

    private static GuiComposer AddNativeHover(GuiComposer composer, string text, CairoFont font,
        int width, ElementBounds bounds, string key) => IsGodRaysHover(text)
        ? composer
        : composer.AddHoverText(text, font, width, bounds, key);

    private static GuiComposer AddNativeHover(GuiComposer composer, string text, CairoFont font,
        int width, ElementBounds bounds, TextBackground background, string key) => IsGodRaysHover(text)
        ? composer
        : composer.AddHoverText(text, font, width, bounds, background, key);

    public void Dispose()
    {
        _harmony.UnpatchAll(PatchId);
        _hiddenGodRays.Dispose();
        if (ReferenceEquals(_instance, this)) _instance = null;
    }
}