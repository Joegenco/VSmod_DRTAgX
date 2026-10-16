using System;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.Client.NoObf;

namespace DRTAgX;

/// <summary>Preserve native sky access where whole-voxel sun values cannot describe thin geometry.</summary>
internal sealed class SheyderSunGeometryCompatibility : IDisposable
{
    private const string PatchId = "drtagx.sun.partial-geometry";
    private readonly Harmony _harmony = new(PatchId);
    private static Func<bool>? _directionalSun;
    private static AccessTools.FieldRef<ChunkTesselator, int[]>? _lightField;
    private static AccessTools.FieldRef<ChunkTesselator, Block[]>? _blockField;
    private static Type? _microBlock, _roofBlock;
    private readonly SheyderRoofLightCompatibility? _roofLighting;

    // A worker owns this small scratch buffer, reused across chunk rebuilds.
    // No frame hook, block-entity reads, or changes to native world light data.
    [ThreadStatic] private static SunGeometryLightBuffer? _buffer;

    internal SheyderSunGeometryCompatibility(ICoreClientAPI api)
    {
        object? system = api.ModLoader.GetModSystem("SheyderMod.SheyderModSystem");
        if (system == null) return;
        try
        {
            var assembly = system.GetType().Assembly;
            var patchType = assembly.GetType("SheyderMod.Features.BlockLight.Patches.TesselationLightPatch", true)!;
            var gates = assembly.GetType("SheyderMod.Features.BlockLight.BlockLightGates", true)!;
            var target = AccessTools.DeclaredMethod(patchType, "Postfix")
                         ?? throw new MissingMethodException(patchType.FullName, "Postfix");
            _directionalSun = AccessTools.PropertyGetter(gates, "DirectionalSun").CreateDelegate<Func<bool>>();
            _lightField = AccessTools.FieldRefAccess<ChunkTesselator, int[]>("currentChunkRgbsExt");
            _blockField = AccessTools.FieldRefAccess<ChunkTesselator, Block[]>("currentChunkBlocksExt");
            _microBlock = AccessTools.TypeByName("Vintagestory.GameContent.BlockMicroBlock");
            _roofBlock = api.ModLoader.IsModEnabled("vsroofing") ? AccessTools.TypeByName("VSRoofing.RoofBlock") : null;
            _harmony.Patch(target,
                prefix: new HarmonyMethod(typeof(SheyderSunGeometryCompatibility), nameof(BeforeOverride)),
                finalizer: new HarmonyMethod(typeof(SheyderSunGeometryCompatibility), nameof(AfterOverride)));
            if (_roofBlock != null) _roofLighting = new SheyderRoofLightCompatibility(api, assembly, _roofBlock);

            api.Logger.Notification(
                "[DRT AgX] Directional sun preserves sky access around chiseled geometry.");
        }
        catch (Exception e)
        {
            Dispose();
            api.Logger.Warning("[DRT AgX] Partial-geometry sun compatibility unavailable: " + e.Message);
        }
    }

    // __0 and __4 are the original static patch's ChunkTesselator and success
    // arguments. __instance/__result would refer to our Harmony target itself.
    private static void BeforeOverride(ChunkTesselator __0, bool __4, out SunGeometryLightBuffer? __state)
    {
        __state = null;
        var lightField = _lightField;
        var blockField = _blockField;
        if (!__4 || _directionalSun?.Invoke() != true || lightField == null || blockField == null) return;
        var lights = lightField(__0);
        var blocks = blockField(__0);
        if (lights.Length < SunGeometryLightBuffer.Volume || blocks.Length < SunGeometryLightBuffer.Volume) return;
        var buffer = _buffer ??= new SunGeometryLightBuffer();
        buffer.Begin(lights);
        for (var i = 0; i < SunGeometryLightBuffer.Volume; i++)
        {
            Block? block = blocks[i];
            if (block == null || block.Id == 0) continue;
            // Roofs deliberately use Sheyder's directional bake: restoring native
            // sunlight here would undo the roof light barrier below them.
            if (_microBlock?.IsInstanceOfType(block) == true && _roofBlock?.IsInstanceOfType(block) != true)
                buffer.PreserveNeighborhood(i);
        }

        __state = buffer;
    }

    private static void AfterOverride(SunGeometryLightBuffer? __state) => __state?.RestoreSunlight();

    public void Dispose()
    {
        _roofLighting?.Dispose();
        _harmony.UnpatchAll(PatchId);
        _directionalSun = null;
        _lightField = null;
        _blockField = null;
        _microBlock = _roofBlock = null;
        _buffer = null;
    }
}

/// <summary>Restore only sky metadata at a partial receiver's smooth-light sample footprint.</summary>
internal sealed class SunGeometryLightBuffer
{
    internal const int Size = 34; // Native 32-cubed chunk plus a one-cell neighbour skirt.
    internal const int Volume = Size * Size * Size;
    private readonly int[] _native = new int[Volume];
    private readonly bool[] _preserve = new bool[Volume];
    private int[]? _source;

    internal void Begin(int[] source)
    {
        _source = source;
        Array.Copy(source, _native, Volume);
        Array.Clear(_preserve);
    }

    internal void PreserveNeighborhood(int index)
    {
        var x = index % Size;
        var z = index / Size % Size;
        var y = index / (Size * Size);
        // Include the corner samples used by native smooth lighting. Clamping
        // each axis prevents a halo at x=0 from wrapping into the previous row.
        for (var yy = Math.Max(0, y - 1); yy <= Math.Min(Size - 1, y + 1); yy++)
        for (var zz = Math.Max(0, z - 1); zz <= Math.Min(Size - 1, z + 1); zz++)
        for (var xx = Math.Max(0, x - 1); xx <= Math.Min(Size - 1, x + 1); xx++)
            _preserve[(yy * Size + zz) * Size + xx] = true;
    }

    internal void RestoreSunlight()
    {
        var source = _source;
        _source = null;
        if (source == null) return;
        // CSM casts the actual chisel mesh; a second, block-centre sun
        // reduction causes squares on its exposed faces. Keep native sky access
        // here, retaining Sheyder's RGB and directional bake everywhere else.
        // Sunlight occupies bits 24..31, block RGB bits 0..23. Mutate the existing
        // worker-owned array before TCTCache builds any per-face light samples.
        for (var i = 0; i < Volume; i++)
            if (_preserve[i])
                source[i] = (_native[i] & unchecked((int)0xff000000)) | (source[i] & 0x00ffffff);
    }
}