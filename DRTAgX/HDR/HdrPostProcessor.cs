using System;
using OpenTK.Graphics.OpenGL4;
using Vintagestory.API.Client;
using Vintagestory.API.MathTools;
using Vintagestory.Client.NoObf;

namespace DRTAgX;

/// <summary>
/// Client render-thread post-processor managing the HDR bloom pyramid and auto-exposure adaptation pipeline.
/// Executes immediately before native final composite, performing up to 5-level downsampling, separable Gaussian blurs,
/// weighted luminance metering, and exponential temporal exposure adaptation.
/// </summary>
internal sealed class HdrPostProcessor : IDisposable
{
    private readonly ICoreClientAPI _api;
    private readonly HdrMipResources _resources = new();
    private readonly HdrPassState _savedState = new(false);
    private static readonly string[] LevelNames = { "level0", "level1", "level2", "level3", "level4" };
    private ShaderProgram? _downsample, _blur, _combine, _adapt;
    private int _triangleVao;
    private bool _warned;
    private bool _announced;
    private object? _lastPlayer;
    private bool _bloomActive;
    internal bool Ready { get; private set; }
    internal int BloomTexture => Ready && _bloomActive ? _resources.Bloom : 0;
    internal int ExposureTexture => Ready && _resources.HasExposure ? _resources.Exposure[_resources.ExposureIndex] : 0;
    internal void Deactivate() => Ready = false;

    internal HdrPostProcessor(ICoreClientAPI api)
    {
        _api = api;
        _api.Event.ReloadShader += ReloadShaders;
        ReloadShaders();
    }

    private bool ReloadShaders()
    {
        Ready = false;
        DisposeShaders();
        bool okay = true;
        _downsample = Load("hdr_downsample", ref okay);
        _blur = Load("hdr_blur", ref okay);
        _combine = Load("hdr_combine", ref okay);
        _adapt = Load("hdr_adapt", ref okay);
        return okay;
    }

    private ShaderProgram? Load(string name, ref bool okay)
    {
        ShaderProgram program = (ShaderProgram)_api.Shader.NewShaderProgram();
        program.AssetDomain = "drtagx";
        _api.Shader.RegisterFileShaderProgram(name, program);
        if (program.Compile()) return program;
        _api.Logger.Error($"[DRT AgX] HDR shader {name} failed to compile.");
        program.Dispose();
        okay = false;
        return null;
    }

    internal void Render(AgxConfig config, float deltaTime)
    {
        Ready = false;
        _bloomActive = config.BloomEnabled && config.BloomStrength > 0;
        if (_downsample == null || _blur == null || _combine == null || _adapt == null) return;
        var buffers = _api.Render.FrameBuffers;
        object? player = _api.World?.Player?.Entity;
        if (player == null)
        {
            _lastPlayer = null;
            _resources.HasExposure = false;
            return;
        }
        if (!ReferenceEquals(player, _lastPlayer))
        {
            _lastPlayer = player;
            _resources.HasExposure = false;
        }
        if (buffers == null || buffers.Count == 0 || buffers[0]?.ColorTextureIds?.Length < 1) return;
        FrameBufferRef primary = buffers[0];
        if (primary.Disposed || primary.Width < 2 || primary.Height < 2) return;
        try
        {
            IShaderProgram? previous = _api.Render.CurrentActiveShader;
            _savedState.Capture();
            using var state = _savedState;
            try
            {
                if (!_resources.Ensure(primary.Width, primary.Height)) return;
                if (_triangleVao == 0) _triangleVao = GL.GenVertexArray();
                GL.BindVertexArray(_triangleVao);
                GL.ActiveTexture(TextureUnit.Texture0);
                GL.BindTexture(TextureTarget.Texture2D, primary.ColorTextureIds[0]);
                GL.GetTexLevelParameter(TextureTarget.Texture2D, 0, GetTextureParameter.TextureInternalFormat,
                    out int sceneFormat);
                if (sceneFormat != (int)PixelInternalFormat.Rgba16f)
                    throw new InvalidOperationException($"Primary scene is not RGBA16F (0x{sceneFormat:X}).");
                // Primary color 1 is the native glowParts attachment. A missing
                // attachment leaves the existing unboosted bloom path available.
                int glowTexture = primary.ColorTextureIds.Length > 1 ? primary.ColorTextureIds[1] : 0;
                // Exposure still needs luminance reduction when bloom is disabled.
                // Skip blur/combine work entirely when the player turns off the glow.
                if (_bloomActive || config.AutoExposureEnabled)
                    RenderPyramid(primary.ColorTextureIds[0], glowTexture,
                        primary.Width, primary.Height, config.AutoExposureEnabled);
                if (_bloomActive)
                {
                    BlurPyramid();
                    CombineBloom();
                }
                if (config.AutoExposureEnabled) AdaptExposure(config, deltaTime);
                else _resources.HasExposure = false;
                Ready = true;
                _warned = false;
                if (!_announced)
                {
                    _api.Logger.Notification($"[DRT AgX] HDR mip passes active at {primary.Width}x{primary.Height}; "
                        + $"bloom texture {_resources.Bloom}, exposure texture {_resources.Exposure[_resources.ExposureIndex]}.");
                    _announced = true;
                }
            }
            finally
            {
                // Stop a pass interrupted by an upload failure, then restore the
                // engine's managed shader before the guard restores the exact GL program.
                try { _api.Render.CurrentActiveShader?.Stop(); }
                finally { previous?.Use(); }
            }
        }
        catch (Exception ex)
        {
            Ready = false;
            // The next frame can retry after a resize, reload, or transient GL failure.
            if (!_warned) _api.Logger.Warning($"[DRT AgX] HDR passes failed: {ex.Message}");
            _warned = true;
        }
    }

