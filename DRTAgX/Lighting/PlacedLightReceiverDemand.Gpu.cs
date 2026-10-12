using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using OpenTK.Graphics.OpenGL4;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace DRTAgX;

internal sealed partial class PlacedLightReceiverDemand
{
    private sealed class Slot
    {
        internal int Buffer, ReadbackBuffer, X, Y, Z;
        internal IntPtr Fence, Mapped;
        internal ulong Serial;
        internal object? World;
    }

    private readonly Slot[] _slots = { new(), new(), new() };

    private readonly uint[] _zero = new uint[1];

    // Marshal.Copy accepts int[], preserving every uint bit when copied into
    // the established feedback ABI. Reuse storage rather than pinning per poll.
    private readonly int[] _mappedReadback = new int[HeaderWords + WordCount + MaxBounds * 8];
    private int _program, _compactProgram, _scratch, _depth, _dimensions, _projection, _view, _camera;
    private int _submittedDepth, _submittedWidth, _submittedHeight;
    private readonly float[] _observedProjection = new float[16];
    private bool _projectionValid, _submittedCameraValid;
    private double _submittedCameraX, _submittedCameraY, _submittedCameraZ;
    private Slot? _cycleSlot;
    private int _pixelOrigin, _pixelExtent, _workRegion = -1;
    private bool _workBegin = true, _workComplete = true;
    internal int RegionDispatches { get; private set; }

    private int PendingCount
    {
        get
        {
            int count = 0;
            foreach (var slot in _slots)
                if (slot.Fence != IntPtr.Zero)
                    count++;
            return count;
        }
    }

