using System;
using System.Collections.Generic;
using System.Text;
using OpenTK.Graphics.OpenGL4;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.Client.NoObf;

namespace DRTAgX;

/// <summary>Render-thread bounded FIFO preparation and whole-page publication.
/// GPU commands finish a page from one immutable source/depth snapshot before
/// any later atlas copy; no partial PCF result survives to the next frame.</summary>
internal sealed class PlacedLightSurfaceWorker : IDisposable
{
    // The CPU allocation and injected shader capacities share one definition.
    // Whole immutable jobs still protect ring reuse during the larger burst.
    private const int QueueHeaderBytes = 32 + PlacedLightUpdateExperiment.PageJobs * 16;
    private readonly uint[] _zero = new uint[1];

    private readonly float[] _inverseProjection = new float[16],
        _inverseView = new float[16],
        _calibration = new float[64];

    private readonly double[] _inverseDouble = new double[16];
    private readonly Vec3d _camera = new();
    private readonly int[] _textures = new int[4], _samplers = new int[4];
    private Program? _collect, _schedule, _evaluate;
    private int _queue, _budget = 2;
    private uint _generation;
    private uint _visibleRefreshRevision;

    private int _fullResolutionFrames;

    // Four frames cover queue contention/retries while a 1,024-page bounded
    // worker drains the 4,096-request ring. This burst is semantic-edit only.
    private const int FullResolutionRefreshFrames = 4;
    private bool _failed;
    internal int PageBudget => _budget;
    internal int Dispatches { get; private set; }
    internal int Collections { get; private set; }
    internal int EmptyCollectionsSkipped { get; private set; }

    /// <summary>Explicit diagnostic endpoint only; never polled by the normal
    /// frame loop because reading queue memory may wait for prior GPU commands.</summary>
    private sealed class Program(int id)
    {
        internal readonly int Id = id;
        internal readonly Dictionary<string, int> Uniforms = new(StringComparer.Ordinal);

        internal int Location(string name)
        {
            if (!Uniforms.TryGetValue(name, out int at)) Uniforms.Add(name, at = GL.GetUniformLocation(Id, name));
            return at;
        }
    }

    private static string Expand(ICoreClientAPI api, string name, HashSet<string> seen)
    {
        if (!seen.Add(name)) return string.Empty;
        // Only maintained auxiliary shader assets, compiled once per reload.
        string directory = name == "placedpages.csh" ? "shaders/" :
            name.StartsWith("drtagx_deferred_", StringComparison.Ordinal) ? "shaders/deferred/" : "shaders/lighting/";
        string source = api.Assets.Get(new AssetLocation("drtagx", directory + name)).ToText();
        var result = new StringBuilder(source.Length);
        foreach (string line in source.Split('\n'))
        {
            string trimmed = line.Trim();
            if (trimmed.StartsWith("#include ", StringComparison.Ordinal))
                result.AppendLine(Expand(api, trimmed[9..].Trim(), seen));
            else result.AppendLine(line);
        }

        return result.ToString();
    }

    private static Program Compile(string source)
    {
        int shader = GL.CreateShader(ShaderType.ComputeShader), program = 0;
        try
        {
            GL.ShaderSource(shader, source);
            GL.CompileShader(shader);
            GL.GetShader(shader, ShaderParameter.CompileStatus, out int compiled);
            if (compiled == 0) throw new InvalidOperationException("placedpages.csh: " + GL.GetShaderInfoLog(shader));
            program = GL.CreateProgram();
            GL.AttachShader(program, shader);
            GL.LinkProgram(program);
            GL.GetProgram(program, GetProgramParameterName.LinkStatus, out int linked);
            if (linked == 0)
                throw new InvalidOperationException("placedpages.csh link: " + GL.GetProgramInfoLog(program));
            return new Program(program);
        }
        catch
        {
            if (program != 0) GL.DeleteProgram(program);
            throw;
        }
        finally
        {
            GL.DeleteShader(shader);
        }
    }