    private void RenderPyramid(int primaryTexture, int glowTexture,
        int primaryWidth, int primaryHeight, bool meter)
    {
        // Metering reduces its own mip chain from level zero. Deeper RGB levels
        // are consumed only by bloom and are regenerated when bloom is enabled.
        bool performance = FrameQuality.Current.Performance;
        bool separateMeter = meter && (performance || !_bloomActive);
        int levels = _bloomActive ? _resources.ActiveLevels : 0;
        for (int i = 0; i < levels; i++)
        {
            int source = i == 0 ? primaryTexture : _resources.Scene[i - 1];
            int sourceWidth = i == 0 ? primaryWidth : _resources.Width[i - 1];
            int sourceHeight = i == 0 ? primaryHeight : _resources.Height[i - 1];
            _resources.BindTarget(_resources.Scene[i], _resources.Width[i], _resources.Height[i],
                i == 0 && meter && !separateMeter ? _resources.Meter : 0);
            _downsample!.Use();
            _downsample.BindTexture2D("source", source, 0);
            if (i == 0 && glowTexture > 0)
                _downsample.BindTexture2D("glowParts", glowTexture, 1);
            _downsample.Uniform("emissiveFirstLevel", i == 0 && glowTexture > 0 ? 1 : 0);
            _downsample.Uniform("texelSize", 1f / sourceWidth, 1f / sourceHeight);
            _downsample.Uniform("meterFirstLevel", i == 0 && meter && !separateMeter ? 1 : 0);
            _downsample.Uniform("reduction", i == 0 && performance ? 4 : 2);
            _downsample.Uniform("alignedHalf", sourceWidth == _resources.Width[i] * 2 && sourceHeight == _resources.Height[i] * 2 ? 1 : 0);
            GL.DrawArrays(PrimitiveType.Triangles, 0, 3);
            _downsample.Stop();
        }
        if (separateMeter)
        {
            _resources.BindMeterOnly();
            _downsample!.Use();
            _downsample.BindTexture2D("source", primaryTexture, 0);
            if (glowTexture > 0) _downsample.BindTexture2D("glowParts", glowTexture, 1);
            _downsample.Uniform("emissiveFirstLevel", glowTexture > 0 ? 1 : 0);
            _downsample.Uniform("texelSize", 1f / primaryWidth, 1f / primaryHeight);
            _downsample.Uniform("meterFirstLevel", 1);
            _downsample.Uniform("reduction", 2);
            _downsample.Uniform("alignedHalf", primaryWidth == _resources.MeterWidth * 2 && primaryHeight == _resources.MeterHeight * 2 ? 1 : 0);
            GL.DrawArrays(PrimitiveType.Triangles, 0, 3);
            _downsample.Stop();
        }
        if (meter) _resources.GenerateMeterMips();
    }

