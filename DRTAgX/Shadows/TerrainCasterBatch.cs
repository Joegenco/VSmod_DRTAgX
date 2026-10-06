using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using OpenTK.Graphics.OpenGL4;
using Vintagestory.API.Client;
using Vintagestory.API.MathTools;

namespace DRTAgX;

// Render-thread-only view of resident native pools. No geometry copies. A batch
// resolves references once and classifies each caster once per source, across faces.
internal sealed class TerrainCasterBatch
{
    private readonly List<Page> _pages = new();
    private readonly List<Caster> _casters = new();
    private readonly List<Pool> _pools = new();
    private readonly ConditionalWeakTable<MeshDataPool, DrawState> _states = new();
    private readonly Vec3d _origin = new();
    private int _terrainPassLocation = -1;
    private readonly record struct Page(MeshDataPoolManager Manager, int Atlas, int First, int End, int FirstPool, int EndPool, int TerrainPass);
    private readonly record struct Pool(MeshDataPool Native, DrawState State);
    private struct Caster { internal ModelDataPoolLocation Native; internal byte Faces; internal bool Hidden, OtherDimension; }

    internal void Begin(MeshDataPoolManager[][] passes, int[] atlases)
    {
        End();
        try { Add(passes[0], atlases); if (passes.Length > 5 && passes[5] != null) Add(passes[5], atlases); }
        catch { End(); throw; }
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
                if (passes[pass] != null) Add(passes[pass], atlases, pass);
        }
        catch { End(); throw; }
    }

    private void Add(MeshDataPoolManager[] managers, int[] atlases) => Add(managers, atlases, -1);

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
                saved.Capture(pool); _pools.Add(new Pool(pool, saved));
                if (NativeShadowFields.Locations(pool) is not { } locations) continue;
                bool other = NativeShadowFields.Dimension(pool) != 0;
                foreach (var location in locations)
                    _casters.Add(new Caster { Native = location, Hidden = location.Hide, OtherDimension = other });
            }
            _pages.Add(new Page(manager, atlases[i], first, _casters.Count, firstPool, _pools.Count, terrainPass));
        }
    }

    internal void Prepare(Vec3d renderOrigin, Vec3d light, float range, float[]? worldToEye)
    {
        _origin.Set(renderOrigin.X, renderOrigin.Y, renderOrigin.Z);
        for (int i = 0; i < _casters.Count; ++i)
        {
            var c = _casters[i]; var s = c.Native.FrustumCullSphere;
            float radius = Math.Max(s.radius, Math.Max(s.radiusY, s.radiusZ)) + 2f;
            float x = (float)(s.x - light.X), y = (float)(s.y - light.Y), z = (float)(s.z - light.Z);
            c.Faces = 0;
            if (!c.Hidden && !c.OtherDimension && x*x + y*y + z*z <= (range + radius)*(range + radius))
            {
                // Dynamic faces are defined in eye space; placed faces use world
                // axes. Rotation must match the matrix used for rasterization.
                if (worldToEye != null)
                {
                    float ex = worldToEye[0]*x + worldToEye[4]*y + worldToEye[8]*z;
                    float ey = worldToEye[1]*x + worldToEye[5]*y + worldToEye[9]*z;
                    float ez = worldToEye[2]*x + worldToEye[6]*y + worldToEye[10]*z;
                    x=ex; y=ey; z=ez;
                    // Native view columns can differ slightly from unit length.
                    radius *= 1.001f;
                }
                for (int f = 0; f < 6; ++f) if (IntersectsFace(x,y,z,radius,f)) c.Faces |= (byte)(1 << f);
            }
            _casters[i] = c;
        }
    }

    internal static bool IntersectsFace(float x, float y, float z, float radius, int face)
    {
        float forward = face switch { 0 => x, 1 => -x, 2 => y, 3 => -y, 4 => z, _ => -z };
        float a = face < 2 ? Math.Abs(y) : Math.Abs(x), b = face < 4 ? Math.Abs(z) : Math.Abs(y);
        // Plane normals (1, +/- .94, 0) have length sqrt(1+.94^2).
        // Scaling the radius is essential for conservative sphere-plane culling.
        float planeRadius = radius * MathF.Sqrt(1f + .94f*.94f);
        return forward >= -radius && forward - .94f*a >= -planeRadius && forward - .94f*b >= -planeRadius;
    }

    internal int Draw(int face)
    {
        int groups = 0;
        try
        {
            foreach (var page in _pages)
            {
                bool any = false;
                for (int i = page.First; i < page.End; ++i)
                {
                    var c = _casters[i];
                    bool keep = face < 0 ? c.Faces != 0 : (c.Faces & (1 << face)) != 0;
                    c.Native.Hide = !keep; any |= keep;
                }
                if (!any) continue;
                // PLS opt-in only: each pass uses its own alpha coverage and
                // liquids use native unpacked vertices instead of packed quads.
                if (_terrainPassLocation >= 0) GL.Uniform1(_terrainPassLocation, page.TerrainPass);
                GL.ActiveTexture(TextureUnit.Texture0); GL.BindTexture(TextureTarget.Texture2D, page.Atlas);
                page.Manager.Render(_origin, "origin", EnumFrustumCullMode.NoCull);
                for (int p = page.FirstPool; p < page.EndPool; ++p) groups += _pools[p].Native.indicesGroupsCount;
            }
            return groups;
        }
        finally { Restore(); }
    }

    private void Restore()
    {
        foreach (var c in _casters) c.Native.Hide = c.Hidden;
        foreach (var p in _pools) p.State.Restore(p.Native);
    }
    internal void End() { Restore(); _casters.Clear(); _pages.Clear(); _pools.Clear(); _terrainPassLocation = -1; }

    private sealed class DrawState
    {
        private int[] _starts = Array.Empty<int>(), _sizes = Array.Empty<int>();
        private int[] _nativeStarts = Array.Empty<int>(), _nativeSizes = Array.Empty<int>();
        private int _count;
        internal void Capture(MeshDataPool pool)
        {
            _nativeStarts=pool.indicesStartsByte; _nativeSizes=pool.indicesSizes; _count=pool.indicesGroupsCount;
            if (_starts.Length < _count) { _starts = new int[_count]; _sizes = new int[_count]; }
            Array.Copy(_nativeStarts,_starts,_count); Array.Copy(_nativeSizes,_sizes,_count);
        }
        internal void Restore(MeshDataPool pool)
        {
            pool.indicesStartsByte=_nativeStarts; pool.indicesSizes=_nativeSizes; pool.indicesGroupsCount=_count;
            Array.Copy(_starts,_nativeStarts,_count); Array.Copy(_sizes,_nativeSizes,_count);
        }
    }
}
