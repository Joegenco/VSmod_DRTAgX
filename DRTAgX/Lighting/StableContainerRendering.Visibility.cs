using System;
using System.Threading;
using Vintagestory.API.Client;

namespace DRTAgX;

// Snapshot identity checks intentionally use exact copied values, not tolerances.
// ReSharper disable CompareOfFloatsByEqualityOperator
internal sealed partial class StableContainerRendering
{
    private long _viewFrame = -1;
    private FrustumCulling? _viewCuller;
    private double _cameraX, _cameraZ, _viewRange;

    // Called on the render thread in Before, once before native sun shadows
    // and our point-light captures. The terrain worker only publishes new
    // registrations/bounds; one lock covers the existing registry each frame.
    internal void BeforeFrame()
    {
        ++_frame;
        lock (_stateLock)
        {
            for (int i = _states.Count - 1; i >= 0; i--)
            {
                if (!_states[i].TryGetTarget(out State? state) || state.Renderer is not { } renderer ||
                    !_renderers.TryGetValue(renderer, out State? mapped) || !ReferenceEquals(state, mapped))
                {
                    _states.RemoveAt(i);
                    continue;
                }

                RefreshCaster(state, renderer);
            }
        }
    }

    private bool CameraVisible(State state)
    {
        // A mod can animate bones arbitrarily far from the rest mesh. Cull only
        // known rest poses; active/open animations keep their native visibility
        // until they return to rest. New worker registrations fail open until
        // their render-thread transform snapshot is ready.
        if (Volatile.Read(ref state.RefreshFrame) != _frame || !state.HasTransform ||
            state.Utility.activeAnimationsByAnimCode.Count != 0 ||
            state.Utility.animator?.ActiveAnimationCount > 0) return true;

        // A third-party callback can move/scale an idle model after Before.
        // Let that draw through until the next coherent bounds snapshot, rather
        // than hide it using its previous transform. No native state is changed.
        var renderer = state.Renderer;
        var pos = state.BlockEntity.Pos;
        if (renderer == null || state.X != pos.X || state.Y != pos.InternalY || state.Z != pos.Z) return true;
        float[]? custom = renderer.CustomTransform;
        bool hasCustom = custom is { Length: >= 16 };
        if (hasCustom != state.HasCustomTransform) return true;
        if (hasCustom)
        {
            for (int i = 0; i < 16; i++)
                if (custom![i] != state.CustomModel[i])
                    return true;
        }
        else if (renderer.ScaleX != state.ScaleX || renderer.ScaleY != state.ScaleY ||
                 renderer.ScaleZ != state.ScaleZ || renderer.rotationDeg.Y != state.Yaw) return true;

        if (_viewFrame != _frame)
        {
            _viewFrame = _frame;
            _viewCuller = _api.Render.DefaultFrustumCuller;
            var camera = _api.World?.Player?.Entity?.CameraPos;
            _viewRange = _viewCuller is { ViewDistanceSq: > 0 } && camera != null
                ? Math.Sqrt(_viewCuller.ViewDistanceSq)
                : 0;
            if (camera != null)
            {
                _cameraX = camera.X;
                _cameraZ = camera.Z;
            }
        }

        if (_viewRange <= 0 || _viewCuller == null) return true;
        float radius = state.Radius + .05f;
        if (!float.IsFinite(radius) || radius <= 0) return true;
        // Transform the rest sphere centre with the same local matrix used by
        // the native model and stable caster. Keep world translation in double
        // precision, including custom shear/scale and offset transforms.
        float[] model = state.LocalModel;
        double x = state.X + .5 * (model[0] + model[4] + model[8]) + model[12];
        double y = state.Y + .5 * (model[1] + model[5] + model[9]) + model[13];
        double z = state.Z + .5 * (model[2] + model[6] + model[10]) + model[14];
        if (!double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(z)) return true;
        double dx = x - _cameraX, dz = z - _cameraZ, reach = _viewRange + radius;
        // Native terrain uses horizontal view distance. Include the whole
        // sphere, so a large modded container can straddle the distance edge.
        return dx * dx + dz * dz <= reach * reach && _viewCuller.SphereInFrustum(x, y, z, radius);
    }
}