    private void BlurPyramid()
    {
        _blur!.Use();
        for (int i = 0; i < _resources.ActiveLevels; i++)
        {
            float radius = 1f + (i + (FrameQuality.Current.Performance ? 1 : 0)) * 0.5f;
            // The three-fetch Gaussian identity requires an aligned radius of exactly one texel.
            _blur.Uniform("radiusOne", radius == 1f ? 1 : 0);
            int w = _resources.Width[i], h = _resources.Height[i];
            _resources.BindTarget(_resources.Scratch[i], w, h);
            _blur.BindTexture2D("source", _resources.Scene[i], 0);
            _blur.Uniform("blurStep", radius / w, 0f);
            GL.DrawArrays(PrimitiveType.Triangles, 0, 3);
            _resources.BindTarget(_resources.Scene[i], w, h);
            _blur.BindTexture2D("source", _resources.Scratch[i], 0);
            _blur.Uniform("blurStep", 0f, radius / h);
            GL.DrawArrays(PrimitiveType.Triangles, 0, 3);
        }
        _blur.Stop();
    }

    private void CombineBloom()
    {
        _resources.BindTarget(_resources.Bloom, _resources.Width[0], _resources.Height[0]);
        _combine!.Use();
        for (int i = 0; i < _resources.ActiveLevels; i++)
            _combine.BindTexture2D(LevelNames[i], _resources.Scene[i], i);
        // The fifth sampler retains a valid texture while its Performance weight is zero.
        if (_resources.ActiveLevels == 4) _combine.BindTexture2D(LevelNames[4], _resources.Scene[3], 4);
        _combine.Uniform("performanceMode", FrameQuality.Current.Performance ? 1 : 0);
        GL.DrawArrays(PrimitiveType.Triangles, 0, 3);
        _combine.Stop();
    }

    private void AdaptExposure(AgxConfig config, float deltaTime)
    {
        int next = 1 - _resources.ExposureIndex;
        _resources.BindTarget(_resources.Exposure[next], 1, 1);
        _adapt!.Use();
        _adapt.BindTexture2D("meter", _resources.Meter, 0);
        _adapt.BindTexture2D("previousEv", _resources.Exposure[_resources.ExposureIndex], 1);
        _adapt.Uniform("meterLod", (float)_resources.MeterLod);
        _adapt.Uniform("deltaTime", Math.Clamp(deltaTime, 0f, 0.25f));
        // Runtime safety bounds follow the GUI instead of silently imposing an older ceiling.
        _adapt.Uniform("adaptationSeconds", Math.Clamp(config.AutoEvAdaptationSpeed, AgxGuiDialog.SpeedMin, AgxGuiDialog.SpeedMax));
        float minAutoEv = Math.Clamp(config.AutoEvMinStops, AgxGuiDialog.AutoEvMinFloor, AgxGuiDialog.AutoEvMinCeil);
        float maxAutoEv = Math.Clamp(config.AutoEvMaxStops, AgxGuiDialog.AutoEvMaxFloor, AgxGuiDialog.AutoEvMaxCeil);
        _adapt.Uniform("evBounds", minAutoEv, maxAutoEv);
        _adapt.Uniform("resetExposure", _resources.HasExposure ? 0 : 1);
        GL.DrawArrays(PrimitiveType.Triangles, 0, 3);
        _adapt.Stop();
        _resources.ExposureIndex = next;
        _resources.HasExposure = true;
    }

    private void DisposeShaders()
    {
        _downsample?.Dispose(); _downsample = null;
        _blur?.Dispose(); _blur = null;
        _combine?.Dispose(); _combine = null;
        _adapt?.Dispose(); _adapt = null;
    }

    public void Dispose()
    {
        _api.Event.ReloadShader -= ReloadShaders;
        DisposeShaders();
        _resources.Dispose();
        if (_triangleVao != 0) GL.DeleteVertexArray(_triangleVao);
        _triangleVao = 0;
    }
}