    internal void Update(ICoreClientAPI api, double[] view, float[] projection, Vec3d cameraWorld)
    {
        UpdateNearCamera(cameraWorld.X, cameraWorld.Y, cameraWorld.Z);
        if (!ReferenceEquals(_world, api.World))
        {
            _world = api.World;
            SnapshotReady = false;
            OccupiedRegions = 0;
            _acceptedSerial = ++_serial;
            _submittedView.Reset();
            _cadence.Reset();
            _cycleSlot = null;
            _projectionValid = _submittedCameraValid = false;
            RequestRefresh();
            Revision++;
        }

        if (_failed) return;
        try
        {
            Poll();
            var buffers = api.Render.FrameBuffers;
            // Same already-drawn primary depth contract used by the existing
            // static-light tile cull and held-light projection fitting paths.
            if (buffers.Count == 0 || buffers[0] is not
                    { Disposed: false, DepthTextureId: > 0, Width: > 0, Height: > 0 } primary)
            {
                SetNotReady();
                // A temporarily unavailable framebuffer may return with the
                // same matrices and texture identity. Keep recovery pending.
                RequestRefresh();
                return;
            }

            bool changed = _submittedView.Update(view, projection);
            bool projectionChanged = !_projectionValid;
            for (int i = 0; i < 16 && !projectionChanged; i++)
                // Exact retained values distinguish projection edits from view
                // motion; authored matrices are not approximate geometry tests.
                projectionChanged = !_observedProjection[i].Equals(projection[i]);
            if (projectionChanged)
            {
                Array.Copy(projection, _observedProjection, 16);
                _projectionValid = true;
            }

            double dx = cameraWorld.X - _submittedCameraX,
                dy = cameraWorld.Y - _submittedCameraY,
                dz = cameraWorld.Z - _submittedCameraZ;
            bool teleport = !_submittedCameraValid || dx * dx + dy * dy + dz * dz >= 64;
            // Full-pixel receiver collection is expensive even in an empty cave.
            // Spread motion over three of nine regions per second. First motion
            // and the final settled view get complete coverage;
            // projection/resize/teleport and real geometry
            // callbacks remain immediate; accepted precise bounds stay readable.
            long now = Stopwatch.GetTimestamp();
            _cadence.Observe(changed, now);
            // Explicit development A/B control reproduces the previous policy
            // in the same binary. Ordinary launches never enable this override.
            if (projectionChanged || teleport || _submittedDepth != primary.DepthTextureId ||
                _submittedWidth != primary.Width || _submittedHeight != primary.Height) RequestRefresh();
            bool immediate = Volatile.Read(ref _refresh) != 0;
            if (!immediate && !_cadence.Due(now))
            {
                if (_cadence.Pending) DeferredMotionFrames++;
                return;
            }

            // Two unfinished dispatches are the admission bound. A slow GPU
            // skips submission; it never blocks or overwrites an in-flight slot.
            if (PendingCount >= 2)
            {
                return;
            }

            if (Mat4f.Invert(_inverseProjection, projection) == null)
            {
                SetNotReady();
                return;
            }

            Mat4d.Invert(_inverseView, view);
            for (int i = 0; i < 16; i++)
                if (!double.IsFinite(_inverseView[i]) || !float.IsFinite(_inverseProjection[i]))
                {
                    SetNotReady();
                    return;
                }

            Array.Clear(_rotation);
            for (int column = 0; column < 3; column++)
            for (int row = 0; row < 3; row++)
                _rotation[column * 4 + row] = (float)_inverseView[column * 4 + row];
            _rotation[15] = 1;
            bool full = immediate || _cadence.FullView;
            // Partial cycles have no feedback fence yet but still own their
            // slot and shared scratch; normal admission cannot reuse them.
            Slot? free = full ? null : _cycleSlot;
            if (free == null)
                foreach (var slot in _slots)
                    if (slot.Fence == IntPtr.Zero)
                    {
                        free = slot;
                        break;
                    }

            if (free == null) return;
            _workRegion = full ? -1 : _cadence.Region;
            _workBegin = full || _cycleSlot == null;
            _workComplete = full || _workRegion == PlacedLightDemandCadence.RegionsPerCycle - 1;
            if (_workBegin)
            {
                // One fixed world grid keeps tiles from moving views in the
                // same namespace until the complete cycle is published.
                free.X = checked((int)Math.Floor(cameraWorld.X / CellBlocks)) - Axis / 2;
                free.Y = checked((int)Math.Floor(cameraWorld.Y / CellBlocks)) - Axis / 2;
                free.Z = checked((int)Math.Floor(cameraWorld.Z / CellBlocks)) - Axis / 2;
                free.World = _world;
                free.Serial = ++_serial;
            }

            _cycleSlot = full ? null : free;
            // Clear before submission so a callback arriving during GL work
            // remains pending rather than being accidentally acknowledged.
            if (full) Interlocked.Exchange(ref _refresh, 0);
            Dispatch(api, free, primary.DepthTextureId, primary.Width, primary.Height, cameraWorld);
            _submittedDepth = primary.DepthTextureId;
            _submittedWidth = primary.Width;
            _submittedHeight = primary.Height;
            _submittedCameraX = cameraWorld.X;
            _submittedCameraY = cameraWorld.Y;
            _submittedCameraZ = cameraWorld.Z;
            _submittedCameraValid = true;
            _cadence.Submitted(now, full);
            if (!full) RegionDispatches++;
            if (_workComplete)
            {
                _cycleSlot = null;
                Submissions++;
                if (immediate) ImmediateSubmissions++;
                else MotionSubmissions++;
            }
        }
        catch (Exception ex)
        {
            _failed = true;
            SetNotReady();
            api.Logger.Warning("[DRT AgX] Receiver demand unavailable; retaining bounded near-receiver admission: {0}",
                ex.Message);
        }
    }

