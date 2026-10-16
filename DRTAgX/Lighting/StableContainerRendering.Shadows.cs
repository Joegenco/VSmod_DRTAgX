using System;
using System.Collections.Generic;
using System.Threading;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace DRTAgX;

internal sealed partial class StableContainerRendering
{
    private static float RestMeshRadius(MeshData? mesh)
    {
        if (mesh?.xyz == null || mesh.VerticesCount <= 0) return 0;
        if (mesh.NeedsRenderPass(EnumChunkRenderPass.Transparent))
        {
            // Mirror native mainThreadInit's exact split predicate. Purely
            // transparent containers keep the original terrain/render policy;
            // an empty opaque MultiTextureMeshRef never reports Initialized.
            bool opaque = false;
            for (int i = 0; i < mesh.RenderPassCount; i++)
                opaque |= mesh.RenderPassesAndExtraBits[i] != (int)EnumChunkRenderPass.Transparent;
            if (!opaque) return 0;
        }

        float radiusSquared = 0;
        int end = Math.Min(mesh.xyz.Length, mesh.VerticesCount * 3);
        for (int i = 0; i + 2 < end; i += 3)
        {
            // Native terrain rotation is around the block centre. A sphere
            // about that centre also bounds the original animated rest mesh.
            float x = mesh.xyz[i] - .5f, y = mesh.xyz[i + 1] - .5f, z = mesh.xyz[i + 2] - .5f;
            radiusSquared = Math.Max(radiusSquared, x * x + y * y + z * z);
        }

        return float.IsFinite(radiusSquared) ? MathF.Sqrt(radiusSquared) + .01f : 0;
    }

    // Only render-thread callers inspect live GPU refs/transforms. Tessellation
    // publishes scalar bounds under the same lock, without retaining MeshData.
    private void RefreshCaster(State state, AnimatableRenderer renderer)
    {
        // Before refreshes all retained models once. A newly completed terrain
        // upload can invalidate this stamp and be picked up by the next shadow
        // capture, without revisiting every model for every light and callback.
        if (Volatile.Read(ref state.RefreshFrame) == _frame) return;
        Volatile.Write(ref state.RefreshFrame, _frame);
        MultiTextureMeshRef? mesh = renderer.mtmeshrefOpaque;
        if (mesh is not { Disposed: false, Initialized: true }) mesh = null;
        float[]? custom = renderer.CustomTransform;
        bool hasCustom = custom is { Length: >= 16 };
        float yaw = renderer.rotationDeg.Y;
        var pos = state.BlockEntity.Pos;
        bool changed = !state.HasTransform || state.X != pos.X || state.Y != pos.InternalY || state.Z != pos.Z ||
                       state.HasCustomTransform != hasCustom;
        if (hasCustom)
        {
            for (int i = 0; i < 16; i++) changed |= state.CustomModel[i] != custom![i];
        }
        else
            changed |= state.Yaw != yaw || state.ScaleX != renderer.ScaleX ||
                       state.ScaleY != renderer.ScaleY || state.ScaleZ != renderer.ScaleZ;

        if (changed)
        {
            // Exact public AnimatableRenderer transform order. Its base model
            // vertices already describe the closed/default pose; no bones or
            // animation evaluation enter these point-light shadow maps.
            float[] model = state.LocalModel;
            Mat4f.Identity(model);
            if (hasCustom)
            {
                Array.Copy(custom!, state.CustomModel, 16);
                Array.Copy(custom!, model, 16);
            }
            else
            {
                Mat4f.Translate(model, model, .5f, 0, .5f);
                Mat4f.Scale(model, model, renderer.ScaleX, renderer.ScaleY, renderer.ScaleZ);
                Mat4f.RotateY(model, model, yaw * GameMath.DEG2RAD);
                Mat4f.Translate(model, model, -.5f, 0, -.5f);
            }

            // Frobenius norm conservatively includes custom shear/nonuniform
            // scale. Ordinary native models use their exact maximum axis scale.
            float scale = hasCustom
                ? MathF.Sqrt(model[0] * model[0] + model[1] * model[1] + model[2] * model[2] +
                             model[4] * model[4] + model[5] * model[5] + model[6] * model[6] +
                             model[8] * model[8] + model[9] * model[9] + model[10] * model[10])
                : Math.Max(Math.Abs(renderer.ScaleX), Math.Max(Math.Abs(renderer.ScaleY), Math.Abs(renderer.ScaleZ)));
            state.Radius = state.RestRadius * scale;
            state.X = pos.X;
            state.Y = pos.InternalY;
            state.Z = pos.Z;
            state.Yaw = yaw;
            state.ScaleX = renderer.ScaleX;
            state.ScaleY = renderer.ScaleY;
            state.ScaleZ = renderer.ScaleZ;
            state.HasTransform = true;
            state.HasCustomTransform = hasCustom;
        }

        bool meshChanged = !ReferenceEquals(mesh, state.ShadowMesh);
        state.ShadowMesh = mesh;
        // A native mesh becoming ready/replaced, or a model transform changing,
        // changes actual blockers. Lid pose deliberately never enters this key.
        if (meshChanged || changed && mesh != null) _casterChanged(pos);
    }

