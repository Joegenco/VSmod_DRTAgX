using System;
using System.Reflection;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.Client.NoObf;

namespace DRTAgX;

// Domain folders share the root namespace; Harmony requires these argument names.
// ReSharper disable CheckNamespace, InconsistentNaming
/// <summary>Optional RainCurtains 1.2.2 HDR/fog adaptation; the provider retains its draw.</summary>
internal sealed class RainCurtainsCompatibility : IDisposable
{
    private const string PatchId = "drtagx.atmosphere.raincurtains";
    private static RainCurtainsCompatibility? _current;
    private readonly Harmony? _harmony;
    private readonly ICoreClientAPI _api;
    private readonly string _helper = string.Empty;
    private bool _fallback, _warned;
    private object? _providerRenderer;
    private FieldInfo? _providerProgram;
    private Func<bool>? _reloadProvider;

    private readonly record struct Sources(string Vertex, string Fragment);

    internal RainCurtainsCompatibility(ICoreClientAPI api)
    {
        _api = api;
        if (!api.ModLoader.IsModEnabled("raincurtains")) return;
        object? renderer = null;
        FieldInfo? field = null;
        IShaderProgram? previous = null;
        bool adopted = false;
        try
        {
            // These optional-mod names/fields are verified against installed 1.2.2.
            // No game-private members or hard RainCurtains assembly reference are used.
            object? system = api.ModLoader.GetModSystem("RainCurtains.RainCurtainsModSystem");
            renderer = system == null ? null : AccessTools.Field(system.GetType(), "renderer")?.GetValue(system);
            if (renderer?.GetType().FullName != "RainCurtains.RainCurtainRenderer")
                throw new MissingMemberException("RainCurtains renderer");
            var load = AccessTools.Method(renderer.GetType(), "LoadShader", Type.EmptyTypes);
            field = AccessTools.Field(renderer.GetType(), "prog");
            var compile = AccessTools.Method(typeof(ShaderProgram), nameof(IShaderProgram.Compile), Type.EmptyTypes);
            if (load?.ReturnType != typeof(bool) || field?.FieldType != typeof(IShaderProgram) ||
                compile?.ReturnType != typeof(bool)) throw new MissingMemberException("RainCurtains shader lifecycle");

            // Memory shaders have no native file-include expansion. Inline the same
            // maintained helpers; UBO/LUT binding remains with AtmosphereProgramBindings.
            string Read(string path) =>
                api.Assets.Get(new AssetLocation("drtagx", "shaders/atmosphere/" + path)).ToText();

            _helper = Read("drtagx_atmosphere_sampling.fsh").Replace("#include drtagx_fog_transport.ash",
                          Read("drtagx_fog_transport.ash"), StringComparison.Ordinal) + "\n" +
                      Read("drtagx_raincurtains_lighting.fsh");
            _harmony = new Harmony(PatchId);
            _current = this;
            _harmony.Patch(compile, prefix: new HarmonyMethod(typeof(RainCurtainsCompatibility), nameof(BeforeCompile)),
                postfix: new HarmonyMethod(typeof(RainCurtainsCompatibility), nameof(AfterCompile)));
            // RainCurtains starts before DRTAgX. Rebuild only its existing owner,
            // once on the render-thread startup boundary, rather than reload all shaders.
            _providerRenderer = renderer;
            _providerProgram = field;
            _reloadProvider = load.CreateDelegate<Func<bool>>(renderer);
            previous = field.GetValue(renderer) as IShaderProgram;
            bool loaded = _reloadProvider();
            var replacement = field.GetValue(renderer) as IShaderProgram;
            adopted = loaded && replacement is { Disposed: false };
            if (!adopted && previous is { Disposed: false }) field.SetValue(renderer, previous);
            if (adopted && previous != null && !ReferenceEquals(previous, replacement)) previous.Dispose();
        }
        catch (Exception ex)
        {
            // A provider exception after clearing its field must not hide the
            // previously working rain shader during this initialization attempt.
            if (!adopted && previous is { Disposed: false } && renderer != null) field?.SetValue(renderer, previous);
            if (!adopted)
            {
                _reloadProvider = null;
                _providerRenderer = null;
                _providerProgram = null;
            }

            _harmony?.UnpatchAll(PatchId);
            if (_current == this) _current = null;
            Warn("Compatibility unavailable; provider retained: " + ex.Message);
        }
    }

