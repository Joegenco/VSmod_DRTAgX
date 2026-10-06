using System;
using System.Reflection;
using HarmonyLib;
using OpenTK.Graphics.OpenGL4;
using Vintagestory.API.Client;

namespace DRTAgX;

/// <summary>
/// Harmony-based lifecycle hook that intercepts native final composition and post-processing passes.
/// Suppresses redundant native and SheyderMod bloom passes and binds DRTAgX HDR bloom (unit 8)
/// and auto-exposure metering textures (unit 9) to the final compositing shader.
/// </summary>
internal sealed class HdrFinalHook : IDisposable
{
    private const string HarmonyId = "drtagx.hdr.final";
    private static HdrFinalHook? _current;
    private readonly ICoreClientAPI _api;
    private readonly Harmony _harmony = new(HarmonyId);
    private readonly HdrPostProcessor _processor;
    private readonly Func<AgxConfig> _config;
    private FieldInfo? _nativeBloomField;
    private AccessTools.FieldRef<object, bool>? _nativeBloomRef;
    private int _sheyderAttempts;
    private bool _sheyderPatched;
    private bool _nativeChanged, _nativeHookFailed, _announcedFinal;
    private float _deltaTime;
    private int _activeTexture, _bloomBinding, _exposureBinding, _bloomSampler, _exposureSampler;
    private bool _finalBindingsChanged;

    internal HdrFinalHook(ICoreClientAPI api, Func<AgxConfig> config)
    {
        _api = api;
        _config = config;
        _processor = new HdrPostProcessor(api);
        _current = this;
        try
        {
            MethodInfo? final = AccessTools.Method("Vintagestory.Client.NoObf.ClientPlatformWindows:RenderFinalComposition");
            MethodInfo? post = AccessTools.Method("Vintagestory.Client.NoObf.ClientPlatformWindows:RenderPostprocessingEffects");
            if (final == null) throw new MissingMethodException("RenderFinalComposition");
            _harmony.Patch(final, prefix: new HarmonyMethod(typeof(HdrFinalHook), nameof(FinalPrefix)),
                postfix: new HarmonyMethod(typeof(HdrFinalHook), nameof(FinalPostfix)),
                finalizer: new HarmonyMethod(typeof(HdrFinalHook), nameof(FinalPostfix)));
            if (post != null)
            {
                _nativeBloomField = AccessTools.Field(post.DeclaringType, "RenderBloom");
                if (_nativeBloomField?.FieldType == typeof(bool))
                {
                    // Validate once and avoid boxing bools in the normal per-frame path.
                    // Reflection remains a fallback if this runtime cannot create a field ref.
                    try { _nativeBloomRef = AccessTools.FieldRefAccess<object, bool>(_nativeBloomField); }
                    catch { _nativeBloomRef = null; }
                    _harmony.Patch(post, prefix: new HarmonyMethod(typeof(HdrFinalHook), nameof(NativeBloomPrefix)),
                        postfix: new HarmonyMethod(typeof(HdrFinalHook), nameof(NativeBloomPostfix)),
                        finalizer: new HarmonyMethod(typeof(HdrFinalHook), nameof(NativeBloomPostfix)));
                    _api.Logger.Notification("[DRT AgX] Native bloom render hook installed.");
                }
            }
            TryPatchSheyderBloom();
        }
        catch (Exception ex)
        {
            _api.Logger.Error($"[DRT AgX] HDR hook unavailable: {ex.Message}");
        }
    }

    internal void BeforeFrame(float deltaTime)
    {
        _deltaTime = deltaTime;
        if (!_sheyderPatched && _sheyderAttempts < 2) TryPatchSheyderBloom();
    }

    private void TryPatchSheyderBloom()
    {
        _sheyderAttempts++;
        try
        {
            MethodInfo? target = AccessTools.Method("SheyderMod.Features.Bloom.BloomRenderer:RenderBloomAndBindFinal");
            if (target == null) throw new MissingMethodException("SheyderMod BloomRenderer.RenderBloomAndBindFinal");
            _harmony.Patch(target, prefix: new HarmonyMethod(typeof(HdrFinalHook), nameof(SheyderBloomPrefix)));
            _sheyderPatched = true;
            _api.Logger.Notification("[DRT AgX] SheyderMod bloom render hook installed.");
        }
        catch (Exception ex)
        {
            if (_sheyderAttempts == 2)
                _api.Logger.Warning($"[DRT AgX] SheyderMod bloom work could not be suppressed after two attempts; its final composite remains discarded: {ex.Message}");
        }
    }

    [HarmonyPriority(Priority.Last)]
    private static void NativeBloomPrefix(object __instance, ref bool __state)
    {
        HdrFinalHook? hook = _current;
        if (hook?._nativeBloomField == null || !hook._processor.Ready) return;
        try
        {
            if (hook._nativeBloomRef != null)
            {
                ref bool enabled = ref hook._nativeBloomRef(__instance);
                __state = enabled;
                enabled = false;
            }
            else
            {
                __state = (bool)(hook._nativeBloomField.GetValue(__instance) ?? false);
                hook._nativeBloomField.SetValue(__instance, false);
            }
            hook._nativeChanged = true;
        }
        catch (Exception ex)
        {
            if (!hook._nativeHookFailed)
                hook._api.Logger.Warning($"[DRT AgX] Native bloom work could not be suppressed; its composite remains discarded: {ex.Message}");
            hook._nativeHookFailed = true;
        }
    }