    private void RetireCaster(State state)
    {
        if (state.ShadowMesh == null) return;
        state.ShadowMesh = null;
        _casterChanged(state.BlockEntity.Pos);
    }

    private static void RendererDisposing(AnimatableRenderer __instance)
    {
        var owner = _current;
        if (owner == null) return;
        lock (owner._stateLock)
        {
            if (!owner._renderers.TryGetValue(__instance, out State? state)) return;
            owner._renderers.Remove(__instance);
            if (ReferenceEquals(state.Renderer, __instance))
            {
                owner._utilities.Remove(state.Utility);
                owner.RetireCaster(state);
            }
        }
    }

    internal static int CaptureCasters(List<StableContainerShadowRenderer.Caster> destination,
        Vec3d origin, Vec3d light, float range, float[]? camera, bool excludeEmitter, out bool ready)
    {
        ready = true;
        var owner = _current;
        if (owner == null) return 0;
        int count = 0;
        lock (owner._stateLock)
        {
            // Compact stale weak slots during the same bounded registry scan;
            // exploration must not accumulate work for already-unloaded chunks.
            for (int i = owner._states.Count - 1; i >= 0; i--)
            {
                if (!owner._states[i].TryGetTarget(out State? state) || state.Renderer is not { } renderer ||
                    !owner._renderers.TryGetValue(renderer, out State? mapped) || !ReferenceEquals(state, mapped))
                {
                    owner._states.RemoveAt(i);
                    continue;
                }

                owner.RefreshCaster(state, renderer);
                if (state.BlockEntity.Pos.dimension != 0 || !float.IsFinite(state.Radius)) continue;
                if (excludeEmitter && state.X == Math.Floor(light.X) && state.Y == Math.Floor(light.Y) &&
                    state.Z == Math.Floor(light.Z)) continue;

                float[] model = state.LocalModel;
                float x = (float)(state.X - light.X) + .5f * (model[0] + model[4] + model[8]) + model[12];
                float y = (float)(state.Y - light.Y) + .5f * (model[1] + model[5] + model[9]) + model[13];
                float z = (float)(state.Z - light.Z) + .5f * (model[2] + model[6] + model[10]) + model[14];
                float radius = state.Radius, reach = range + radius;
                if (x * x + y * y + z * z > reach * reach) continue;
                var mesh = state.ShadowMesh;
                if (mesh == null)
                {
                    // A completed upload with no opaque submeshes has no depth
                    // contribution, rather than a permanently pending caster.
                    if (renderer.mtmeshrefOpaque is { Disposed: false } uploaded && uploaded.meshrefs.Length == 0)
                        continue;
                    // A terrain worker can retain a container before native
                    // main-thread upload. Keep previous completed PLS depth
                    // until every nearby rest caster is ready for publication.
                    ready = false;
                    continue;
                }

                if (camera != null)
                {
                    float ex = camera[0] * x + camera[4] * y + camera[8] * z;
                    float ey = camera[1] * x + camera[5] * y + camera[9] * z;
                    float ez = camera[2] * x + camera[6] * y + camera[10] * z;
                    x = ex;
                    y = ey;
                    z = ez;
                    radius *= 1.001f;
                }

                byte faces = 0;
                for (int face = 0; face < 6; face++)
                    if (TerrainCasterBatch.IntersectsFace(x, y, z, radius, face))
                        faces |= (byte)(1 << face);
                if (faces == 0) continue;
                if (count == destination.Count) destination.Add(new StableContainerShadowRenderer.Caster());
                var caster = destination[count++];
                caster.Mesh = mesh;
                caster.Faces = faces;
                Array.Copy(model, caster.Model, 16);
                // Subtract doubles before float conversion, matching the native
                // terrain origin in both held/omni and emitter-relative passes.
                caster.Model[12] += (float)(state.X - origin.X);
                caster.Model[13] += (float)(state.Y - origin.Y);
                caster.Model[14] += (float)(state.Z - origin.Z);
            }
        }

        return count;
    }
}