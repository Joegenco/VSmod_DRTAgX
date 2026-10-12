using System;
using System.Collections.Generic;
using HarmonyLib;
using Vintagestory.API.Client;
using EntityPlayer = Vintagestory.API.Common.EntityPlayer;
using Vintagestory.GameContent;

namespace DRTAgX;

// Render-thread-only snapshots of native opaque entity meshes and current poses.
// Matrix preparation runs once per nearby entity, never once per shadow face;
// animation, tessellation and the native opaque renderer are not replayed.
internal sealed class MovingEntityShadowCasters
{
    internal sealed class DrawRecord
    {
        internal MultiTextureMeshRef Mesh = null!;
        internal readonly float[] Model = new float[16];
        internal float[] Matrices = Array.Empty<float>();
        internal int JointCount;
        internal float EyeX, EyeY, EyeZ, Radius;
    }

    private static readonly Func<EntityShapeRenderer, MultiTextureMeshRef?> OpaqueMesh =
        NativeShadowFields.Reader<EntityShapeRenderer, MultiTextureMeshRef>("meshRefOpaque");

    private static readonly Func<EntityPlayerShapeRenderer, MultiTextureMeshRef?> PlayerMesh =
        NativeShadowFields.Reader<EntityPlayerShapeRenderer, MultiTextureMeshRef>("thirdPersonMeshRef");

    private static readonly Func<EntityShapeRenderer, bool> Spectator =
        NativeShadowFields.Reader<EntityShapeRenderer, bool>("isSpectator");

    private static readonly AccessTools.FieldRef<EntityPlayerShapeRenderer, float> PlayerSmoothedYaw =
        AccessTools.FieldRefAccess<EntityPlayerShapeRenderer, float>("smoothedBodyYaw");

    private readonly List<DrawRecord> _records = new();
    private readonly float[] _savedModel = new float[16];
    internal int Count { get; private set; }
    internal DrawRecord this[int index] => _records[index];

    internal void Prepare(ICoreClientAPI api, float[][] lightPositions, int[] slots,
        float[] sourceRanges, int sourceCount, float[] camera)
    {
        End();
        var localPlayer = api.World.Player.Entity;
        var origin = localPlayer.CameraPos;
        bool thirdPerson = api.World.Player.CameraMode != EnumCameraMode.FirstPerson;
        foreach (var entity in api.World.LoadedEntities.Values)
        {
            // First-person hands have a separate mesh/pose and sit beside the
            // light source. Third-person uses the full body as a normal caster.
            bool isSelf = entity.EntityId == localPlayer.EntityId;
            if ((isSelf && !thirdPerson) || entity.Pos.Dimension != localPlayer.Pos.Dimension ||
                entity.Properties?.Client?.Renderer is not EntityShapeRenderer renderer || Spectator(renderer))
                continue;

            var box = entity.SelectionBox;
            if (box == null) continue;
            // Native self rendering starts at the render origin. In third-person
            // the view matrix carries the camera orbit, not the model offset.
            float x = (isSelf ? 0f : (float)(entity.Pos.X - origin.X)) + (box.X1 + box.X2) * .5f;
            float y = (isSelf ? 0f : (float)(entity.Pos.InternalY - origin.Y)) + (box.Y1 + box.Y2) * .5f;
            float z = (isSelf ? 0f : (float)(entity.Pos.Z - origin.Z)) + (box.Z1 + box.Z2) * .5f;
            if (isSelf && localPlayer.MountedOn?.RenderTransform is { } mountTransform)
            {
                // Match the mount transform used by the native self model.
                var m = mountTransform.Values;
                float mx = m[0] * x + m[4] * y + m[8] * z + m[12];
                float my = m[1] * x + m[5] * y + m[9] * z + m[13];
                z = m[2] * x + m[6] * y + m[10] * z + m[14];
                x = mx;
                y = my;
            }

            float ex = camera[0] * x + camera[4] * y + camera[8] * z + camera[12];
            float ey = camera[1] * x + camera[5] * y + camera[9] * z + camera[13];
            float ez = camera[2] * x + camera[6] * y + camera[10] * z + camera[14];
            float dx = (box.X2 - box.X1) * .5f,
                dy = (box.Y2 - box.Y1) * .5f,
                dz = (box.Z2 - box.Z1) * .5f;
            // Selection volumes do not contain every animated wing/limb. This
            // guard keeps those extremities and small mounted offsets eligible.
            float radius = MathF.Sqrt(dx * dx + dy * dy + dz * dz) + 1.5f;
            if (!float.IsFinite(radius + ex + ey + ez) ||
                !NearSelectedLight(ex, ey, ez, radius, lightPositions, slots, sourceRanges, sourceCount))
                continue;

            var playerRenderer = renderer as EntityPlayerShapeRenderer;
            var mesh = playerRenderer != null ? PlayerMesh(playerRenderer) : OpaqueMesh(renderer);
            var animator = entity is EntityPlayer player
                ? player.TpAnimManager?.Animator ?? entity.AnimManager?.Animator
                : entity.AnimManager?.Animator;
            if (mesh == null || mesh.Disposed || !mesh.Initialized ||
                animator?.Matrices is not { } matrices || animator.MaxJointId <= 0 ||
                matrices.Length / 16 < animator.MaxJointId)
                continue;

            if (Count == _records.Count) _records.Add(new DrawRecord());
            var record = _records[Count];
            var originalModel = renderer.ModelMat;
            if (originalModel is not { Length: >= 16 }) continue;
            Array.Copy(originalModel, _savedModel, 16);
            float bodyYaw = renderer.bodyYawLerped;
            float smoothedYaw = playerRenderer != null ? PlayerSmoothedYaw(playerRenderer) : 0f;
            try
            {
                // The native shadow matrix path skips step/bobbing advancement.
                // Players use their full body mesh and third-person pose;
                // never change selfNowShadowPass or tick an animation manager.
                if (playerRenderer != null)
                    playerRenderer.loadModelMatrixForPlayer(entity, isSelf, 0f, true);
                else
                    renderer.loadModelMatrix(entity, 0f, true);
                Array.Copy(renderer.ModelMat, record.Model, 16);
            }
            finally
            {
                renderer.ModelMat = originalModel;
                Array.Copy(_savedModel, originalModel, 16);
                renderer.bodyYawLerped = bodyYaw;
                if (playerRenderer != null) PlayerSmoothedYaw(playerRenderer) = smoothedYaw;
            }

            record.Mesh = mesh;
            record.Matrices = matrices;
            record.JointCount = animator.MaxJointId;
            record.EyeX = ex;
            record.EyeY = ey;
            record.EyeZ = ez;
            record.Radius = radius;
            ++Count;
        }
    }

    private static bool NearSelectedLight(float x, float y, float z, float radius,
        float[][] positions, int[] slots, float[] ranges, int count)
    {
        for (int i = 0; i < count; ++i)
        {
            if (slots[i] < 0) continue;
            var p = positions[i];
            float dx = x - p[0], dy = y - p[1], dz = z - p[2], reach = ranges[i] + radius;
            if (dx * dx + dy * dy + dz * dz <= reach * reach) return true;
        }

        return false;
    }

    internal void End()
    {
        // Keep reusable matrix storage, but release world-owned meshes and bone
        // arrays immediately after drawing so unloaded entities are not retained.
        for (int i = 0; i < Count; ++i)
        {
            _records[i].Mesh = null!;
            _records[i].Matrices = Array.Empty<float>();
            _records[i].JointCount = 0;
        }

        Count = 0;
    }
}