    private void Poll()
    {
        // Render-thread read-only access. A signaled fence makes prior coherent
        // GPU writes visible to this persistent mapping (ARB_buffer_storage).
        // GetBufferSubData can serialize behind unrelated current-frame draws
        // even after this slot's fence signals; polling must never bind/read GL.
        if (PendingCount == 0) return;
        foreach (var slot in _slots)
        {
            if (slot.Fence == IntPtr.Zero) continue;
            var status = GL.ClientWaitSync(slot.Fence, ClientWaitSyncFlags.None, 0);
            if (status == WaitSyncStatus.TimeoutExpired) continue;
            if (status == WaitSyncStatus.WaitFailed)
                throw new InvalidOperationException("Receiver demand fence failed.");
            GL.DeleteSync(slot.Fence);
            slot.Fence = IntPtr.Zero;
            if (slot.Serial <= _acceptedSerial || !ReferenceEquals(slot.World, _world)) continue;
            if (slot.Mapped == IntPtr.Zero)
                throw new InvalidOperationException("Receiver demand feedback is not mapped.");
            // The shader may count more occupied cells than the compact record
            // capacity. Clamp that unsigned header before touching mapped memory.
            uint records = unchecked((uint)Marshal.ReadInt32(slot.Mapped, 3 * sizeof(uint)));
            int words = HeaderWords + WordCount + (int)Math.Min(records, MaxBounds) * 8;
            Marshal.Copy(slot.Mapped, _mappedReadback, 0, words);
            System.Buffer.BlockCopy(_mappedReadback, 0, _readback, 0, words * sizeof(uint));
            Readbacks++;
            Accept(slot);
        }
    }

    private static void EnsureFeedbackSlot(Slot slot)
    {
        if (slot.Buffer != 0) return;
        int buffer = GL.GenBuffer(), readback = GL.GenBuffer();
        try
        {
            GL.BindBuffer(BufferTarget.ShaderStorageBuffer, buffer);
            GL.BufferData(BufferTarget.ShaderStorageBuffer, BufferBytes, IntPtr.Zero, BufferUsageHint.DynamicCopy);
            GL.BindBuffer(BufferTarget.ShaderStorageBuffer, readback);
            // OpenGL 4.4+ is covered by the supported 4.6 hardware baseline.
            // The collector's contended atomics stay in GPU storage. Only the
            // completed GPU copy targets this read-only persistent staging map.
            // Server copies need no DYNAMIC_STORAGE_BIT/client write permission.
            // OpenTK's storage enum omits [Flags]; 0xC1 is the specified union
            // MAP_READ(0x01) | MAP_PERSISTENT(0x40) | MAP_COHERENT(0x80).
            GL.BufferStorage(BufferTarget.ShaderStorageBuffer, BufferBytes, IntPtr.Zero,
                (BufferStorageFlags)0xC1);
            IntPtr mapped = GL.MapBufferRange(BufferTarget.ShaderStorageBuffer, IntPtr.Zero, BufferBytes,
                MapBufferAccessMask.MapReadBit | MapBufferAccessMask.MapPersistentBit |
                MapBufferAccessMask.MapCoherentBit);
            if (mapped == IntPtr.Zero)
                throw new InvalidOperationException("Receiver demand persistent feedback mapping failed.");
            slot.Buffer = buffer;
            slot.ReadbackBuffer = readback;
            slot.Mapped = mapped;
        }
        catch
        {
            GL.DeleteBuffer(buffer);
            GL.DeleteBuffer(readback);
            throw;
        }
    }

    private void EnsureProgram(ICoreClientAPI api)
    {
        if (_program != 0 && _compactProgram != 0) return;
        string source = api.Assets.Get(new AssetLocation("drtagx", "shaders/placedreceiverdemand.csh")).ToText();
        try
        {
            _program = Compile(source);
            _compactProgram =
                Compile(source.Replace("#version 430 core", "#version 430 core\n#define DRT_DEMAND_COMPACT"));
            _depth = GL.GetUniformLocation(_program, "terrainDepth");
            _dimensions = GL.GetUniformLocation(_program, "dimensions");
            _projection = GL.GetUniformLocation(_program, "inverseProjection");
            _view = GL.GetUniformLocation(_program, "inverseViewRotation");
            _camera = GL.GetUniformLocation(_program, "cameraInGrid");
            _pixelOrigin = GL.GetUniformLocation(_program, "pixelOrigin");
            _pixelExtent = GL.GetUniformLocation(_program, "pixelExtent");
        }
        catch
        {
            if (_program != 0) GL.DeleteProgram(_program);
            if (_compactProgram != 0) GL.DeleteProgram(_compactProgram);
            _program = _compactProgram = 0;
            throw;
        }
    }

