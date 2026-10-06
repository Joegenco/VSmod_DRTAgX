using System;
using OpenTK.Graphics.OpenGL4;
using Vintagestory.API.Client;
using Vintagestory.API.MathTools;
using Vintagestory.Client.NoObf;

namespace DRTAgX;

/// <summary>One ambient snapshot per frame, after native preparation and before sky/world draws.</summary>
internal sealed class AtmosphereRenderer : IRenderer
{
    private readonly ICoreClientAPI _api;
    internal const int FrameFloatCount = 120; // Existing ABI plus an aligned fog/shelter/LOD vec4.
    private readonly float[] _frame = new float[FrameFloatCount], _inverseView = new float[16], _inverseProjection = new float[16];
    private readonly double[] _sunGridInverseView = new double[16];
    private readonly AtmosphereComputeState _state = new();
    private readonly AtmosphereSkyResources _sky;
    private readonly AtmosphereFogVolume _volume = new();
    private readonly AtmosphereAmbientResources _ambient = new();
    private readonly AtmosphereSkyProfile _profile = new();
    private readonly AtmosphereSheyderBridge _sheyder;
    private readonly AtmosphereProgramBindings _bindings;
    private readonly AtmosphereFogEnvironment _fogEnvironment = new();
    private readonly LodFogRange _lodFogRange;
    private readonly int _buffer;
    private int _worldFogProgram, _wfDensity = -1, _wfIntensity = -1, _wfStart = -1, _wfFalloff = -1;
    private object? _world;
    private bool _pending = true, _failureLogged, _skyAvailable, _volumeAvailable, _ambientAvailable, _airCaptured;
    private double _airCameraY;
    public double RenderOrder => 0.09; // Night sky .1, sky .2, celestial .3, terrain .37.
    public int RenderRange => int.MaxValue;
    internal bool SunShadowGridEnabled { get; set; } = true;

    internal AtmosphereRenderer(ICoreClientAPI api)
    {
        _api = api;
        _lodFogRange = new LodFogRange(api);
        _sky = new AtmosphereSkyResources(api);
        _sheyder = new AtmosphereSheyderBridge(api);
        int previous = GL.GetInteger(GetPName.UniformBufferBinding);
        _buffer = GL.GenBuffer();
        GL.BindBuffer(BufferTarget.UniformBuffer, _buffer);
        GL.BufferData(BufferTarget.UniformBuffer, _frame.Length * sizeof(float), _frame, BufferUsageHint.DynamicDraw);
        GL.BindBuffer(BufferTarget.UniformBuffer, previous);
        _bindings = new AtmosphereProgramBindings(api, _sky, _buffer);
        api.Event.ReloadShader += Reload;
        api.Event.RegisterRenderer(this, EnumRenderStage.Opaque, "drtagx_atmosphere");
        api.Event.RegisterRenderer(this, EnumRenderStage.AfterFinalComposition, "drtagx_atmosphere_end");
    }

    private bool Reload()
    {
        _bindings.Reload();
        _pending = true; _skyAvailable = _volumeAvailable = false; _failureLogged = false; _worldFogProgram = 0;
        _ambientAvailable = false;
        _sheyder.ReplacementReady = false;
        FogAndLightAssetBridge.Apply(_api);
        return true;
    }

    private void Upload()
    {
        GL.BindBuffer(BufferTarget.UniformBuffer, _buffer);
        GL.BufferSubData(BufferTarget.UniformBuffer, IntPtr.Zero, _frame.Length * sizeof(float), _frame);
    }

    private static float Nonnegative(float value) => float.IsFinite(value) ? Math.Max(value, 0f) : 0f;
    private float Uniform(int program, int location)
    {
        if (location < 0) return 0f;
        GL.GetUniform(program, location, out float value);
        return Nonnegative(value);
    }

