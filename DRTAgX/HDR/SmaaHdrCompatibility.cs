using System;
using System.Reflection;
using HarmonyLib;
using OpenTK.Graphics.OpenGL4;
using Vintagestory.API.Client;

namespace DRTAgX;

/// <summary>Keep optional VS-SMAA scene colors in HDR before the final AgX transform.</summary>
internal sealed class SmaaHdrCompatibility : IDisposable
{
    private const string HarmonyId = "drtagx.hdr.smaa";
    private static SmaaHdrCompatibility? _current;
    private readonly Harmony _harmony = new(HarmonyId);
    private readonly ICoreClientAPI _api;
    private object? _renderer;
    private bool _warned;

    internal SmaaHdrCompatibility(ICoreClientAPI api)
    {
        _api = api;
        Type? renderer = AccessTools.TypeByName("VSSMAA.SMAARenderer");
        if (renderer == null) return; // Optional mod; no hard assembly dependency.
        try
        {
            MethodInfo? targets = AccessTools.Method(renderer, "EnsureTargets", new[] { typeof(int), typeof(int) });
            MethodInfo? compile = AccessTools.Method(renderer, "Compile");
            ParameterInfo[]? parameters = compile?.GetParameters();
            if (targets == null || parameters?.Length != 3 || parameters[1].ParameterType != typeof(string)
                || parameters[2].ParameterType != typeof(string)) throw new MissingMethodException("SMAA target/shader layout");
            // These names/types are verified against installed VS-SMAA 1.0.0.
            // Reject another layout before Harmony injects private field values.
            foreach (string name in new[] { "width", "height", "edgesFbo", "outTex", "outFbo", "sharpTex", "sharpFbo" })
                if (AccessTools.Field(renderer, name)?.FieldType != typeof(int)) throw new MissingFieldException(name);
            _current = this;
            _harmony.Patch(targets, prefix: new HarmonyMethod(typeof(SmaaHdrCompatibility), nameof(TargetsPrefix)),
                postfix: new HarmonyMethod(typeof(SmaaHdrCompatibility), nameof(TargetsPostfix)));
            _harmony.Patch(compile!, prefix: new HarmonyMethod(typeof(SmaaHdrCompatibility), nameof(CompilePrefix)));
            api.Logger.Notification("[DRT AgX] SMAA HDR color targets and sharpening compatibility installed.");
        }
        catch (Exception ex)
        {
            _harmony.UnpatchAll(HarmonyId);
            if (_current == this) _current = null;
            api.Logger.Warning($"[DRT AgX] SMAA HDR compatibility unavailable: {ex.Message}");
        }
    }

    // The provider calls EnsureTargets on the render thread before its draws.
    // Injected fields avoid reflection/GL queries/allocations on ordinary frames.
    private static void TargetsPrefix(object __instance, int __0, int __1, int ___width, int ___height,
        int ___edgesFbo, out bool __state)
    {
        __state = _current != null && (!ReferenceEquals(_current._renderer, __instance)
            || ___edgesFbo == 0 || ___width != __0 || ___height != __1);
    }

    private static void TargetsPostfix(object __instance, int __0, int __1, int ___outTex, int ___outFbo,
        int ___sharpTex, int ___sharpFbo, bool __state)
    {
        SmaaHdrCompatibility? hook = _current;
        if (!__state || hook == null) return;
        hook._renderer = __instance;
        try
        {
            // Only these two textures contain scene RGB. Edge/weight/LUT control
            // textures keep the provider's normalized formats and stencil setup.
            PromoteColorTarget(___outTex, ___outFbo, __0, __1);
            PromoteColorTarget(___sharpTex, ___sharpFbo, __0, __1);
        }
        catch (Exception ex) { hook.WarnOnce(ex.Message); }
    }

    private static void CompilePrefix(ref string __1, string __2)
    {
        if (__2 != "cas-fs") return;
        if (TryPatchSharpSource(__1, out string patched)) __1 = patched;
        else _current?.WarnOnce("Unrecognized SMAA sharpening source; original source retained.");
    }

