using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using OpenTK.Graphics.OpenGL4;
using Vintagestory.API.Client;
using Vintagestory.API.MathTools;

namespace DRTAgX;

// Render-thread-only view of resident native pools. No geometry copies. A batch
// resolves references once and classifies each caster once per source, across faces.
internal sealed class TerrainCasterBatch : IDisposable
{
    private readonly List<Page> _pages = new();
    private readonly List<Caster> _casters = new();
    private readonly List<Pool> _pools = new();
    private readonly List<int> _touchedPages = new();
    private readonly ConditionalWeakTable<MeshDataPool, DrawState> _states = new();
    private readonly Vec3d _origin = new();
    private int _terrainPassLocation = -1;
    private readonly MovingTerrainDrawScope? _movingDrawScope;
    private long _batchGeneration;
    private bool _precomputeThisBatch;

    internal TerrainCasterBatch(bool precomputeMovingDraws = false) =>
        _movingDrawScope = precomputeMovingDraws ? new MovingTerrainDrawScope() : null;

    private record struct Page(
        MeshDataPoolManager Manager,
        int Atlas,
        int First,
        int End,
        int FirstPool,
        int EndPool,
        int TerrainPass)
    {
        internal byte Faces;
        internal bool Captured;
    }

    private record struct Pool(MeshDataPool Native, DrawState State, int First, int End)
    {
        internal bool BorrowedHide;
    }

    private struct Caster
    {
        internal ModelDataPoolLocation Native;
        internal int PoolIndex;
        internal byte Faces;
        internal bool Hidden, OtherDimension;
    }

    internal void Begin(MeshDataPoolManager[][] passes, int[] atlases, int terrainPassLocation)
    {
        End();
        _movingDrawScope?.Initialize();
        _precomputeThisBatch = _movingDrawScope?.Available == true;
        ++_batchGeneration;
        _terrainPassLocation = terrainPassLocation;
        try
        {
            Add(passes[0], atlases, 0);
            // VS Roofing and other solid two-sided shapes use OpaqueNoCull.
            // Reuse their resident native pools and the existing per-light
            // culling; transparent glass and liquid pools remain excluded.
            if (passes.Length > 1 && passes[1] != null) Add(passes[1], atlases, 1);
            if (passes.Length > 5 && passes[5] != null) Add(passes[5], atlases, 5);
        }
        catch
        {
            End();
            throw;
        }
    }

    internal void BeginAllTerrainPasses(MeshDataPoolManager[][] passes, int[] atlases, int terrainPassLocation)
    {
        End();
        _terrainPassLocation = terrainPassLocation;
        try
        {
            // Resident chunk pools contain terrain only; entity renderers are
            // never submitted here. Keep native Hide, LOD and dimension rules.
            for (int pass = 0; pass < passes.Length; ++pass)
                if (passes[pass] != null)
                    Add(passes[pass], atlases, pass);
        }
        catch
        {
            End();
            throw;
        }
    }

    private void Add(MeshDataPoolManager[] managers, int[] atlases, int terrainPass)
    {
        for (int i = 0; i < managers.Length && i < atlases.Length; ++i)
        {
            var manager = managers[i];
            if (manager == null || atlases[i] <= 0 || NativeShadowFields.Pools(manager) is not { } pools) continue;
            int first = _casters.Count, firstPool = _pools.Count;
            foreach (var pool in pools)
            {
                var saved = _states.GetValue(pool, static _ => new DrawState());
                var locations = NativeShadowFields.Locations(pool);
                int poolIndex = _pools.Count, poolFirst = _casters.Count;
                bool captureMoving = false;
                if (_precomputeThisBatch)
                {
                    var moving = saved.Moving ??= new MovingTerrainPoolDraw();
                    if (saved.MovingGeneration != _batchGeneration)
                    {
                        saved.MovingGeneration = _batchGeneration;
                        moving.Capture(pool, locations?.Count ?? -1);
                        captureMoving = true;
                    }
                    else if (saved.MovingPage == _pages.Count)
                    {
                        // Preserve repeated entries in a foreign manager's pool
                        // list, but never append them twice to one shared draw list.
                        moving.Invalidate();
                    }

                    saved.MovingPage = _pages.Count;
                }

                _pools.Add(new Pool(pool, saved, poolFirst, poolFirst + (locations?.Count ?? 0)));
                if (locations == null) continue;
                bool other = NativeShadowFields.Dimension(pool) != 0;
                foreach (var location in locations)
                {
                    if (captureMoving) saved.Moving!.CaptureLocation(location);
                    _casters.Add(new Caster
                    {
                        Native = location, PoolIndex = poolIndex,
                        Hidden = location.Hide, OtherDimension = other
                    });
                }
            }

            _pages.Add(new Page(manager, atlases[i], first, _casters.Count, firstPool, _pools.Count, terrainPass));
        }
    }