    private static void NativeBloomPostfix(object __instance, bool __state)
    {
        HdrFinalHook? hook = _current;
        if (hook?._nativeBloomField == null || !hook._nativeChanged) return;
        // The postfix handles normal frames; its idempotent finalizer also runs
        // after a native exception and leaves that exception unchanged.
        try
        {
            if (hook._nativeBloomRef != null) hook._nativeBloomRef(__instance) = __state;
            else hook._nativeBloomField.SetValue(__instance, __state);
        }
        catch (Exception ex)
        {
            if (!hook._nativeHookFailed)
                hook._api.Logger.Warning($"[DRT AgX] Native bloom flag restoration failed: {ex.Message}");
            hook._nativeHookFailed = true;
        }
        hook._nativeChanged = false;
    }

    private static bool SheyderBloomPrefix() => _current?._processor.Ready != true;

    [HarmonyPriority(Priority.Last)]
    private static void FinalPrefix()
    {
        HdrFinalHook? hook = _current;
        if (hook == null) return;
        try
        {
            IShaderProgram? shader = hook._api.Render.GetEngineShader(EnumShaderProgram.Final);
            if (shader == null || shader.Disposed || shader.LoadError
                || !shader.HasUniform("drtBloomReady") || !shader.HasUniform("drtExposureEnabled"))
            {
                hook._processor.Deactivate();
                return;
            }
            hook._processor.Render(hook._config(), hook._deltaTime);
            hook.BindFinalInputs();
        }
        catch (Exception ex)
        {
            FinalPostfix();
            hook._api.Logger.Warning($"[DRT AgX] HDR final binding failed: {ex.Message}");
        }
    }

    private void BindFinalInputs()
    {
        IShaderProgram? shader = _api.Render.GetEngineShader(EnumShaderProgram.Final);
        if (shader == null || shader.Disposed || shader.LoadError) return;
        if (!shader.HasUniform("drtBloomReady") || !shader.HasUniform("drtExposureEnabled")) return;
        GL.GetInteger(GetPName.ActiveTexture, out _activeTexture);
        GL.ActiveTexture(TextureUnit.Texture8);
        GL.GetInteger(GetPName.TextureBinding2D, out _bloomBinding);
        GL.GetInteger(GetPName.SamplerBinding, out _bloomSampler);
        GL.ActiveTexture(TextureUnit.Texture9);
        GL.GetInteger(GetPName.TextureBinding2D, out _exposureBinding);
        GL.GetInteger(GetPName.SamplerBinding, out _exposureSampler);
        GL.ActiveTexture((TextureUnit)_activeTexture);
        _finalBindingsChanged = true;
        IShaderProgram? active = _api.Render.CurrentActiveShader;
        int previousProgram = GL.GetInteger(GetPName.CurrentProgram);
        try
        {
            shader.Use();
            shader.Uniform("drtBloomReady", _processor.Ready ? 1 : 0);
            // Zero strength bypasses sampling even when a previous bloom texture remains bound.
            AgxConfig config = _config();
            if (shader.HasUniform("drtBloomStrength"))
                shader.Uniform("drtBloomStrength", config.BloomEnabled ? Math.Clamp(config.BloomStrength, 0f, 2f) : 0f);
            shader.Uniform("drtExposureEnabled", _processor.ExposureTexture != 0 ? 1 : 0);
            if (_processor.BloomTexture != 0)
            {
                GL.BindSampler(8, 0);
                shader.BindTexture2D("drtBloomTex", _processor.BloomTexture, 8);
            }
            if (_processor.ExposureTexture != 0)
            {
                GL.BindSampler(9, 0);
                shader.BindTexture2D("drtExposureTex", _processor.ExposureTexture, 9);
            }
        }
        finally
        {
            // Textures 8/9 stay borrowed until the native final draw's postfix.
            // Only shader state is restored here; FinalPrefix restores textures on failure.
            try
            {
                try { shader.Stop(); }
                finally { active?.Use(); }
            }
            finally { GL.UseProgram(previousProgram); }
        }
        if (_processor.Ready && !_announcedFinal)
        {
            _api.Logger.Notification("[DRT AgX] HDR bloom and exposure bound to final shader.");
            _announcedFinal = true;
        }
    }

    private static void FinalPostfix()
    {
        HdrFinalHook? hook = _current;
        if (hook == null || !hook._finalBindingsChanged) return;
        // Also installed as a void Harmony finalizer: release borrowed units
        // when the native draw throws, without suppressing its exception.
        GL.ActiveTexture(TextureUnit.Texture8);
        GL.BindTexture(TextureTarget.Texture2D, hook._bloomBinding);
        GL.BindSampler(8, hook._bloomSampler);
        GL.ActiveTexture(TextureUnit.Texture9);
        GL.BindTexture(TextureTarget.Texture2D, hook._exposureBinding);
        GL.BindSampler(9, hook._exposureSampler);
        GL.ActiveTexture((TextureUnit)hook._activeTexture);
        hook._finalBindingsChanged = false;
    }

    public void Dispose()
    {
        // Release any pending native-draw bindings before deleting owned textures.
        // Disposal and draw callbacks share the render thread and current GL context.
        if (ReferenceEquals(_current, this)) FinalPostfix();
        _harmony.UnpatchAll(HarmonyId);
        _processor.Dispose();
        if (ReferenceEquals(_current, this)) _current = null;
    }
}
