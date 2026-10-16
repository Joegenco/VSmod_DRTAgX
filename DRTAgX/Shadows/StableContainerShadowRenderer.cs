using System;
using System.Collections.Generic;
using OpenTK.Graphics.OpenGL4;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace DRTAgX;

// Depth-only rest meshes borrowed from the native container renderer. No extra
// mesh upload, bone palette, animation tick or visible scene draw is performed.
internal sealed class StableContainerShadowRenderer : IDisposable
{
    internal sealed class Caster
    {
        internal MultiTextureMeshRef? Mesh;
        internal readonly float[] Model = new float[16];
        internal byte Faces;
    }

    private readonly List<Caster> _casters = new();
    private readonly float[] _matrix = new float[16];
    private readonly string _name;
    private readonly bool _moving;
    private IShaderProgram? _program;
    private int _count, _matrixLocation, _heldLocation;
    private bool _failed;

    internal StableContainerShadowRenderer(string name, bool moving = false)
    {
        _name = name;
        _moving = moving;
    }

    internal bool Prepare(ICoreClientAPI api, Vec3d origin, Vec3d light, float range,
        float[]? camera, bool excludeEmitter = false)
    {
        End();
        _count = StableContainerRendering.CaptureCasters(_casters, origin, light, range, camera, excludeEmitter,
            out bool ready);
        if (_count == 0) return ready;
        if (_failed) return false;
        if (_program is { Disposed: false }) return ready;
        var program = api.Shader.NewShaderProgram();
        try
        {
            program.VertexShader = api.Shader.NewShader(EnumShaderType.VertexShader);
            program.VertexShader.Code = api.Assets
                .Get(new AssetLocation("drtagx", "shaders/stable_container_shadow.vsh"))
                .ToText();
            // Static maps have no dependency on the moving pass's held-fit UBO.
            program.VertexShader.PrefixCode = _moving ? "\n#define DRT_CONTAINER_MOVING 1\n" : string.Empty;
            program.FragmentShader = api.Shader.NewShader(EnumShaderType.FragmentShader);
            program.FragmentShader.Code = api.Assets
                .Get(new AssetLocation("drtagx", "shaders/moving_entity_shadow.fsh"))
                .ToText();
            program.FragmentShader.PrefixCode = _moving ? "\n#define DRT_MOVING_SHADOWS 1\n" : string.Empty;
            api.Shader.RegisterMemoryShaderProgram(_name, program);
            if (!program.Compile())
                throw new InvalidOperationException("Container rest-shadow shader failed to compile");
            _matrixLocation = GL.GetUniformLocation(program.ProgramId, "casterMatrix");
            _heldLocation = GL.GetUniformLocation(program.ProgramId, "heldProjectionIndex");
            _program = program;
            return ready;
        }
        catch (Exception ex)
        {
            program.Dispose();
            _failed = true;
            api.Logger.Warning("[DRTAgX] Container rest-shadow casters unavailable: {0}", ex.Message);
            return false;
        }
    }

    internal int Draw(ICoreClientAPI api, IShaderProgram terrain, int hand, int face, float[] projectionCamera)
    {
        if (_count == 0 || _failed || _program == null || _program.Disposed) return 0;
        int draws = 0;
        var started = false;
        try
        {
            for (int i = 0; i < _count; i++)
            {
                var caster = _casters[i];
                if (caster.Mesh is not { Disposed: false, Initialized: true } ||
                    face >= 0 && (caster.Faces & (1 << face)) == 0) continue;
                // Empty faces should not invoke native shader hooks. Start on
                // the first surviving mesh, retaining CurrentActiveShader for
                // the public multi-texture draw helper's sampler lookup.
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

        // The enclosing ShadowGlState restores framebuffer, VAO, texture0 and
        // sampler0. TerrainCasterBatch rebinds its own atlas before each draw.
        return draws;
    }

    internal void End()
    {
        for (int i = 0; i < _count; i++) _casters[i].Mesh = null;
        _count = 0;
    }

    internal void Reload()
    {
        End();
        _program?.Dispose();
        _program = null;
        _failed = false;
    }

    public void Dispose() => Reload();
}