    private static int Compile(string source)
    {
        int shader = GL.CreateShader(ShaderType.ComputeShader);
        int program = 0;
        try
        {
            GL.ShaderSource(shader, source);
            GL.CompileShader(shader);
            GL.GetShader(shader, ShaderParameter.CompileStatus, out int compiled);
            if (compiled == 0) throw new InvalidOperationException(GL.GetShaderInfoLog(shader));
            program = GL.CreateProgram();
            GL.AttachShader(program, shader);
            GL.LinkProgram(program);
            GL.GetProgram(program, GetProgramParameterName.LinkStatus, out int linked);
            if (linked == 0) throw new InvalidOperationException(GL.GetProgramInfoLog(program));
            return program;
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

    private void Dispatch(ICoreClientAPI api, Slot slot, int depth, int width, int height, Vec3d cameraWorld)
    {
        int program = GL.GetInteger(GetPName.CurrentProgram),
            generic = GL.GetInteger(GetPName.ShaderStorageBufferBinding),
            active = GL.GetInteger(GetPName.ActiveTexture);
        var binding = IndexedBufferState.Capture(BufferRangeTarget.ShaderStorageBuffer, 6);
        var scratchBinding = IndexedBufferState.Capture(BufferRangeTarget.ShaderStorageBuffer, 5);
        GL.ActiveTexture(TextureUnit.Texture13);
        int texture = GL.GetInteger(GetPName.TextureBinding2D), sampler = GL.GetInteger(GetPName.SamplerBinding);
        try
        {
            EnsureProgram(api);
            if (_scratch == 0)
            {
                _scratch = GL.GenBuffer();
                GL.BindBuffer(BufferTarget.ShaderStorageBuffer, _scratch);
                GL.BufferData(BufferTarget.ShaderStorageBuffer, ScratchBytes, IntPtr.Zero, BufferUsageHint.DynamicCopy);
            }

            GL.BindBuffer(BufferTarget.ShaderStorageBuffer, _scratch);
            if (_workBegin)
                GL.ClearBufferData(BufferTarget.ShaderStorageBuffer, PixelInternalFormat.R32ui, PixelFormat.RedInteger,
                    PixelType.UnsignedInt, _zero);
            GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 5, _scratch);
            EnsureFeedbackSlot(slot);

            GL.BindBuffer(BufferTarget.ShaderStorageBuffer, slot.Buffer);
            if (_workBegin)
                GL.ClearBufferData(BufferTarget.ShaderStorageBuffer, PixelInternalFormat.R32ui, PixelFormat.RedInteger,
                    PixelType.UnsignedInt, _zero);
            GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 6, slot.Buffer);
            GL.BindSampler(13, 0);
            GL.BindTexture(TextureTarget.Texture2D, depth);
            GL.UseProgram(_program);
            GL.Uniform1(_depth, 13);
            GL.Uniform2(_dimensions, width, height);
            int x0 = 0, y0 = 0, x1 = width, y1 = height;
            if (_workRegion >= 0)
            {
                int column = _workRegion % 3, row = _workRegion / 3;
                x0 = width * column / 3;
                x1 = width * (column + 1) / 3;
                y0 = height * row / 3;
                y1 = height * (row + 1) / 3;
            }

            GL.Uniform2(_pixelOrigin, x0, y0);
            GL.Uniform2(_pixelExtent, x1 - x0, y1 - y0);
            GL.UniformMatrix4(_projection, 1, false, _inverseProjection);
            GL.UniformMatrix4(_view, 1, false, _rotation);
            // Only small relative coordinates reach float GLSL. Absolute world
            // origin and the bitmap's integer regions remain on the CPU.
            GL.Uniform3(_camera, (float)(cameraWorld.X - slot.X * 8.0),
                (float)(cameraWorld.Y - slot.Y * 8.0), (float)(cameraWorld.Z - slot.Z * 8.0));
            GL.MemoryBarrier(MemoryBarrierFlags.TextureFetchBarrierBit | MemoryBarrierFlags.BufferUpdateBarrierBit);
            GL.DispatchCompute((x1 - x0 + 15) / 16, (y1 - y0 + 15) / 16, 1);
            GL.MemoryBarrier(MemoryBarrierFlags.ShaderStorageBarrierBit);
            // Only a complete cycle or immediate full refresh may compact and
            // copy admission feedback; intermediate tiles remain private.
            if (!_workComplete) return;
            GL.UseProgram(_compactProgram);
            GL.DispatchCompute(Axis * Axis * Axis / 64, 1, 1);
            GL.MemoryBarrier(MemoryBarrierFlags.ShaderStorageBarrierBit | MemoryBarrierFlags.BufferUpdateBarrierBit);
            // The 4.5 named copy preserves native buffer bindings. Coherent
            // mapping plus the following fence makes this staging transfer
            // visible to CPU polling without synchronizing later scene draws.
            GL.CopyNamedBufferSubData(slot.Buffer, slot.ReadbackBuffer, IntPtr.Zero, IntPtr.Zero, BufferBytes);
            slot.Fence = GL.FenceSync(SyncCondition.SyncGpuCommandsComplete, WaitSyncFlags.None);
        }
        finally
        {
            binding.Restore();
            scratchBinding.Restore();
            GL.BindBuffer(BufferTarget.ShaderStorageBuffer, generic);
            GL.BindTexture(TextureTarget.Texture2D, texture);
            GL.BindSampler(13, sampler);
            GL.ActiveTexture((TextureUnit)active);
            GL.UseProgram(program);
        }
    }

