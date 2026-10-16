using System;
using OpenTK.Graphics.OpenGL4;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace DRTAgX;

/// <summary>Draw-only animated casters for the current-frame moving atlas.
/// Bones upload once per frame; faces borrow aligned ranges of our own UBO.</summary>
internal sealed class MovingEntityShadowRenderer : IDisposable
{
    private readonly MovingEntityShadowCasters _casters = new();
    private readonly float[] _matrix = new float[16];
    private float[] _bones = Array.Empty<float>();
    private IShaderProgram? _program;
    private int _nativeId, _buffer, _blockBytes, _strideBytes, _matrixLocation, _heldLocation;
    private bool _warned, _failed, _borrowed;
    private IndexedBufferState _uniform0;

    internal void Prepare(ICoreClientAPI api, float[][] positions, int[] slots, float[] ranges,
        int sourceCount, float[] camera)
    {
        End();
        if (_failed) return; // Retry compatibility/compile failures only on shader reload.
        try
        {
            _casters.Prepare(api, positions, slots, ranges, sourceCount, camera);
            if (_casters.Count == 0) return;

            // Binding 0 belongs to native Animation. Preserve its full range,
            // and never overwrite the engine's buffer or animation matrices.
            _uniform0 = IndexedBufferState.Capture(BufferRangeTarget.UniformBuffer, 0);
            _borrowed = true;
            if (!EnsureProgram(api))
            {
                End();
                return;
            }

            int floats = checked(_casters.Count * (_strideBytes / sizeof(float)));
            if (_bones.Length < floats) _bones = new float[Math.Max(floats, _bones.Length * 2)];
            Array.Clear(_bones, 0, floats);
            for (int i = 0; i < _casters.Count; ++i)
            {
                var caster = _casters[i];
                int length = caster.JointCount * 16;
                // A mod skeleton that exceeds this native shader variant must
                // not read a truncated palette and cast arbitrary geometry.
                if (length <= 0 || length > _blockBytes / sizeof(float) || length > caster.Matrices.Length)
                    continue;
                Array.Copy(caster.Matrices, 0, _bones, i * (_strideBytes / sizeof(float)), length);
            }

            if (_buffer == 0) _buffer = GL.GenBuffer();
            GL.BindBuffer(BufferTarget.UniformBuffer, _buffer);
            // Orphan the previous frame's storage to avoid waiting for its
            // shadows before uploading the current, already-evaluated poses.
            GL.BufferData(BufferTarget.UniformBuffer, (IntPtr)(floats * sizeof(float)), IntPtr.Zero,
                BufferUsageHint.StreamDraw);
            GL.BufferSubData(BufferTarget.UniformBuffer, IntPtr.Zero,
                (IntPtr)(floats * sizeof(float)), _bones);
        }
        catch (Exception ex)
        {
            End();
            if (!_warned)
            {
                api.Logger.Warning("[DRTAgX] Animated moving-shadow casters unavailable: {0}", ex.Message);
                _warned = true;
            }

            _failed = true;
        }
    }