    internal static bool TryPatchSharpSource(string source, out string patched)
    {
        patched = source;
        const string contrast = "float amp = clamp(min(mn, 2.0 - mx) * rcpM, 0.0, 1.0);";
        const string reciprocal = "float rcpM = 1.0 / mx;";
        const string weight = "amp = inversesqrt(amp);";
        if (!Single(source, contrast) || !Single(source, reciprocal) || !Single(source, weight)) return false;
        // Derive a bounded contrast weight from local luminance, without an SDR
        // white ceiling. Filtering and its neighborhood bounds stay in HDR RGB.
        patched = source.Replace(reciprocal, "float rcpM = 1.0 / max(mx, 1e-6);", StringComparison.Ordinal)
            .Replace(contrast, "// DRTAgX: relative HDR contrast; black has zero sharpening weight.\n"
                + "    float amp = clamp(max(mn, 0.0) * rcpM, 0.0, 1.0);", StringComparison.Ordinal)
            .Replace(weight, "amp = sqrt(amp);", StringComparison.Ordinal)
            // |w| <= 1/8 keeps the filter denominator >= 1/2, including black
            // and saturated highlights. Preserve the user's normal 0..1 slider.
            .Replace("sharpness * 0.125", "clamp(sharpness, 0.0, 1.0) * 0.125", StringComparison.Ordinal);
        return true;
    }

    internal static void PromoteColorTarget(int texture, int fbo, int width, int height)
    {
        if (texture <= 0 || fbo <= 0 || width <= 0 || height <= 0) return;
        using var state = new TextureMutationState();
        GL.BindTexture(TextureTarget.Texture2D, texture);
        GL.GetTexLevelParameter(TextureTarget.Texture2D, 0, GetTextureParameter.TextureInternalFormat, out int format);
        if (format == (int)PixelInternalFormat.Rgba16f || format == (int)PixelInternalFormat.Rgba32f) return;
        if (format != (int)PixelInternalFormat.Rgba8) throw new InvalidOperationException($"Unexpected SMAA color format 0x{format:X}.");
        // Storage is changed before the provider's full-screen color draws;
        // retain its texture identity, filtering and existing FBO attachment.
        GL.BindBuffer(BufferTarget.PixelUnpackBuffer, 0);
        GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba16f,
            width, height, 0, PixelFormat.Rgba, PixelType.HalfFloat, IntPtr.Zero);
        GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer, fbo);
        FramebufferErrorCode status = GL.CheckFramebufferStatus(FramebufferTarget.DrawFramebuffer);
        if (status == FramebufferErrorCode.FramebufferComplete) return;
        GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba8,
            width, height, 0, PixelFormat.Rgba, PixelType.UnsignedByte, IntPtr.Zero);
        throw new InvalidOperationException($"SMAA HDR framebuffer incomplete ({status}); restored RGBA8.");
    }

    private static bool Single(string source, string anchor)
    {
        int first = source.IndexOf(anchor, StringComparison.Ordinal);
        return first >= 0 && source.IndexOf(anchor, first + anchor.Length, StringComparison.Ordinal) < 0;
    }

    private void WarnOnce(string reason)
    {
        if (_warned) return;
        _warned = true;
        _api.Logger.Warning($"[DRT AgX] SMAA HDR compatibility: {reason}");
    }

    public void Dispose()
    {
        _harmony.UnpatchAll(HarmonyId);
        if (_current == this) _current = null;
        _renderer = null;
    }

    // Preserve every binding touched by storage mutation, including an unpack
    // PBO: IntPtr.Zero must mean empty storage rather than a buffer offset.
    private readonly struct TextureMutationState : IDisposable
    {
        private readonly int _texture, _drawFbo, _unpack;
        public TextureMutationState()
        {
            GL.GetInteger(GetPName.TextureBinding2D, out _texture);
            GL.GetInteger(GetPName.DrawFramebufferBinding, out _drawFbo);
            GL.GetInteger(GetPName.PixelUnpackBufferBinding, out _unpack);
        }
        public void Dispose()
        {
            GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer, _drawFbo);
            GL.BindBuffer(BufferTarget.PixelUnpackBuffer, _unpack);
            GL.BindTexture(TextureTarget.Texture2D, _texture);
        }
    }
}
