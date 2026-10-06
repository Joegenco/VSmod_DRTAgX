using System;
using System.Text;
using OpenTK.Graphics.OpenGL4;
using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace DRTAgX;

// The native graphics loader does not expose compute stages. This small owner
// compiles the maintained asset and restores binding 6 immediately after dispatch.
internal sealed class StaticLightTileCompute : IDisposable
{
    private int _program, _rectangles, _countLocation, _widthLocation, _tilesLocation;
    private bool _failed;
    private int _depthProgram, _depthCount, _depthWidth, _depthTiles, _depthSampler, _depthInverse;
    private bool _depthFailed;
    internal bool Failed => _failed;

    internal void Reset()
    {
        if (_program != 0) GL.DeleteProgram(_program);
        if (_depthProgram != 0) GL.DeleteProgram(_depthProgram);
        _program = 0;
        _depthProgram = 0; _depthFailed = false;
        _failed = false;
    }

    private void Ensure(ICoreClientAPI api)
    {
        if (_program != 0) return;
        if (_failed) throw new InvalidOperationException("Static tile compute unavailable until shader reload.");
        int shader = GL.CreateShader(ShaderType.ComputeShader), program = 0;
        try
        {
            var asset = api.Assets.Get(new AssetLocation("drtagx", "shaders/staticlighttiles.csh"));
            GL.ShaderSource(shader, Encoding.UTF8.GetString(asset.Data));
            GL.CompileShader(shader);
            GL.GetShader(shader, ShaderParameter.CompileStatus, out int compiled);
            if (compiled == 0) throw new InvalidOperationException("staticlighttiles.csh (GLSL 430): " + GL.GetShaderInfoLog(shader));
            program = GL.CreateProgram();
            GL.AttachShader(program, shader);
            GL.LinkProgram(program);
            GL.GetProgram(program, GetProgramParameterName.LinkStatus, out int linked);
            if (linked == 0) throw new InvalidOperationException("staticlighttiles.csh link: " + GL.GetProgramInfoLog(program));
            _countLocation = GL.GetUniformLocation(program, "sourceCount");
            _widthLocation = GL.GetUniformLocation(program, "tileWidth");
            _tilesLocation = GL.GetUniformLocation(program, "tileCount");
            _program = program;
            program = 0;
        }
        catch { _failed = true; throw; }
        finally
        {
            GL.DeleteShader(shader);
            if (program != 0) GL.DeleteProgram(program);
        }
    }

    internal void Dispatch(ICoreClientAPI api, int[] rectangles, int count, int width, int tiles)
    {
        Ensure(api);
        DispatchProgram(rectangles, count, width, tiles, _program, _countLocation, _widthLocation, _tilesLocation, 0, null);
    }

    internal bool DispatchDepth(ICoreClientAPI api, int[] rectangles, int count, int width, int tiles,
        int depthTexture, float[] inverseProjection)
    {
        if (_depthFailed) return false;
        if (_depthProgram == 0)
        {
            int shader = GL.CreateShader(ShaderType.ComputeShader), program = 0;
            try
            {
                var asset = api.Assets.Get(new AssetLocation("drtagx", "shaders/staticlighttiles.csh"));
                string source = Encoding.UTF8.GetString(asset.Data).Replace("#version 430 core", "#version 430 core\n#define DRT_DEPTH_CULL");
                GL.ShaderSource(shader, source); GL.CompileShader(shader);
                GL.GetShader(shader, ShaderParameter.CompileStatus, out int compiled);
                if (compiled == 0) throw new InvalidOperationException(GL.GetShaderInfoLog(shader));
                program = GL.CreateProgram(); GL.AttachShader(program, shader); GL.LinkProgram(program);
                GL.GetProgram(program, GetProgramParameterName.LinkStatus, out int linked);
                if (linked == 0) throw new InvalidOperationException(GL.GetProgramInfoLog(program));
                _depthCount = GL.GetUniformLocation(program, "sourceCount");
                _depthWidth = GL.GetUniformLocation(program, "tileWidth");
                _depthTiles = GL.GetUniformLocation(program, "tileCount");
                _depthSampler = GL.GetUniformLocation(program, "terrainDepth");
                _depthInverse = GL.GetUniformLocation(program, "inverseProjection");
                _depthProgram = program; program = 0;
            }
            catch (Exception ex)
            {
                _depthFailed = true;
                api.Logger.Warning("[DRT AgX] Depth-aware placed-light culling unavailable; retaining coarse masks: {0}", ex.Message);
                return false;
            }
            finally { GL.DeleteShader(shader); if (program != 0) GL.DeleteProgram(program); }
        }
        DispatchProgram(rectangles, count, width, tiles, _depthProgram, _depthCount, _depthWidth, _depthTiles, depthTexture, inverseProjection);
        return true;
    }