    private void Capture(float deltaTime)
    {
        var ambient = _api.Ambient;
        AmbientSkyInputs.Capture(ambient, _frame);
        var fog = ambient.BlendedFogColor;
        _frame[0] = Nonnegative(fog[0]); _frame[1] = Nonnegative(fog[1]); _frame[2] = Nonnegative(fog[2]); _frame[3] = 1f;
        _frame[4] = Nonnegative(ambient.BlendedFogDensity); _frame[5] = Math.Clamp(ambient.BlendedFogMin, 0f, 1f);
        _frame[6] = ambient.BlendedFlatFogDensity; _frame[7] = ambient.BlendedFlatFogYPosForShader;
        int desired = _api.Settings.Int.Get("viewDistance", 512);
        var data = _api.World.Player.WorldData;
        int approved = data.LastApprovedViewDistance;
        _frame[8] = desired > 0 && approved > 0 ? Math.Min(desired, approved) : Math.Max(desired, approved);
        _fogEnvironment.Capture(_api, _frame, _lodFogRange.Endpoint);
        _frame[9] = 200f;
        _frame[10] = _api.World is ClientMain client && client.playerProperties != null && client.playerProperties.EyesInWaterDepth >= 0.1f ? 1f : 0f;
        CaptureAir();
        var calendar = _api.World.Calendar;
        var sun = calendar.SunPositionNormalized; var moon = calendar.MoonPosition;
        _profile.Capture(calendar.SunsetMod, sun.Y, ambient.BlendedCloudDensity, deltaTime, _frame);
        float moonLength = (float)Math.Sqrt(moon.X * moon.X + moon.Y * moon.Y + moon.Z * moon.Z);
        _frame[12] = sun.X; _frame[13] = sun.Y; _frame[14] = sun.Z; _frame[15] = Nonnegative(calendar.SunLightStrength);
        _frame[16] = moon.X / Math.Max(moonLength, 0.00001f); _frame[17] = moon.Y / Math.Max(moonLength, 0.00001f);
        _frame[18] = moon.Z / Math.Max(moonLength, 0.00001f); _frame[19] = Nonnegative(calendar.MoonLightStrength);
        Mat4f.Invert(_inverseView, _api.Render.CameraMatrixOriginf);
        _frame[20] = _inverseView[12]; _frame[21] = _inverseView[13]; _frame[22] = _inverseView[14];
        _frame[23] = (float)(_api.World.Player.Entity.CameraPos.Y - _api.World.SeaLevel);
        // Reuse the existing frame flag; disabling the look also skips camera-phase work.
        _frame[115] = 0f;
        if (SunShadowGridEnabled && _api.World is ClientMain gridWorld)
        {
            // Native Opaque camera is ready at this snapshot boundary. Forward
            // receivers share the existing UBO; no per-draw camera/grid uploads.
            Mat4d.Invert(_sunGridInverseView, gridWorld.CurrentModelViewMatrixd);
            for (int i = 0; i < 3; ++i)
                _frame[112+i] = (float)(_sunGridInverseView[12+i] - Math.Floor(_sunGridInverseView[12+i]));
            _frame[115] = 1f;
        }
        _frame[26] = Math.Max(1f, _frame[8]);
        var buffers = _api.Render.FrameBuffers;
        // Scene fragments use primary-target pixels. Window dimensions apply
        // only to the final composite and differ when render scaling is enabled.
        var primary = buffers != null && buffers.Count > 0 ? buffers[0] : null;
        _frame[32] = primary is { Disposed: false, Width: > 0 } ? primary.Width : _api.Render.FrameWidth;
        _frame[33] = primary is { Disposed: false, Height: > 0 } ? primary.Height : _api.Render.FrameHeight;
        _frame[35] = buffers != null && buffers.Count > 5 && buffers[5] != null &&
            !buffers[5].Disposed && buffers[5].DepthTextureId > 0 ? 1f : 0f;
        Mat4f.Invert(_inverseProjection, _api.Render.CurrentProjectionMatrix);
        Array.Copy(_inverseProjection, 0, _frame, 36, 16);
        Array.Copy(_inverseView, 0, _frame, 52, 16);
        Array.Copy(_api.Render.CurrentProjectionMatrix, 0, _frame, 76, 16);
        // WorldFog's native renderer has already published its current-frame envelope.
        var native = _api.Render.GetEngineShader(EnumShaderProgram.Chunkopaque);
        if (native != null && !native.Disposed && native.ProgramId > 0)
        {
            int program = native.ProgramId;
            if (_worldFogProgram != program)
            {
                _worldFogProgram = program;
                _wfDensity = GL.GetUniformLocation(program, "wf_Density"); _wfIntensity = GL.GetUniformLocation(program, "wf_Intensity");
                _wfStart = GL.GetUniformLocation(program, "wf_FadeStart"); _wfFalloff = GL.GetUniformLocation(program, "wf_FallOff");
            }
            _frame[28] = Uniform(program, _wfDensity); _frame[29] = Uniform(program, _wfIntensity);
            _frame[30] = Uniform(program, _wfStart); _frame[31] = Uniform(program, _wfFalloff);
        }
    }

    private void CaptureAir()
    {
        double cameraY = _api.World.Player.Entity.CameraPos.Y;
        if (_frame[10] < 0.7f)
        {
            // A dry snapshot prevents the native submerged density/color from
            // also being applied to the air segment beyond a known water exit.
            Array.Copy(_frame, 0, _frame, 68, 8);
            _airCameraY = cameraY; _airCaptured = true;
        }
        else if (!_airCaptured)
        {
            // Starting submerged supplies no verified dry blend. Use the public
            // ambient base until the first dry frame; never guess modifier keys.
            var ambient = _api.Ambient.Base;
            _frame[68] = Nonnegative(ambient.FogColor.Value[0]);
            _frame[69] = Nonnegative(ambient.FogColor.Value[1]);
            _frame[70] = Nonnegative(ambient.FogColor.Value[2]);
            _frame[72] = Nonnegative(ambient.FogDensity.Value);
            _frame[73] = Math.Clamp(ambient.FogMin.Value, 0f, 1f);
            _frame[74] = ambient.FlatFogDensity.Value;
            _frame[75] = ambient.FlatFogYPos.Value + _api.World.SeaLevel - (float)cameraY;
            _airCameraY = cameraY; _airCaptured = true;
        }
        else
        {
            // Plane coordinates remain camera-relative while swimming.
            _frame[75] += (float)(_airCameraY - cameraY); _airCameraY = cameraY;
        }
    }

