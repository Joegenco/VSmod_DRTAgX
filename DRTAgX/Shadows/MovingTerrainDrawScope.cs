using System;
using System.Collections.Generic;
using HarmonyLib;
using Vintagestory.API.Client;

namespace DRTAgX;

// Supply only our moving-shadow index selection. The native manager still owns
// pool traversal, origin uniforms and RenderMesh, including foreign draw hooks.
internal sealed class MovingTerrainDrawScope : IDisposable
{
    private static readonly Harmony Harmony = new("drtagx.moving-terrain-selection");
    private static int _owners;
    [ThreadStatic] private static MovingTerrainDrawScope? _active;
    private readonly Dictionary<MeshDataPool, MovingTerrainPoolDraw> _pools = new(ReferenceEqualityComparer.Instance);
    private MovingTerrainDrawScope? _previous;
    private bool _initialized, _ownsHook, _open;
    internal bool Available => _ownsHook;

    internal void Initialize()
    {
        if (_initialized) return;
        _initialized = true;
        var method = AccessTools.Method(typeof(MeshDataPool), nameof(MeshDataPool.FrustumCull),
            [typeof(FrustumCulling), typeof(EnumFrustumCullMode)]);
        if (method == null || IntPtr.Size != 8) return;
        // Inspect once at first actual shadow use, after mod startup. Skipping
        // native reculling must not silently remove another mod's cull policy.
        var patches = HarmonyLib.Harmony.GetPatchInfo(method);
        if (patches != null)
            foreach (string owner in patches.Owners)
                if (owner != Harmony.Id)
                    return;
        try
        {
            if (_owners == 0)
                Harmony.Patch(method, prefix: new HarmonyMethod(typeof(MovingTerrainDrawScope), nameof(BeforeCull)));
            ++_owners;
            _ownsHook = true;
        }
        catch
        {
            // Unknown runtime/provider contracts keep the existing native path.
            if (_owners == 0) Harmony.UnpatchAll(Harmony.Id);
        }
    }

    internal void BeginPage()
    {
        EndPage();
        if (!Available) return;
        _previous = _active;
        _active = this;
        _open = true;
    }

    internal void Add(MeshDataPool pool, MovingTerrainPoolDraw draw, int face)
    {
        if (!_open || !draw.Valid) return;
        draw.SelectedFace = face < 0 ? 6 : face;
        _pools[pool] = draw;
    }

    private static bool BeforeCull(MeshDataPool __instance, EnumFrustumCullMode frustumCullMode)
    {
        var scope = _active;
        return scope == null || frustumCullMode != EnumFrustumCullMode.NoCull ||
               !scope._pools.TryGetValue(__instance, out var draw) || !draw.Publish(__instance);
    }

    internal void EndPage()
    {
        if (_open)
        {
            _active = _previous;
            _previous = null;
            _open = false;
        }

        _pools.Clear();
    }

    public void Dispose()
    {
        EndPage();
        if (!_ownsHook) return;
        _ownsHook = false;
        if (--_owners == 0) Harmony.UnpatchAll(Harmony.Id);
    }
}

// Reusable compact draw groups. No native mesh, VAO, pool list, visibility flag
// or index buffer is changed; only the public CPU draw-list references are lent.
internal sealed class MovingTerrainPoolDraw
{
    private readonly int[][] _starts = new int[7][], _sizes = new int[7][];
    private readonly int[] _counts = new int[7], _triangles = new int[7];
    private List<ModelDataPoolLocation>? _locations;
    private int _locationCount, _capturedLocations, _indexCapacity, _allocatedTriangles;
    internal bool Valid { get; private set; }
    internal int SelectedFace;

    internal MovingTerrainPoolDraw()
    {
        for (int face = 0; face < 7; ++face)
            _starts[face] = _sizes[face] = Array.Empty<int>();
    }

    internal void Capture(MeshDataPool pool, int expectedLocations)
    {
        // Native index bounds and total allocation do not vary by light or cube
        // face. Validate once while Begin already visits these same locations.
        _allocatedTriangles = 0;
        _capturedLocations = 0;
        _locations = NativeShadowFields.Locations(pool);
        _locationCount = expectedLocations;
        _indexCapacity = pool.IndicesPoolSize;
        Valid = pool.GetType() == typeof(MeshDataPool) && NativeShadowFields.Dimension(pool) == 0 &&
                _locations?.Count == expectedLocations && expectedLocations >= 0 &&
                pool.indicesStartsByte != null && pool.indicesSizes != null &&
                (long)expectedLocations * 2 <= pool.indicesStartsByte.Length &&
                expectedLocations <= pool.indicesSizes.Length;
    }

    internal void CaptureLocation(ModelDataPoolLocation location)
    {
        if (!Valid) return;
        ++_capturedLocations;
        int start = location.IndicesStart, size = location.IndicesEnd - start;
        if (start < 0 || start > int.MaxValue / sizeof(int) || size < 0 ||
            location.IndicesEnd > _indexCapacity)
        {
            Valid = false;
            return;
        }

        _allocatedTriangles += size / 3;
    }

    internal void Invalidate() => Valid = false;

    internal void Reset()
    {
        Array.Clear(_counts);
        Array.Clear(_triangles);
    }

    internal void Add(ModelDataPoolLocation location, byte faces, bool cubeFaces)
    {
        if (!Valid) return;
        int start = location.IndicesStart, size = location.IndicesEnd - start;
        Append(6, start, size); // Union for the single first-person projector.
        if (!cubeFaces) return;
        for (int face = 0; face < 6; ++face)
            if ((faces & (1 << face)) != 0)
                Append(face, start, size);
    }

    private void Append(int face, int start, int size)
    {
        int count = _counts[face];
        if (count == _sizes[face].Length)
        {
            int capacity = Math.Max(8, count * 2);
            Array.Resize(ref _starts[face], capacity * 2);
            Array.Resize(ref _sizes[face], capacity);
        }

        // Native MultiDrawElements carries one pointer in two int words on the
        // supported 64-bit runtime. Native FrustumCull writes the same low word.
        _starts[face][count * 2] = start * sizeof(int);
        _starts[face][count * 2 + 1] = 0;
        _sizes[face][count] = size;
        _counts[face] = count + 1;
        _triangles[face] += size / 3;
    }

    internal bool Publish(MeshDataPool pool)
    {
        // A synchronous foreign draw callback can change a pool between prepare
        // and submission. Unknown changes conservatively use the native Hide
        // flags: this can submit extra geometry, but cannot drop a new caster.
        if (!Valid || _capturedLocations != _locationCount || _locations?.Count != _locationCount ||
            !ReferenceEquals(NativeShadowFields.Locations(pool), _locations)) return false;
        int face = SelectedFace;
        pool.indicesStartsByte = _starts[face];
        pool.indicesSizes = _sizes[face];
        pool.indicesGroupsCount = _counts[face];
        pool.RenderedTriangles = _triangles[face];
        pool.AllocatedTris = _allocatedTriangles;
        return true;
    }
}