    private void DispatchProgram(int[] rectangles, int count, int width, int tiles, int program,
        int countLocation, int widthLocation, int tilesLocation, int depthTexture, float[]? inverseProjection)
    {
        int previousProgram = GL.GetInteger(GetPName.CurrentProgram);
        int generic = GL.GetInteger(GetPName.ShaderStorageBufferBinding);
        var previousRectangles = IndexedBufferState.Capture(BufferRangeTarget.ShaderStorageBuffer, 6);
        int active = 0, oldDepth = 0, oldSampler = 0;
        int timing = PlacedLightGpuProfile.Tiles.Begin(PlacedLightGpuProfile.Enabled);
        if (depthTexture != 0)
        {
            active = GL.GetInteger(GetPName.ActiveTexture);
            GL.ActiveTexture(TextureUnit.Texture13);
            oldDepth = GL.GetInteger(GetPName.TextureBinding2D); oldSampler = GL.GetInteger(GetPName.SamplerBinding);
        }
        try
        {
            if (_rectangles == 0) _rectangles = GL.GenBuffer();
            GL.BindBuffer(BufferTarget.ShaderStorageBuffer, _rectangles);
            GL.BufferData(BufferTarget.ShaderStorageBuffer, count * 4 * sizeof(int), rectangles, BufferUsageHint.StreamDraw);
            GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 6, _rectangles);
            GL.UseProgram(program);
            GL.Uniform1(countLocation, count);
            GL.Uniform1(widthLocation, width);
            GL.Uniform1(tilesLocation, tiles);
            if (depthTexture != 0)
            {
                // Depth is already drawn this frame at Opaque .37. Compute
                // writes only SSBO masks, never an attached framebuffer texture.
                GL.BindSampler(13, 0); GL.BindTexture(TextureTarget.Texture2D, depthTexture);
                GL.Uniform1(_depthSampler, 13);
                GL.UniformMatrix4(_depthInverse, 1, false, inverseProjection!);
            }
            // The previous deferred draw may still be reading this persistent
            // tile buffer. Order those accesses before overwriting its masks.
            GL.MemoryBarrier(MemoryBarrierFlags.ShaderStorageBarrierBit);
            // GL 4.3 guarantees at least 65535 groups per dimension. Split large
            // (e.g. 8K) depth grids across Y without changing linear tile indices.
            GL.DispatchCompute(depthTexture != 0 ? Math.Min(tiles, 65535) : (tiles + 63) / 64,
                depthTexture != 0 ? (tiles + 65534) / 65535 : 1, 1);
            // Deferred fragment reads see this dispatch's SSBO writes without CPU readback.
            GL.MemoryBarrier(MemoryBarrierFlags.ShaderStorageBarrierBit);
        }
        finally
        {
            PlacedLightGpuProfile.Tiles.End(timing);
            if (depthTexture != 0)
            {
                GL.BindTexture(TextureTarget.Texture2D, oldDepth); GL.BindSampler(13, oldSampler);
                GL.ActiveTexture((TextureUnit)active);
            }
            previousRectangles.Restore();
            GL.BindBuffer(BufferTarget.ShaderStorageBuffer, generic);
            GL.UseProgram(previousProgram);
        }
    }

    public void Dispose()
    {
        Reset();
        if (_rectangles != 0) GL.DeleteBuffer(_rectangles);
        _rectangles = 0;
    }
}