    internal bool Dispatch(ICoreClientAPI api, PlacedLightSurfaceCache cache, int plane, int normal, int bounds,
        int width,
        int height)
    {
        if (_failed || !cache.Ready || plane == 0 || normal == 0 || bounds == 0 || width <= 0 || height <= 0 ||
            api.World is not ClientMain world) return !_failed;
        int program = GL.GetInteger(GetPName.CurrentProgram),
            generic = GL.GetInteger(GetPName.ShaderStorageBufferBinding);
        int active = GL.GetInteger(GetPName.ActiveTexture);
        var queueBinding = IndexedBufferState.Capture(BufferRangeTarget.ShaderStorageBuffer, 5);
        var pagesBinding = IndexedBufferState.Capture(BufferRangeTarget.ShaderStorageBuffer, 6);
        var sourceBinding = IndexedBufferState.Capture(BufferRangeTarget.ShaderStorageBuffer, 7);
        for (int i = 0; i < 4; ++i)
        {
            GL.ActiveTexture(TextureUnit.Texture11 + i);
            _textures[i] = GL.GetInteger(i == 2 ? GetPName.TextureBinding2DArray : GetPName.TextureBinding2D);
            _samplers[i] = GL.GetInteger(GetPName.SamplerBinding);
        }

        try
        {
            if (_collect == null || _evaluate == null)
            {
                string source = Expand(api, "placedpages.csh", new HashSet<string>(StringComparer.Ordinal));
                source = source.Replace("#version 430 core", "#version 430 core\n" +
                                                             $"#define DRT_PAGE_JOB_CAPACITY {PlacedLightUpdateExperiment.PageJobs}\n" +
                                                             $"#define DRT_PAGE_QUEUE_CAPACITY {PlacedLightUpdateExperiment.PageQueue}");
                _collect = Compile(source.Replace("#version 430 core", "#version 430 core\n#define DRT_PAGE_COLLECT"));
                _schedule = Compile(source.Replace("#version 430 core",
                    "#version 430 core\n#define DRT_PAGE_SCHEDULE"));
                _evaluate = Compile(source);
            }

            if (_queue == 0)
            {
                _queue = GL.GenBuffer();
                GL.BindBuffer(BufferTarget.ShaderStorageBuffer, _queue);
                GL.BufferData(BufferTarget.ShaderStorageBuffer,
                    QueueHeaderBytes + PlacedLightUpdateExperiment.PageQueue * 16, IntPtr.Zero,
                    BufferUsageHint.DynamicDraw);
            }

            if (_generation != cache.Generation)
            {
                GL.BindBuffer(BufferTarget.ShaderStorageBuffer, _queue);
                GL.ClearBufferData(BufferTarget.ShaderStorageBuffer, PixelInternalFormat.R32ui, PixelFormat.RedInteger,
                    PixelType.UnsignedInt, _zero);
                _generation = cache.Generation;
            }

            // The accepted production policy completes visible pages at full
            // bounded throughput. Adaptive timing experiments remain Debug-only.
            _budget = PlacedLightUpdateExperiment.PageJobs;
            PlacedLightView.CameraFromView(world.CurrentModelViewMatrixd, _inverseDouble, _camera);
            for (int i = 0; i < 16; ++i) _inverseView[i] = (float)_inverseDouble[i];
            Mat4f.Invert(_inverseProjection, api.Render.CurrentProjectionMatrix);
            SurfaceLightBindings.Calibrate(api.World.BlockLightLevels, _calibration);
            GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 5, _queue);
            GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 6, cache.Buffer);
            GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 7, cache.SourceBuffer);
            GL.ActiveTexture(TextureUnit.Texture11);
            GL.BindSampler(11, 0);
            GL.BindTexture(TextureTarget.Texture2D, plane);
            GL.ActiveTexture(TextureUnit.Texture12);
            GL.BindSampler(12, 0);
            GL.BindTexture(TextureTarget.Texture2D, normal);
            GL.ActiveTexture(TextureUnit.Texture13);
            GL.BindSampler(13, 0);
            GL.BindTexture(TextureTarget.Texture2DArray, cache.ShadowTexture);
            GL.ActiveTexture(TextureUnit.Texture14);
            GL.BindSampler(14, 0);
            GL.BindTexture(TextureTarget.Texture2D, bounds);
            if (_visibleRefreshRevision != cache.VisibleRefreshRevision)
            {
                _visibleRefreshRevision = cache.VisibleRefreshRevision;
                _fullResolutionFrames = FullResolutionRefreshFrames;
            }

            // The rotating 4x4 collector is useful during unchanged gameplay,
            // but can hide a tiny edited page for 15 frames. Cover every pixel
            // briefly after source edits or a coherent depth-map promotion.
            var pixelStep = _fullResolutionFrames > 0 ? 1 : PlacedLightUpdateExperiment.PixelStep;
            GL.MemoryBarrier(MemoryBarrierFlags.ShaderStorageBarrierBit | MemoryBarrierFlags.TextureFetchBarrierBit);
            // No source influence means every collector invocation would reject
            // its receiver. Skip that screen scan, but still schedule/evaluate:
            // old queued pages must release their locks after the last light goes.
            if (cache.BoundsReady)
            {
                Bind(_collect, cache);
                GL.Uniform1(_collect.Location("drtReceiverPlane"), 11);
                GL.Uniform1(_collect.Location("drtPageNormal"), 12);
                GL.Uniform1(_collect.Location("drtReceiverBounds"), 14);
                GL.Uniform2(_collect.Location("drtPageExtent"), width, height);
                GL.Uniform1(_collect.Location("drtPagePixelStep"), pixelStep);
                GL.UniformMatrix4(_collect.Location("drtPageInverseProjection"), 1, false, _inverseProjection);
                GL.UniformMatrix4(_collect.Location("drtPageInverseView"), 1, false, _inverseView);
                var groupPixels = pixelStep * 8;
                GL.DispatchCompute((width + groupPixels - 1) / groupPixels,
                    (height + groupPixels - 1) / groupPixels, 1);
                Collections++;
            }
            else EmptyCollectionsSkipped++;

            GL.MemoryBarrier(MemoryBarrierFlags.ShaderStorageBarrierBit);
            var schedule = _schedule!; // EnsurePrograms completed before this dispatch.
            Bind(schedule, cache);
            // Keep recently visible queued pages eligible throughout the normal
            // 16-phase cycle even when an edit temporarily samples at step1.
            var ordinaryStep = PlacedLightUpdateExperiment.PixelStep;
            GL.Uniform1(schedule.Location("drtPageVisibleFrames"), (uint)(ordinaryStep * ordinaryStep));
            GL.Uniform1(schedule.Location("drtPageBudget"), (uint)_budget);
            GL.DispatchCompute(1, 1, 1);
            GL.MemoryBarrier(MemoryBarrierFlags.ShaderStorageBarrierBit);
            Bind(_evaluate, cache);
            GL.Uniform1(_evaluate.Location("drtStaticMaps"), 13);
            GL.Uniform2(_evaluate.Location("drtPlacedCalibration[0]"), 32, _calibration);
            GL.DispatchCompute(_budget, 1, 1);
            GL.MemoryBarrier(MemoryBarrierFlags.ShaderStorageBarrierBit);
            Dispatches++;
            if (_fullResolutionFrames > 0) --_fullResolutionFrames;
            return true;
        }
        catch (Exception ex)
        {
            _failed = true;
            api.Logger.Warning("[DRTAgX] Bounded PLS preparation unavailable; retaining native lighting: " +
                               ex.Message);
            return false;
        }
        finally
        {
            for (int i = 0; i < 4; ++i)
            {
                GL.ActiveTexture(TextureUnit.Texture11 + i);
                GL.BindTexture(i == 2 ? TextureTarget.Texture2DArray : TextureTarget.Texture2D, _textures[i]);
                GL.BindSampler(11 + i, _samplers[i]);
            }

            GL.ActiveTexture((TextureUnit)active);
            sourceBinding.Restore();
            pagesBinding.Restore();
            queueBinding.Restore();
            GL.BindBuffer(BufferTarget.ShaderStorageBuffer, generic);
            GL.UseProgram(program);
        }
    }

    private void Bind(Program p, PlacedLightSurfaceCache c)
    {
        GL.UseProgram(p.Id);
        GL.Uniform1(p.Location("drtMemoGeneration"), c.Generation);
        GL.Uniform1(p.Location("drtMemoFrame"), c.Frame);
        GL.Uniform1(p.Location("drtMemoCapacityMask"), c.PageSetMask);
        GL.Uniform1(p.Location("drtMemoSourceCount"), c.SourceCount);
        GL.Uniform1(p.Location("drtMemoDenseSourceRevision"), c.DenseSourceRevision);
        GL.Uniform1(p.Location("drtMemoEvaluationRevision"), c.EvaluationRevision);
        GL.Uniform1(p.Location("drtMemoTransitionFrames"), PlacedLightUpdateExperiment.TransitionFrames);
        GL.Uniform1(p.Location("drtMemoRegionOffset"), PlacedLightSurfaceCache.SourcePrefixBytes / 16);
        GL.Uniform3(p.Location("drtMemoAnchorCells"), c.AnchorCellX, c.AnchorCellY, c.AnchorCellZ);
        GL.Uniform3(p.Location("drtMemoCameraOffset"), c.CameraOffset(_camera.X, _camera.Y, _camera.Z));
        GL.Uniform1(p.Location("drtMemoBoundsReady"), c.BoundsReady ? 1 : 0);
        GL.Uniform3(p.Location("drtMemoBoundsMin"), c.BoundsMinimum);
        GL.Uniform3(p.Location("drtMemoBoundsMax"), c.BoundsMaximum);
    }

    internal void Reload()
    {
        if (_collect != null) GL.DeleteProgram(_collect.Id);
        if (_schedule != null) GL.DeleteProgram(_schedule.Id);
        if (_evaluate != null) GL.DeleteProgram(_evaluate.Id);
        _collect = _schedule = _evaluate = null;
        _failed = false;
        _visibleRefreshRevision = 0;
        _fullResolutionFrames = 0;
        // Queued jobs contain receiver identity only, so a shader reload can
        // safely evaluate them against the next reconciled source snapshot.
    }

    public void Dispose()
    {
        Reload();
        if (_queue != 0) GL.DeleteBuffer(_queue);
        _queue = 0;
        _generation = 0;
    }
}