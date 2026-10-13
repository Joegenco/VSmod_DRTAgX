using System;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace DRTAgX;

// Current-frame third-person attachment positions. Native item draw matrices are
// shared between hands and arrive later than deferred lighting, so reading them
// would lag one frame. Compose the current prepared pose without replaying a draw
// or advancing animation. The local first-person source keeps its stable anchor.
internal sealed class MovingPlayerLightAnchors
{
    private static readonly AccessTools.FieldRef<EntityPlayerShapeRenderer, float> SmoothedYaw =
        AccessTools.FieldRefAccess<EntityPlayerShapeRenderer, float>("smoothedBodyYaw");

    private readonly float[] _saved = new float[16], _model = new float[16];
    private readonly Matrixf _item = new();
    private long _preparedId;
    private bool _prepared;

    internal void Begin() => _prepared = false;

    internal bool TryHand(ICoreClientAPI api, EntityPlayer player, bool right, float[] eye)
    {
        var stack = (right ? player.RightHandItemSlot : player.LeftHandItemSlot)?.Itemstack;
        var pose = player.TpAnimManager?.Animator?.GetAttachmentPointPose(right ? "RightHand" : "LeftHand");
        var collectible = stack?.Collectible;
        var transform = right
            ? collectible?.TpHandTransform
            : collectible?.TpOffHandTransform ?? collectible?.TpHandTransform;
        if (collectible == null || pose?.AttachPoint == null || pose.AnimModelMatrix is not { Length: >= 16 } ||
            transform == null ||
            player.Properties?.Client?.Renderer is not EntityPlayerShapeRenderer renderer)
            return false;
        try
        {
            if (!_prepared || _preparedId != player.EntityId)
            {
                var original = renderer.ModelMat;
                if (original is not { Length: >= 16 }) return false;
                Array.Copy(original, _saved, 16);
                float bodyYaw = renderer.bodyYawLerped, smoothedYaw = SmoothedYaw(renderer);
                try
                {
                    // dt=0 and the native shadow path do not tick camera/animation.
                    // Restore mutable renderer matrix and yaw immediately afterward.
                    renderer.loadModelMatrixForPlayer(player, false, 0f, true);
                    Array.Copy(renderer.ModelMat, _model, 16);
                }
                finally
                {
                    renderer.ModelMat = original;
                    Array.Copy(_saved, original, 16);
                    renderer.bodyYawLerped = bodyYaw;
                    SmoothedYaw(renderer) = smoothedYaw;
                }

                _preparedId = player.EntityId;
                _prepared = true;
            }

            ComposeItem(_item, _model, pose, transform);
            // Native authored particle origin follows torches/lanterns better than
            // the hand joint itself. Missing/custom draw transforms safely fall
            // back to the player's body-relative anchor in the caller.
            var origin = collectible.TopMiddlePos;
            return ToEye(_item.Values, api.Render.CameraMatrixOriginf,
                origin.X, origin.Y, origin.Z, eye);
        }
        catch
        {
            _prepared = false;
            return false;
        }
    }

    internal static void ComposeItem(Matrixf result, float[] model, AttachmentPointAndPose pose,
        ModelTransform transform)
    {
        var point = pose.AttachPoint;
        var origin = transform.Origin;
        var translate = transform.Translation;
        var rotation = transform.Rotation;
        var scale = transform.ScaleXYZ;
        // Match EntityShapeRenderer's public native item transform ordering.
        // Attachment coordinates use model pixels (1/16 block).
        result.Set(model).Mul(pose.AnimModelMatrix)
            .Translate(origin.X, origin.Y, origin.Z)
            .Scale(scale.X, scale.Y, scale.Z)
            .Translate(point.PosX / 16f + translate.X, point.PosY / 16f + translate.Y, point.PosZ / 16f + translate.Z)
            .Rotate((float)(point.RotationX + rotation.X) * GameMath.DEG2RAD,
                (float)(point.RotationY + rotation.Y) * GameMath.DEG2RAD,
                (float)(point.RotationZ + rotation.Z) * GameMath.DEG2RAD)
            .Translate(-origin.X, -origin.Y, -origin.Z);
    }

    internal static bool ToEye(float[] model, float[] camera, float x, float y, float z, float[] eye)
    {
        float rx = model[0] * x + model[4] * y + model[8] * z + model[12];
        float ry = model[1] * x + model[5] * y + model[9] * z + model[13];
        float rz = model[2] * x + model[6] * y + model[10] * z + model[14];
        eye[0] = camera[0] * rx + camera[4] * ry + camera[8] * rz + camera[12];
        eye[1] = camera[1] * rx + camera[5] * ry + camera[9] * rz + camera[13];
        eye[2] = camera[2] * rx + camera[6] * ry + camera[10] * rz + camera[14];
        return float.IsFinite(eye[0] + eye[1] + eye[2]);
    }
}