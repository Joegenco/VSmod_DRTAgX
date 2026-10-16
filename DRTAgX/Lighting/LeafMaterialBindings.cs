using System;
using System.Collections.Generic;
using System.Threading;
using HarmonyLib;
using OpenTK.Graphics.OpenGL4;
using Vintagestory.API.Client;
using Vintagestory.Client.NoObf;

namespace DRTAgX;

/// <summary>Scoped, read-only leaf material metadata for native terrain draws.</summary>
internal sealed partial class LeafMaterialBindings : IDisposable
{
    private const int BufferBinding = 2; // Native FaceData owns SSBO 3; PLS owns 4..7.
    private static LeafMaterialBindings? _instance;
    private readonly ICoreClientAPI _api;
    private readonly Harmony _harmony = new("drtagx.leaf-material");
    private readonly Dictionary<int, int> _locations = new();
    private LeafMaterialMask _mask = new();
    private int _buffer, _program, _location = -1, _atlasTexture;
    private bool _bound;
    private IndexedBufferState _previous;

    internal LeafMaterialBindings(ICoreClientAPI api)
    {
        _api = api;
        _instance = this;
        try
        {
            var use = AccessTools.Method(typeof(ShaderProgramBase), nameof(IShaderProgram.Use), Type.EmptyTypes);
            var stop = AccessTools.Method(typeof(ShaderProgramBase), nameof(IShaderProgram.Stop), Type.EmptyTypes);
            var bind = AccessTools.Method(typeof(ShaderProgramBase), nameof(IShaderProgram.BindTexture2D),
                [typeof(string), typeof(int), typeof(int)]);
            if (use == null || stop == null || bind == null)
                throw new MissingMethodException("Native terrain shader bindings");
            _harmony.Patch(use, postfix: new HarmonyMethod(typeof(LeafMaterialBindings), nameof(AfterUse)));
            _harmony.Patch(stop, prefix: new HarmonyMethod(typeof(LeafMaterialBindings), nameof(BeforeStop)));
            _harmony.Patch(bind, postfix: new HarmonyMethod(typeof(LeafMaterialBindings), nameof(AfterTexture)));
            StartAtlasLifetime();
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    private static void AfterUse(ShaderProgramBase __instance)
    {
        var owner = _instance;
        if (owner == null) return;
        owner.Restore();
        if (__instance is ShaderProgramChunkopaque) owner.Bind(__instance.ProgramId);
    }

    private static void BeforeStop() => _instance?.Restore();

    private static void AfterTexture(ShaderProgramBase __instance, int textureId, int textureNumber)
    {
        var owner = _instance;
        if (owner != null && textureNumber == 0 && __instance.ProgramId == owner._program)
            owner.SelectAtlas(textureId);
    }

    private void Bind(int program)
    {
        if (!_locations.TryGetValue(program, out int location))
            _locations[program] = location = GL.GetUniformLocation(program, "drtLeafAtlasInfo");
        if (location < 0) return; // An unmodified/foreign shader has no owned metadata ABI.
        _program = program;
        _location = location;
        _atlasTexture = -1;
        GL.Uniform4(location, 0, 0, 0, 0);
        if (_buffer == 0 || _mask.Pages.Count == 0 || _readyGeneration != Volatile.Read(ref _generation) ||
            Volatile.Read(ref _pendingMutations) != 0) return;
        int generic = GL.GetInteger(GetPName.ShaderStorageBufferBinding);
        _previous = IndexedBufferState.Capture(BufferRangeTarget.ShaderStorageBuffer, BufferBinding);
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, BufferBinding, _buffer);
        // BindBufferBase also changes the generic binding. We only borrow the
        // indexed slot, so do not extend that mutation across other mod hooks.
        GL.BindBuffer(BufferTarget.ShaderStorageBuffer, generic);
        _bound = true;
    }

    private void SelectAtlas(int texture)
    {
        bool ready = _bound && _readyGeneration == Volatile.Read(ref _generation) &&
                     Volatile.Read(ref _pendingMutations) == 0;
        if (ready && texture == _atlasTexture) return;
        _atlasTexture = texture;
        if (ready && _mask.Pages.TryGetValue(texture, out var page))
            GL.ProgramUniform4(_program, _location, page.Width, page.Height, page.WordOffset, 1);
        else GL.ProgramUniform4(_program, _location, 0, 0, 0, 0);
    }

    private void Restore()
    {
        // An external raw-GL user of the native terrain program must not see a
        // ready flag after our indexed-buffer lifetime has ended.
        if (_program != 0 && _location >= 0) GL.ProgramUniform4(_program, _location, 0, 0, 0, 0);
        if (_bound)
        {
            int generic = GL.GetInteger(GetPName.ShaderStorageBufferBinding);
            _previous.Restore();
            GL.BindBuffer(BufferTarget.ShaderStorageBuffer, generic);
            _bound = false;
        }

        _program = 0;
        _location = -1;
    }

    private bool ReloadShader()
    {
        Restore();
        _locations.Clear();
        return true;
    }

    public void Dispose()
    {
        Restore();
        _api.Event.BlockTexturesLoaded -= OnAtlasReload;
        _api.Event.ReloadTextures -= OnAtlasReload;
        _api.Event.ReloadShader -= ReloadShader;
        _harmony.UnpatchAll(_harmony.Id);
        if (_buffer != 0) GL.DeleteBuffer(_buffer);
        _buffer = 0;
        _locations.Clear();
        if (_instance == this) _instance = null;
    }
}