    internal void Prepare(Vec3d renderOrigin, Vec3d light, float range, float[]? worldToEye,
        bool cubeFaces = true)
    {
        _origin.Set(renderOrigin.X, renderOrigin.Y, renderOrigin.Z);
        for (int pageIndex = 0; pageIndex < _pages.Count; ++pageIndex)
        {
            var page = _pages[pageIndex];
            bool fast = _precomputeThisBatch;
            if (fast)
                for (int p = page.FirstPool; p < page.EndPool; ++p)
                {
                    var pool = _pools[p];
                    pool.State.Moving!.Reset();
                }

            byte pageFaces = 0;
            for (int i = page.First; i < page.End; ++i)
            {
                var c = _casters[i];
                c.Faces = 0;
                if (!c.Hidden && !c.OtherDimension)
                {
                    var s = c.Native.FrustumCullSphere;
                    float radius = Math.Max(s.radius, Math.Max(s.radiusY, s.radiusZ)) + 2f;
                    float x = (float)(s.x - light.X), y = (float)(s.y - light.Y), z = (float)(s.z - light.Z);
                    if (x * x + y * y + z * z <= (range + radius) * (range + radius))
                    {
                        // A held projection draws the union of all six faces.
                        // That union covers every finite in-range sphere, so
                        // it needs neither a rotation nor six cube-plane tests.
                        if (!cubeFaces) c.Faces = 0x3f;
                        else
                        {
                            // Dynamic faces use eye axes; placed faces use world axes.
                            if (worldToEye != null)
                            {
                                float ex = worldToEye[0] * x + worldToEye[4] * y + worldToEye[8] * z;
                                float ey = worldToEye[1] * x + worldToEye[5] * y + worldToEye[9] * z;
                                float ez = worldToEye[2] * x + worldToEye[6] * y + worldToEye[10] * z;
                                x = ex;
                                y = ey;
                                z = ez;
                                radius *= 1.001f; // Preserve conservative native view scaling.
                            }

                            for (int f = 0; f < 6; ++f)
                                if (IntersectsFace(x, y, z, radius, f))
                                    c.Faces |= (byte)(1 << f);
                        }
                    }
                }

                pageFaces |= c.Faces;
                if (fast && c.Faces != 0) _pools[c.PoolIndex].State.Moving!.Add(c.Native, c.Faces, cubeFaces);
                _casters[i] = c;
            }

            page.Faces = pageFaces;
            _pages[pageIndex] = page;
        }
    }

    internal static bool IntersectsFace(float x, float y, float z, float radius, int face)
    {
        float forward = face switch { 0 => x, 1 => -x, 2 => y, 3 => -y, 4 => z, _ => -z };
        float a = face < 2 ? Math.Abs(y) : Math.Abs(x), b = face < 4 ? Math.Abs(z) : Math.Abs(y);
        // Plane normals (1, +/- .94, 0) have length sqrt(1+.94^2).
        // Scaling the radius is essential for conservative sphere-plane culling.
        float planeRadius = radius * MathF.Sqrt(1f + .94f * .94f);
        return forward >= -radius && forward - .94f * a >= -planeRadius && forward - .94f * b >= -planeRadius;
    }

    internal int Draw(int face)
    {
        int groups = 0;
        try
        {
            for (int pageIndex = 0; pageIndex < _pages.Count; ++pageIndex)
            {
                var page = _pages[pageIndex];
                if (face < 0 ? page.Faces == 0 : (page.Faces & (1 << face)) == 0) continue;
                PreparePageDraw(pageIndex, face);
                // Each submitted pass uses its native alpha coverage. Placed
                // liquid casters additionally select their unpacked vertex ABI.
                if (_terrainPassLocation >= 0) GL.Uniform1(_terrainPassLocation, page.TerrainPass);
                GL.ActiveTexture(TextureUnit.Texture0);
                GL.BindTexture(TextureTarget.Texture2D, page.Atlas);
                page.Manager.Render(_origin, "origin", EnumFrustumCullMode.NoCull);
                for (int p = page.FirstPool; p < page.EndPool; ++p) groups += _pools[p].Native.indicesGroupsCount;
                // Finish each borrow before the next page: modded managers can
                // share a pool, and its next capture must see the native state.
                Restore();
            }

            return groups;
        }
        finally
        {
            Restore();
        }
    }

