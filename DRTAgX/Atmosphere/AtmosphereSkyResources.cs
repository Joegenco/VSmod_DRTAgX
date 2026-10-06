using System;
using OpenTK.Graphics.OpenGL4;
using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace DRTAgX;

/// <summary>Owns cached transport and double-buffered sky LUTs, including reload/disposal.</summary>
internal sealed class AtmosphereSkyResources : IDisposable
{
    private readonly ICoreClientAPI _api;
    private int _transProgram, _multipleProgram, _skyProgram, _trans, _multiple;
    private int _sunLocation, _moonLocation, _heightLocation;
    private float _elapsed = 1f, _blendTime;
    private readonly float[] _lastSun = new float[4], _lastMoon = new float[4];
    private readonly float[] _profile = [1f, 1f, 1f, 0.35f];
    private int _transProfile, _multipleProfile, _skyProfile;
    private float _lastAltitude = float.NaN;
    internal int Previous { get; private set; }
    internal int Current { get; private set; }
    internal float Blend => Math.Clamp(_blendTime / 0.1f, 0f, 1f);
    internal float Normalization { get; private set; } = 1f;
    internal bool Ready { get; private set; }

    internal AtmosphereSkyResources(ICoreClientAPI api) => _api = api;

    internal void Initialize()
    {
        Dispose();
        _transProgram = AtmosphereCompute.Load(_api, "drtagx_atmosphere_transmittance");
        _multipleProgram = AtmosphereCompute.Load(_api, "drtagx_atmosphere_multiscatter");
        _skyProgram = AtmosphereCompute.Load(_api, "drtagx_atmosphere_skyview");
        _transProfile = GL.GetUniformLocation(_transProgram, "drtScatteringProfile");
        _multipleProfile = GL.GetUniformLocation(_multipleProgram, "drtScatteringProfile");
        _skyProfile = GL.GetUniformLocation(_skyProgram, "drtScatteringProfile");
        _profile[0] = _profile[1] = _profile[2] = 1f; _profile[3] = 0.35f;
        _trans = AtmosphereCompute.Texture(256, 64);
        _multiple = AtmosphereCompute.Texture(32, 32);
        Previous = AtmosphereCompute.Texture(192, 108);
        Current = AtmosphereCompute.Texture(192, 108);
        foreach (int sky in new[] { Previous, Current })
        {
            GL.BindTexture(TextureTarget.Texture2D, sky);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.Repeat);
        }
        GL.UseProgram(_multipleProgram);
        GL.Uniform1(GL.GetUniformLocation(_multipleProgram, "transmittanceTable"), 0);
        GL.UseProgram(_skyProgram);
        GL.Uniform1(GL.GetUniformLocation(_skyProgram, "transmittanceTable"), 0);
        GL.Uniform1(GL.GetUniformLocation(_skyProgram, "multiscatterTable"), 1);
        _sunLocation = GL.GetUniformLocation(_skyProgram, "sunDirection");
        _moonLocation = GL.GetUniformLocation(_skyProgram, "moonDirection");
        _heightLocation = GL.GetUniformLocation(_skyProgram, "cameraAltitude");
        RebuildTransport();
        NormalizeNoon();
        _elapsed = 1f;
        _lastAltitude = float.NaN;
    }

    private void BindTables()
    {
        GL.ActiveTexture(TextureUnit.Texture0);
        GL.BindTexture(TextureTarget.Texture2D, _trans);
        GL.ActiveTexture(TextureUnit.Texture1);
        GL.BindTexture(TextureTarget.Texture2D, _multiple);
        GL.UseProgram(_skyProgram);
    }

    private void RebuildTransport()
    {
        // Extinction, successive scattering and sky integration must share the same medium.
        GL.UseProgram(_transProgram); GL.Uniform4(_transProfile, _profile[0], _profile[1], _profile[2], _profile[3]);
        AtmosphereCompute.Dispatch2D(_transProgram, _trans, 256, 64);
        GL.UseProgram(_multipleProgram); GL.Uniform4(_multipleProfile, _profile[0], _profile[1], _profile[2], _profile[3]);
        GL.ActiveTexture(TextureUnit.Texture0); GL.BindTexture(TextureTarget.Texture2D, _trans);
        AtmosphereCompute.Dispatch2D(_multipleProgram, _multiple, 32, 32);
        GL.UseProgram(_skyProgram); GL.Uniform4(_skyProfile, _profile[0], _profile[1], _profile[2], _profile[3]);
    }

    private void NormalizeNoon()
    {
        // Numerical reference only: match the old clear-noon zenith luminance
        // (sky texture *3). No screenshots or frame/performance measurements.
        int nativeSky = _api.Render.GetOrLoadTexture(new AssetLocation("game", "textures/environment/sky.png"));
        GL.ActiveTexture(TextureUnit.Texture0);
        GL.BindTexture(TextureTarget.Texture2D, nativeSky);
        GL.GetTexLevelParameter(TextureTarget.Texture2D, 0, GetTextureParameter.TextureWidth, out int width);
        GL.GetTexLevelParameter(TextureTarget.Texture2D, 0, GetTextureParameter.TextureHeight, out int height);
        var native = new float[width * height * 4];
        GL.GetTexImage(TextureTarget.Texture2D, 0, PixelFormat.Rgba, PixelType.Float, native);
        int offset = (width - 1) * 4; // u=1, v=0: noon, zenith in native lookup.
        float target = 3f * (native[offset] * 0.2126f + native[offset + 1] * 0.7152f + native[offset + 2] * 0.0722f);
        BindTables();
        GL.Uniform4(_sunLocation, 0f, 1f, 0f, 1f);
        GL.Uniform4(_moonLocation, 0f, -1f, 0f, 0f);
        GL.Uniform1(_heightLocation, 1f);
        AtmosphereCompute.Dispatch2D(_skyProgram, Current, 192, 108);
        GL.MemoryBarrier(MemoryBarrierFlags.TextureUpdateBarrierBit);
        GL.BindTexture(TextureTarget.Texture2D, Current);
        var reference = new float[192 * 108 * 4];
        GL.GetTexImage(TextureTarget.Texture2D, 0, PixelFormat.Rgba, PixelType.Float, reference);
        int top = (107 * 192 + 96) * 4;
        float measured = reference[top] * 0.2126f + reference[top + 1] * 0.7152f + reference[top + 2] * 0.0722f;
        Normalization = measured > 0.00001f && target > 0f ? target / measured : 1f;
        _api.Logger.Notification($"[DRT AgX] Atmospheric sky noon normalization={Normalization:F4}; native={target:F4}, LUT={measured:F4}.");
    }

    internal void Update(float[] frame, float deltaTime)
    {
        _elapsed += Math.Max(deltaTime, 0f);
        _blendTime += Math.Max(deltaTime, 0f);
        bool changed = !Ready || Math.Abs(frame[23] - _lastAltitude) > 1f;
        bool profileChanged = !Ready;
        for (int i = 0; i < 4; ++i)
            profileChanged |= Math.Abs(frame[AtmosphereSkyProfile.Offset + i] - _profile[i]) > 0.025f;
        changed |= profileChanged;
        for (int i = 0; i < 4; ++i)
            changed |= Math.Abs(frame[12 + i] - _lastSun[i]) > 0.0001f || Math.Abs(frame[16 + i] - _lastMoon[i]) > 0.0001f;
        if (!changed || Ready && _elapsed < 0.1f) return;
        if (profileChanged)
        {
            // Bounded refresh threshold avoids rebuilding transport for tiny weather/time changes.
            Array.Copy(frame, AtmosphereSkyProfile.Offset, _profile, 0, 4);
            RebuildTransport();
        }
        bool first = !Ready;
        (Previous, Current) = (Current, Previous);
        BindTables();
        GL.Uniform4(_sunLocation, frame[12], frame[13], frame[14], frame[15]);
        GL.Uniform4(_moonLocation, frame[16], frame[17], frame[18], frame[19]);
        GL.Uniform1(_heightLocation, frame[23]);
        AtmosphereCompute.Dispatch2D(_skyProgram, Current, 192, 108);
        if (first)
        {
            // CopyImageSubData reads shader-written storage through the update
            // path; publish the write before copying the initial history LUT.
            GL.MemoryBarrier(MemoryBarrierFlags.TextureUpdateBarrierBit);
            GL.CopyImageSubData(Current, ImageTarget.Texture2D, 0, 0, 0, 0,
                Previous, ImageTarget.Texture2D, 0, 0, 0, 0, 192, 108, 1);
            GL.MemoryBarrier(MemoryBarrierFlags.TextureFetchBarrierBit | MemoryBarrierFlags.TextureUpdateBarrierBit);
        }
        Array.Copy(frame, 12, _lastSun, 0, 4);
        Array.Copy(frame, 16, _lastMoon, 0, 4);
        _lastAltitude = frame[23];
        _elapsed = _blendTime = 0f;
        Ready = true;
    }

    internal void ResetWorld() { Ready = false; _lastAltitude = float.NaN; }

    public void Dispose()
    {
        Ready = false;
        foreach (int program in new[] { _transProgram, _multipleProgram, _skyProgram })
            if (program != 0) GL.DeleteProgram(program);
        foreach (int texture in new[] { _trans, _multiple, Previous, Current })
            if (texture != 0) GL.DeleteTexture(texture);
        _transProgram = _multipleProgram = _skyProgram = _trans = _multiple = Previous = Current = 0;
    }
}