    internal void Reload()
    {
        if (_program != 0) GL.DeleteProgram(_program);
        if (_compactProgram != 0) GL.DeleteProgram(_compactProgram);
        _program = _compactProgram = 0;
        _failed = false;
        SnapshotReady = false;
        _acceptedSerial = ++_serial;
        OccupiedRegions = 0;
        _submittedView.Reset();
        _cadence.Reset();
        _cycleSlot = null;
        _projectionValid = _submittedCameraValid = false;
        RequestRefresh();
        Revision++;
        // Old dispatches retain their own immutable slots and are discarded on
        // polling. Reload cannot reinterpret an old result under new shader math.
    }

    internal void Suspend()
    {
        // Idempotent while disabled: no revision/serial/GL work. On resume an
        // immediate full view supersedes any abandoned partial GPU collection.
        _cycleSlot = null;
        _cadence.Reset();
        _submittedView.Reset();
        RequestRefresh();
    }

    public void Dispose()
    {
        Reload();
        // Disposal runs on the render thread. Unmapping ends CPU ownership;
        // OpenGL retains deleted storage until queued GPU users have finished.
        int previous = GL.GetInteger(GetPName.ShaderStorageBufferBinding);
        bool deletedPrevious = false;
        try
        {
            foreach (var slot in _slots)
            {
                if (slot.Fence != IntPtr.Zero) GL.DeleteSync(slot.Fence);
                if (slot.Buffer != 0)
                {
                    deletedPrevious |= previous == slot.Buffer;
                    GL.DeleteBuffer(slot.Buffer);
                }

                if (slot.ReadbackBuffer != 0)
                {
                    deletedPrevious |= previous == slot.ReadbackBuffer;
                    GL.BindBuffer(BufferTarget.ShaderStorageBuffer, slot.ReadbackBuffer);
                    if (slot.Mapped != IntPtr.Zero) GL.UnmapBuffer(BufferTarget.ShaderStorageBuffer);
                    GL.DeleteBuffer(slot.ReadbackBuffer);
                }

                slot.Fence = slot.Mapped = IntPtr.Zero;
                slot.Buffer = slot.ReadbackBuffer = 0;
            }
        }
        finally
        {
            GL.BindBuffer(BufferTarget.ShaderStorageBuffer, deletedPrevious ? 0 : previous);
        }

        if (_scratch != 0) GL.DeleteBuffer(_scratch);
        _scratch = 0;
    }
}