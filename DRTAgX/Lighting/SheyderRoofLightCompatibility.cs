using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace DRTAgX;

/// <summary>Use covered VS Roofing cells as solid barriers in Sheyder's light bake only.</summary>
internal sealed class SheyderRoofLightCompatibility : IDisposable
{
    private const string PatchId = "drtagx.sheyder.roof-light";
    private readonly Harmony _harmony = new(PatchId);
    private static readonly MethodInfo NativeAbsorption = AccessTools.Method(typeof(IWorldChunk),
        nameof(IWorldChunk.GetLightAbsorptionAt), new[] { typeof(int), typeof(BlockPos), typeof(IList<Block>) });
    private static Type? _roofType;
    private static FieldInfo? _profileShapes;
    private static object? _partialShape, _fullShape;
    private static ConditionalWeakTable<IList<Block>, byte[]> _profiles = new();
    // Mirror Sheyder's registry lifetime: one immutable classification per block
    // list, with a worker-local last-list cache to avoid dictionary work per ray.
    [ThreadStatic] private static IList<Block>? _lastBlocks;
    [ThreadStatic] private static byte[]? _lastProfile;

    internal SheyderRoofLightCompatibility(ICoreClientAPI api, Assembly assembly, Type roofType)
    {
        try
        {
            _roofType = roofType;
            var profiles = assembly.GetType("SheyderMod.Features.BlockLight.Rays.BlockProfiles", true)!;
            var build = AccessTools.DeclaredMethod(profiles, "Build", new[] { typeof(IList<Block>) })
                        ?? throw new MissingMethodException(profiles.FullName, "Build");
            _profileShapes = AccessTools.DeclaredField(profiles, "Shapes");
            Type? shape = _profileShapes?.FieldType.GetElementType();
            if (build.ReturnType != profiles || shape is not { IsEnum: true })
                throw new MissingMemberException("Sheyder BlockProfiles.Shapes enum array");
            _partialShape = Enum.Parse(shape, "Partial");
            _fullShape = Enum.Parse(shape, "FullCube");
            var occlusion = assembly.GetType("SheyderMod.Features.BlockLight.Rays.Occlusion", true)!;
            foreach (string name in new[] { "BlocksRay", "AbsorptionEighths" })
            {
                var method = AccessTools.DeclaredMethod(occlusion, name)
                             ?? throw new MissingMethodException(occlusion.FullName, name);
                _harmony.Patch(method,
                    transpiler: new HarmonyMethod(typeof(SheyderRoofLightCompatibility), nameof(UseRoofAbsorption)));
            }
            _harmony.Patch(build,
                postfix: new HarmonyMethod(typeof(SheyderRoofLightCompatibility), nameof(AfterProfiles)));
            api.Logger.Notification("[DRT AgX] Sheyder block/sun light treats covered VS Roofing cells as solid; bare frames stay open.");
        }
        catch (Exception e)
        {
            Dispose();
            api.Logger.Warning("[DRT AgX] VS Roofing baked-light compatibility unavailable: " + e.Message);
        }
    }

    private static byte[] RoofProfiles(IList<Block> blocks)
    {
        if (ReferenceEquals(_lastBlocks, blocks) && _lastProfile?.Length == blocks.Count) return _lastProfile;
        if (!_profiles.TryGetValue(blocks, out var profile) || profile.Length != blocks.Count)
        {
            // Registries are fixed during rendering. Replace only if the dependency
            // rebuilds after a registry extension, retaining its own growth contract.
            profile = new byte[blocks.Count];
            for (int i = 0; i < profile.Length; ++i)
            {
                Block? block = blocks[i];
                if (block?.Code?.Domain != "vsroofing" || _roofType?.IsInstanceOfType(block) != true) continue;
                profile[i] = block.Variant["type"] switch { "bare" => 1, "filled" => 2, _ => 0 };
            }
            _profiles.AddOrUpdate(blocks, profile);
        }
        _lastBlocks = blocks;
        return _lastProfile = profile;
    }

    private static void AfterProfiles(IList<Block> __0, object __result)
    {
        if (_profileShapes?.GetValue(__result) is not Array shapes) return;
        byte[] roofs = RoofProfiles(__0);
        // Installed roof.json defines bare/filled for every orientation. Covered
        // cells close the whole voxel; bare frames retain their partial geometry.
        for (int i = 0, count = Math.Min(roofs.Length, shapes.Length); i < count; ++i)
            if (roofs[i] != 0) shapes.SetValue(roofs[i] == 2 ? _fullShape : _partialShape, i);
    }

    private static IEnumerable<CodeInstruction> UseRoofAbsorption(IEnumerable<CodeInstruction> instructions)
    {
        var replacement = AccessTools.DeclaredMethod(typeof(SheyderRoofLightCompatibility), nameof(RoofAbsorption));
        int replacements = 0;
        var result = new List<CodeInstruction>();
        foreach (var instruction in instructions)
        {
            if (instruction.Calls(NativeAbsorption))
            {
                // Preserve stack arguments, branch labels and exception markers.
                // The replacement is confined to Sheyder's two bake functions;
                // native propagation, rooms, collision and other mods are untouched.
                instruction.opcode = OpCodes.Call;
                instruction.operand = replacement;
                ++replacements;
            }
            result.Add(instruction);
        }
        if (replacements != 1)
            throw new InvalidOperationException("Sheyder occlusion absorption contract changed.");
        return result;
    }

    private static int RoofAbsorption(IWorldChunk chunk, int index, BlockPos position, IList<Block> blocks)
    {
        byte[] roofs = RoofProfiles(blocks);
        int id = chunk.Data[index];
        // 99 is roof-filled's authored opaque absorption, including roof materials
        // that normally override it. Both sun and block-light rays use this lookup.
        if ((uint)id < (uint)roofs.Length && roofs[id] == 2) return 99;
        return chunk.GetLightAbsorptionAt(index, position, blocks);
    }

    public void Dispose()
    {
        _harmony.UnpatchAll(PatchId);
        _roofType = null;
        _profileShapes = null;
        _partialShape = _fullShape = null;
        _profiles = new();
        _lastBlocks = null;
        _lastProfile = null;
    }
}
