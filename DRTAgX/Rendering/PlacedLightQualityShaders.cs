using System;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.Client.NoObf;

namespace DRTAgX;

/// <summary>Native shader lifecycle specialization for the measured PLS outlier.</summary>
internal sealed class PlacedLightQualityShaders : IDisposable
{
    private const string PatchId = "drtagx.quality.placed.shaders";
    private const string Define = "#define DRT_PERFORMANCE_NO_PLS 1\n";
    private static PlacedLightQualityShaders? _instance;
    private readonly Harmony _harmony = new(PatchId);
    private readonly ICoreClientAPI _api;
    private bool _performance, _failed, _synchronized;
    internal static bool QualityReload { get; private set; }
    internal bool CasterChangedThisFrame { get; private set; }
    private readonly record struct CasterSource(string Vertex, string Fragment, string VertexPrefix, string FragmentPrefix);

    private CasterSource? CaptureCaster()
    {
        try
        {
            var program = _api.Render.GetEngineShader(EnumShaderProgram.Chunkshadowmap);
            if (program == null || program.Disposed || program.LoadError ||
                program.VertexShader?.Code == null || program.FragmentShader?.Code == null) return null;
            // Compare actual expanded shader text and all other native options.
            // Concurrent shader edits must invalidate depth even during a mode toggle.
            return new(program.VertexShader.Code, program.FragmentShader.Code,
                (program.VertexShader.PrefixCode ?? string.Empty).Replace(Define, string.Empty, StringComparison.Ordinal),
                (program.FragmentShader.PrefixCode ?? string.Empty).Replace(Define, string.Empty, StringComparison.Ordinal));
        }
        catch { return null; } // An unknown source contract cannot retain old depth.
    }

    internal PlacedLightQualityShaders(ICoreClientAPI api)
    {
        _api = api;
        // Captured Sheyder uses this exact LoadShader/PrefixCode boundary.
        // A missing optional hook retains runtime caps and logs once.
        try
        {
            var load = AccessTools.Method(typeof(ShaderRegistry), "LoadShader",
                [typeof(ShaderProgram), typeof(EnumShaderType)]);
            if (load == null) throw new MissingMethodException("ShaderRegistry.LoadShader");
            _harmony.Patch(load, postfix: new HarmonyMethod(typeof(PlacedLightQualityShaders), nameof(AfterLoad)));
            _instance = this;
        }
        catch (Exception ex) { _failed = true; api.Logger.Warning("[DRTAgX] PLS shader quality hook unavailable: " + ex.Message); }
    }

    private static void AfterLoad(ShaderProgram program, EnumShaderType shaderType)
    {
        if (_instance?._performance != true || program == null) return;
        IShader? shader = shaderType == EnumShaderType.FragmentShader ? program.FragmentShader :
            shaderType == EnumShaderType.VertexShader ? program.VertexShader : null;
        if (shader != null && !(shader.PrefixCode ?? string.Empty).Contains(Define, StringComparison.Ordinal))
            shader.PrefixCode += Define;
    }

    internal void BeforeFrame()
    {
        // Strict render-thread Before boundary, once per changed mode. The
        // native registry owns recompilation and all reload notifications.
        bool next = FrameQuality.Current.Performance;
        CasterChangedThisFrame = false;
        if (!_synchronized)
        {
            _synchronized = true;
            try
            {
                // Native programs can survive a world/mod-system lifetime.
                // Synchronize once with what is actually compiled, including
                // repairing mixed stages after a partial prior reload.
                var opaque = _api.Render.GetEngineShader(EnumShaderProgram.Chunkopaque);
                bool vertex = opaque?.VertexShader?.PrefixCode?.Contains(Define, StringComparison.Ordinal) == true;
                bool fragment = opaque?.FragmentShader?.PrefixCode?.Contains(Define, StringComparison.Ordinal) == true;
                _performance = vertex == fragment ? vertex : !next;
            }
            catch { /* Missing programs load with the ordinary native prefix. */ }
        }
        if (_failed || next == _performance) return;
        var caster = CaptureCaster();
        _performance = next;
        QualityReload = true;
        try
        {
            if (!_api.Shader.ReloadShaders()) throw new InvalidOperationException("Native reload reported a shader failure");
        }
        catch (Exception ex)
        {
            _failed = true;
            _performance = false;
            // Rebuild the regular native programs if a specialized variant
            // fails. Runtime caps continue to provide native voxel lighting.
            try { _api.Shader.ReloadShaders(); } catch { /* Native failure fallback remains authoritative. */ }
            _api.Logger.Warning("[DRTAgX] PLS specialization failed; retaining runtime quality caps: " + ex.Message);
        }
        finally
        {
            CasterChangedThisFrame = caster == null || caster != CaptureCaster();
            QualityReload = false;
        }
    }

    public void Dispose()
    {
        if (ReferenceEquals(_instance, this)) _instance = null;
        _harmony.UnpatchAll(PatchId);
    }
}