    public void OnRenderFrame(float deltaTime, EnumRenderStage stage)
    {
        _bindings.Restore();
        _state.Capture();
        try
        {
            if (stage == EnumRenderStage.AfterFinalComposition || _api.World?.Player?.Entity == null)
            {
                _frame[3] = 0f; Upload(); return; // GUI/item previews do not receive world fog.
            }
            if (!ReferenceEquals(_world, _api.World))
            { _world = _api.World; _sky.ResetWorld(); _profile.Reset(); _airCaptured = false; }
            Capture(deltaTime);
            if (_pending)
            {
                _pending = false;
                // Optional resource failures are independent. Retry only after
                // reload, never every frame; analytical transport stays active.
                try { _sky.Initialize(); _skyAvailable = true; }
                catch (Exception ex) { _sky.Dispose(); _api.Logger.Error("[DRT AgX] Sky LUT fallback: " + ex); }
                // Keep Sheyder's authored march/settings and avoid a second
                // shaft layer. Shared analytical weather/sky fog is unchanged.
                if (!_sheyder.LegacyAvailable)
                {
                    try { _volume.Initialize(_api); _volumeAvailable = true; }
                    catch (Exception ex) { _volume.Dispose(); _api.Logger.Error("[DRT AgX] Fog volume fallback: " + ex); }
                }
                try { _ambient.Initialize(_api); _ambientAvailable = true; }
                catch (Exception ex) { _ambient.Dispose(); _api.Logger.Error("[DRT AgX] Sky ambient fallback: " + ex); }
                _bindings.Volume = _volume.Texture;
                _api.Logger.Notification($"[DRT AgX] Unified atmospheric transport initialized (sky={_skyAvailable}, optional volume={_volumeAvailable}).");
            }
            if (_skyAvailable)
            {
                try { _sky.Update(_frame, deltaTime); }
                catch (Exception ex) { _skyAvailable = false; _sky.Dispose(); _api.Logger.Error("[DRT AgX] Sky update fallback: " + ex); }
            }
            _frame[11] = _sky.Blend; _frame[24] = _sky.Ready ? 1f : 0f; _frame[27] = _sky.Normalization;
            _frame[25] = 0f;
            if (_volumeAvailable)
            {
                // Only the fallback compute consumes this intermediate frame.
                // Healthy Sheyder needs the final upload below, once per frame.
                Upload();
                try { _volume.Render(_api, _frame, _buffer, _sheyder.Strength); }
                catch (Exception ex) { _volumeAvailable = false; _volume.Dispose(); _bindings.Volume = 0; _api.Logger.Error("[DRT AgX] Shaft update fallback: " + ex); }
            }
            // Only the compatibility fallback owns these volume/composite
            // gates. Healthy Sheyder shafts keep their native late pass.
            _frame[25] = _volume.Ready ? 1f : 0f;
            _frame[34] = _volumeAvailable ? 1f : 0f;
            _frame[AtmosphereAmbientResources.ResultOffset + 3] = 0f;
            Upload();
            // This must follow the last CPU upload: compute publishes only its aligned result vec4.
            if (_ambientAvailable && _sky.Ready)
            {
                try { _ambient.Render(_sky, _buffer); }
                catch (Exception ex)
                { _ambientAvailable = false; _ambient.Dispose(); _api.Logger.Error("[DRT AgX] Ambient update fallback: " + ex); }
            }
            _sheyder.ReplacementReady = _volumeAvailable;
        }
        catch (Exception ex)
        {
            _frame[24] = _frame[25] = _frame[34] = 0f; Upload();
            _sheyder.ReplacementReady = false;
            if (!_failureLogged) { _api.Logger.Error("[DRT AgX] Atmosphere retained analytical/legacy fallback: " + ex); _failureLogged = true; }
        }
        finally { _state.Dispose(); }
    }

    public void Dispose()
    {
        _api.Event.ReloadShader -= Reload;
        _api.Event.UnregisterRenderer(this, EnumRenderStage.Opaque);
        _api.Event.UnregisterRenderer(this, EnumRenderStage.AfterFinalComposition);
        _bindings.Dispose(); _sheyder.Dispose(); _volume.Dispose(); _ambient.Dispose(); _sky.Dispose();
        GL.DeleteBuffer(_buffer);
    }
}
