using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using OpenTK.Graphics.OpenGL4;
using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace DRTAgX;

internal sealed partial class LeafMaterialBindings
{
    private readonly object _atlasGate = new();
    private readonly List<LeafMaterialMask.Rectangle> _retired = new();
    private int _generation, _readyGeneration = -1, _attemptedGeneration = -1, _pendingMutations;
    private bool _atlasLoaded, _mutationHooksReady;

    private void StartAtlasLifetime()
    {
        _api.Event.BlockTexturesLoaded += OnAtlasReload;
        _api.Event.ReloadTextures += OnAtlasReload;
        _api.Event.ReloadShader += ReloadShader;
        try
        {
            // Resolve only implementations of verified public API contracts.
            // No assumptions about the private atlas class or mesh layout.
            var map = _api.BlockTextureAtlas.GetType().GetInterfaceMap(typeof(ITextureAtlasAPI));
            var patched = new HashSet<MethodInfo>();
            int allocationHooks = 0, freeHooks = 0, expectedFreeHooks = 0;
            for (int i = 0; i < map.InterfaceMethods.Length; ++i)
            {
                string name = map.InterfaceMethods[i].Name;
                if (name == nameof(ITextureAtlasAPI.FreeTextureSpace)) ++expectedFreeHooks;
                if (name is not (nameof(ITextureAtlasAPI.AllocateTextureSpace)
                    or nameof(ITextureAtlasAPI.FreeTextureSpace))) continue;
                if (!patched.Add(map.TargetMethods[i])) continue;
                _harmony.Patch(map.TargetMethods[i], prefix: new HarmonyMethod(typeof(LeafMaterialBindings),
                        name == nameof(ITextureAtlasAPI.FreeTextureSpace)
                            ? nameof(BeforeFree)
                            : nameof(BeforeAllocate)),
                    finalizer: new HarmonyMethod(typeof(LeafMaterialBindings), nameof(AfterMutation)));
                if (name == nameof(ITextureAtlasAPI.FreeTextureSpace)) ++freeHooks;
                else ++allocationHooks;
            }

            _mutationHooksReady = allocationHooks > 0 && expectedFreeHooks > 0 && freeHooks == expectedFreeHooks;
        }
        catch (Exception error)
        {
            // An unknown atlas provider retains ordinary shading rather than
            // risk stale material labels after texture space gets reused.
            _api.Logger.Warning("[DRT AgX] Leaf material atlas disabled: {0}", error.Message);
        }
    }

    private static void BeforeAllocate(object __instance, out LeafMaterialBindings? __state)
    {
        var owner = _instance;
        __state = owner != null && ReferenceEquals(__instance, owner._api.BlockTextureAtlas) ? owner : null;
        if (__state == null) return;
        Interlocked.Increment(ref __state._pendingMutations);
        Interlocked.Increment(ref __state._generation);
    }

    private static void BeforeFree(object __instance, object __0, out LeafMaterialBindings? __state)
    {
        BeforeAllocate(__instance, out __state);
        var owner = __state;
        if (owner == null) return;
        try
        {
            ITextureAtlasAPI atlas = owner._api.BlockTextureAtlas;
            var positions = atlas.Positions;
            TextureAtlasPosition? position = __0 switch
            {
                int id when positions != null && (uint)id < (uint)positions.Length => positions[id],
                AssetLocation location => atlas[location],
                _ => null
            };
            lock (owner._atlasGate)
            {
                // Old block definitions may retain a freed sub-id. Never mark its
                // replacement as leaves, even if the atlas reuses its GL texture ID.
                if (position != null) owner._retired.Add(LeafMaterialMask.Rectangle.From(position));
            }
        }
        catch (Exception error)
        {
            // An optional classification lookup must not break another atlas
            // provider's own Free operation. Retain ordinary terrain shading.
            owner._mutationHooksReady = false;
            owner._api.Logger.Warning("[DRT AgX] Leaf atlas invalidation disabled: {0}", error.Message);
        }
    }

    private static void AfterMutation(LeafMaterialBindings? __state)
    {
        if (__state == null) return;
        // A finalizer closes the epoch even when a provider throws. Public
        // atlas APIs do not promise main-thread-only callers: never publish a
        // build taken during an allocation/free operation on another thread.
        Interlocked.Increment(ref __state._generation);
        Interlocked.Decrement(ref __state._pendingMutations);
    }

    private void OnAtlasReload()
    {
        lock (_atlasGate)
        {
            _retired.Clear();
            _atlasLoaded = true;
            Interlocked.Increment(ref _generation);
        }
    }

    // Called from the existing Before stage. Stable frames only compare two
    // integers; material enumeration and upload happen after atlas mutations.
    internal void BeforeFrame()
    {
        int generation = Volatile.Read(ref _generation);
        if (!_atlasLoaded || !_mutationHooksReady || generation == _attemptedGeneration ||
            Volatile.Read(ref _pendingMutations) != 0) return;
        _attemptedGeneration = generation;
        Restore();
        _readyGeneration = -1;
        try
        {
            LeafMaterialMask next;
            lock (_atlasGate) next = LeafMaterialMask.Build(_api.World.Blocks, _api.BlockTextureAtlas, _retired);
            long bytes = (long)next.Words.Length * sizeof(uint);
            // GL_MAX_SHADER_STORAGE_BLOCK_SIZE is absent from bundled GetPName.
            GL.GetInteger64((GetPName)0x90DE, out long maximumBytes);
            if (bytes > maximumBytes) return;
            int generic = GL.GetInteger(GetPName.ShaderStorageBufferBinding);
            try
            {
                if (_buffer == 0) _buffer = GL.GenBuffer();
                GL.BindBuffer(BufferTarget.ShaderStorageBuffer, _buffer);
                GL.BufferData(BufferTarget.ShaderStorageBuffer, (IntPtr)bytes, next.Words, BufferUsageHint.StaticDraw);
            }
            finally
            {
                GL.BindBuffer(BufferTarget.ShaderStorageBuffer, generic);
            }

            _mask = next;
            if (generation == Volatile.Read(ref _generation) && Volatile.Read(ref _pendingMutations) == 0)
                _readyGeneration = generation;
        }
        catch (Exception error)
        {
            _api.Logger.Warning("[DRT AgX] Leaf material atlas skipped: {0}", error.Message);
        }
    }
}