    // Native shader compilation runs on the render thread; ordinary draws allocate
    // nothing and acquire only the existing shared atmosphere bindings on Use/Stop.
    private static void BeforeCompile(ShaderProgram __instance, out Sources? __state)
    {
        __state = null;
        var hook = _current;
        if (hook == null || hook._fallback || __instance.PassName != "raincurtains") return;
        try
        {
            string vertex = __instance.VertexShader.Code, fragment = __instance.FragmentShader.Code;
            if (vertex.Contains(RainCurtainsShaderPatch.Marker, StringComparison.Ordinal) &&
                fragment.Contains(RainCurtainsShaderPatch.Marker, StringComparison.Ordinal)) return;
            if (!RainCurtainsShaderPatch.TryPatchSources(vertex, fragment, hook._helper, out string patchedVertex,
                    out string patchedFragment))
            {
                hook.Warn("Unrecognized shader source; provider fallback retained.");
                return;
            }

            // Compile/link a temporary candidate without binding it. A bad helper
            // or another provider ABI is rejected before its live source is changed.
            if (!RainCurtainsShaderPatch.Validate(patchedVertex, patchedFragment, out string error))
            {
                hook.Warn("Candidate rejected; provider fallback retained: " + error);
                return;
            }

            __state = new Sources(vertex, fragment);
            __instance.VertexShader.Code = patchedVertex;
            __instance.FragmentShader.Code = patchedFragment;
        }
        catch (Exception ex)
        {
            hook.Warn("Source adaptation skipped: " + ex.Message);
        }
    }

    private static void AfterCompile(ShaderProgram __instance, Sources? __state, ref bool __result)
    {
        var hook = _current;
        if (__state == null || hook == null) return;
        if (__result)
        {
            hook._api.Logger.Notification(
                "[DRT AgX] RainCurtains shared fog/HDR installed; cloud-map lighting retained.");
            return;
        }

        // An additional native/provider compiler transform can still fail after
        // candidate validation. Restore the original source and retry exactly once.
        __instance.VertexShader.Code = __state.Value.Vertex;
        __instance.FragmentShader.Code = __state.Value.Fragment;
        hook._fallback = true;
        try
        {
            __result = __instance.Compile();
        }
        catch (Exception ex)
        {
            __result = false;
            hook.Warn("Original shader retry failed: " + ex.Message);
        }
        finally
        {
            hook._fallback = false;
        }

        hook.Warn("Native compile rejected adaptation; original RainCurtains shader restored.");
    }


    private void Warn(string reason)
    {
        if (_warned) return;
        _warned = true;
        _api.Logger.Warning("[DRT AgX] RainCurtains: " + reason);
    }

    public void Dispose()
    {
        _harmony?.UnpatchAll(PatchId);
        if (_current == this) _current = null;
        var renderer = _providerRenderer;
        var field = _providerProgram;
        var reload = _reloadProvider;
        _providerRenderer = null;
        _providerProgram = null;
        _reloadProvider = null;
        if (renderer == null || field == null || reload == null) return;
        var previous = field.GetValue(renderer) as IShaderProgram;
        // Restore only our live program before the atmosphere owner frees UBO/LUTs.
        // A disposed provider or a later third-party replacement remains untouched.
        if (previous is not { Disposed: false } || !Owned(previous)) return;
        try
        {
            bool restored = reload();
            var replacement = field.GetValue(renderer) as IShaderProgram;
            if (!restored || replacement is not { Disposed: false } || Owned(replacement))
                throw new InvalidOperationException("Original provider shader was not restored");
            if (!ReferenceEquals(previous, replacement)) previous.Dispose();
        }
        catch (Exception ex)
        {
            if (!previous.Disposed) field.SetValue(renderer, previous);
            Warn("Provider restoration failed during disposal: " + ex.Message);
        }
    }

    private static bool Owned(IShaderProgram program) =>
        program.VertexShader?.Code?.Contains(RainCurtainsShaderPatch.Marker, StringComparison.Ordinal) == true ||
        program.FragmentShader?.Code?.Contains(RainCurtainsShaderPatch.Marker, StringComparison.Ordinal) == true;
}