    private void PreparePageDraw(int pageIndex, int face)
    {
        var page = _pages[pageIndex];
        // Borrow native draw arrays only for a page actually submitted. Native
        // Manager.Render and every foreign hook still see the original pool list.
        // Capture once before the page's first mutation, not once per cube
        // face. Each completed draw restores this same native baseline.
        if (!page.Captured)
        {
            for (int p = page.FirstPool; p < page.EndPool; ++p)
                _pools[p].State.Capture(_pools[p].Native);
            page.Captured = true;
            _pages[pageIndex] = page;
        }

        _touchedPages.Add(pageIndex);
        if (_precomputeThisBatch) _movingDrawScope!.BeginPage();
        for (int p = page.FirstPool; p < page.EndPool; ++p)
        {
            var pool = _pools[p];
            if (_precomputeThisBatch && pool.State.Moving is { Valid: true } moving)
            {
                _movingDrawScope!.Add(pool.Native, moving, face);
                continue;
            }

            // Supported pools skip this entire per-face location walk. Only
            // fallback/native pools borrow visibility, as the original path did.
            pool.BorrowedHide = true;
            _pools[p] = pool;
            for (int i = pool.First; i < pool.End; ++i)
            {
                var c = _casters[i];
                c.Native.Hide = face < 0 ? c.Faces == 0 : (c.Faces & (1 << face)) == 0;
            }
        }
    }

    private void Restore()
    {
        _movingDrawScope?.EndPage();
        // Draw's finally restores each borrowed page, even if a foreign native
        // draw hook throws. End then has nothing to restore a second time.
        foreach (int pageIndex in _touchedPages)
        {
            var page = _pages[pageIndex];
            for (int p = page.FirstPool; p < page.EndPool; ++p)
            {
                var pool = _pools[p];
                if (pool.BorrowedHide)
                {
                    for (int i = pool.First; i < pool.End; ++i)
                        _casters[i].Native.Hide = _casters[i].Hidden;
                    pool.BorrowedHide = false;
                    _pools[p] = pool;
                }

                // Prepared pools never changed Hide, so their scope restores
                // only borrowed arrays/statistics, leaving foreign flags alone.
                pool.State.Restore(pool.Native);
            }
        }

        _touchedPages.Clear();
    }

    internal void End()
    {
        Restore();
        _casters.Clear();
        _pages.Clear();
        _pools.Clear();
        _terrainPassLocation = -1;
        _precomputeThisBatch = false;
    }

    public void Dispose()
    {
        End();
        _movingDrawScope?.Dispose();
    }

    private sealed class DrawState
    {
        private int[] _starts = Array.Empty<int>(), _sizes = Array.Empty<int>();
        private int[] _nativeStarts = Array.Empty<int>(), _nativeSizes = Array.Empty<int>();
        private int _count, _renderedTriangles, _allocatedTriangles;
        internal MovingTerrainPoolDraw? Moving;
        internal long MovingGeneration;
        internal int MovingPage;

        internal void Capture(MeshDataPool pool)
        {
            _nativeStarts = pool.indicesStartsByte;
            _nativeSizes = pool.indicesSizes;
            _count = pool.indicesGroupsCount;
            _renderedTriangles = pool.RenderedTriangles;
            _allocatedTriangles = pool.AllocatedTris;
            int startWords = checked(_count * 2);
            if (_starts.Length < startWords)
            {
                _starts = new int[startWords];
                _sizes = new int[_count];
            }

            Array.Copy(_nativeStarts, _starts, startWords);
            Array.Copy(_nativeSizes, _sizes, _count);
        }

        internal void Restore(MeshDataPool pool)
        {
            pool.indicesStartsByte = _nativeStarts;
            pool.indicesSizes = _nativeSizes;
            pool.indicesGroupsCount = _count;
            pool.RenderedTriangles = _renderedTriangles;
            pool.AllocatedTris = _allocatedTriangles;
            Array.Copy(_starts, _nativeStarts, checked(_count * 2));
            Array.Copy(_sizes, _nativeSizes, _count);
        }
    }
}