    internal int Draw(ICoreClientAPI api, IShaderProgram terrain, int hand, int face,
        float[] projectionCamera, float[] light, float range)
    {
        if (_casters.Count == 0 || _program == null || !_borrowed) return 0;
        int draws = 0;
        var started = false;
        try
        {
            // Share the current atlas sampler; changing it here would also
            // change filtering on the next terrain face in this same pass.
            for (int i = 0; i < _casters.Count; ++i)
            {
                var caster = _casters[i];
                int length = caster.JointCount * 16;
                if (length <= 0 || length > _blockBytes / sizeof(float) || length > caster.Matrices.Length)
                    continue;
                float x = caster.EyeX - light[0], y = caster.EyeY - light[1], z = caster.EyeZ - light[2];
                float reach = range + caster.Radius;
                if (x * x + y * y + z * z > reach * reach ||
                    (face >= 0 && !TerrainCasterBatch.IntersectsFace(x, y, z, caster.Radius, face))) continue;

                // The frame-wide caster list may only overlap a different
                // light/face. Do not run native shader hooks or change GL state
                // until this projection has an actual animated draw.
                if (!started)
                {
                    started = true;
                    terrain.Stop();
                    _program.Use();
                    GL.Uniform1(_heldLocation, hand);
                    GL.ActiveTexture(TextureUnit.Texture0);
                }

                Mat4f.Mul(_matrix, projectionCamera, caster.Model);
                GL.UniformMatrix4(_matrixLocation, 1, false, _matrix);
                GL.BindBufferRange(BufferRangeTarget.UniformBuffer, 0, _buffer,
                    (IntPtr)(i * _strideBytes), (IntPtr)_blockBytes);
                api.Render.RenderMultiTextureMesh(caster.Mesh, "entityTex", 0);
                draws++;
            }
        }
        finally
        {
            if (started)
            {
                _program.Stop();
                terrain.Use();
            }
        }

        return draws;
    }

    private bool EnsureProgram(ICoreClientAPI api)
    {
        var native = api.Render.GetEngineShader(EnumShaderProgram.Shadowmapentityanimated);
        if (native == null || native.Disposed || native.LoadError) return false;
        if (_nativeId == native.ProgramId && _program?.Disposed != true) return !_failed && _program != null;
        _program?.Dispose();
        _program = null;
        _failed = _warned = false;
        _nativeId = native.ProgramId;
        var program = api.Shader.NewShaderProgram();
        try
        {
            program.VertexShader = api.Shader.NewShader(EnumShaderType.VertexShader);
            program.VertexShader.Code = api.Assets.Get(new AssetLocation("drtagx", "shaders/moving_entity_shadow.vsh"))
                .ToText();
            program.VertexShader.PrefixCode = native.VertexShader.PrefixCode;
            program.FragmentShader = api.Shader.NewShader(EnumShaderType.FragmentShader);
            program.FragmentShader.Code =
                api.Assets.Get(new AssetLocation("drtagx", "shaders/moving_entity_shadow.fsh")).ToText();
            // The shared fragment also serves static PLS container maps; only
            // moving maps enforce the emitter-relative shadow-depth limit.
            program.FragmentShader.PrefixCode = "\n#define DRT_MOVING_SHADOWS 1\n";
            api.Shader.RegisterMemoryShaderProgram("drtagx_moving_entity_shadow", program);
            if (!program.Compile())
                throw new InvalidOperationException("Animated moving-shadow shader failed to compile");
            int block = GL.GetUniformBlockIndex(program.ProgramId, "Animation");
            if (block < 0) throw new InvalidOperationException("Missing animated shadow bone block");
            GL.GetActiveUniformBlock(program.ProgramId, block, ActiveUniformBlockParameter.UniformBlockDataSize,
                out _blockBytes);
            int alignment = GL.GetInteger(GetPName.UniformBufferOffsetAlignment);
            _strideBytes = ((_blockBytes + alignment - 1) / alignment) * alignment;
            _matrixLocation = GL.GetUniformLocation(program.ProgramId, "casterMatrix");
            _heldLocation = GL.GetUniformLocation(program.ProgramId, "heldProjectionIndex");
            _program = program;
            return true;
        }
        catch
        {
            program.Dispose();
            throw;
        }
    }

    internal void End()
    {
        _casters.End();
        if (_borrowed) _uniform0.Restore();
        _borrowed = false;
        // The enclosing ShadowGlState restores the generic UBO binding.
    }

    internal void Reload()
    {
        End();
        _program?.Dispose();
        _program = null;
        _nativeId = 0;
        _failed = _warned = false;
    }

    public void Dispose()
    {
        Reload();
        if (_buffer != 0) GL.DeleteBuffer(_buffer);
        _buffer = 0;
    }
}