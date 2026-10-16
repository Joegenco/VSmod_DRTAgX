using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.GameContent;

namespace DRTAgX;

internal sealed partial class StableContainerRendering
{
    // Scoped to the verified native container tessellation method, never the
    // chunk mesher globally. The original still builds/initializes its animator.
    // A missing model or unexpected multiple submissions forwards everything.
    private sealed class SubmissionPool : ITerrainMeshPool
    {
        private readonly StableContainerRendering _owner;
        private readonly BlockEntity _blockEntity;
        private readonly BlockEntityAnimationUtil _utility;
        private readonly ITerrainMeshPool _destination;
        private MeshData? _mesh;
        private float[]? _transform;
        private ColorMapData? _colorMap;
        private int _lod, _kind, _submissions;

        internal SubmissionPool(StableContainerRendering owner, BlockEntity blockEntity,
            BlockEntityAnimationUtil utility, ITerrainMeshPool destination)
        {
            _owner = owner;
            _blockEntity = blockEntity;
            _utility = utility;
            _destination = destination;
        }

        public void AddMeshData(MeshData data, int lodLevel = 1) => Store(data, null, null, lodLevel, 0);

        public void AddMeshData(MeshData data, float[] tfMatrix, int lodLevel = 1) =>
            Store(data, tfMatrix, null, lodLevel, 1);

        public void AddMeshData(MeshData data, ColorMapData colorMapData, int lodLevel = 1) =>
            Store(data, null, colorMapData, lodLevel, 2);

        private void Store(MeshData mesh, float[]? transform, ColorMapData? colorMap, int lod, int kind)
        {
            Flush();
            _submissions++;
            _mesh = mesh;
            _transform = transform;
            _colorMap = colorMap;
            _lod = lod;
            _kind = kind;
        }

        internal void Complete(bool success)
        {
            // Idle native containers submit one rest mesh; active ones submit
            // none. Scene color stays on the native renderer; the opaque mesh's
            // rest vertices additionally supply stable point-light depth only.
            if (success && _submissions <= 1 && _kind == 0 &&
                _owner.RetainRenderer(_blockEntity, _utility, _mesh))
                _mesh = null;
            else
            {
                // A formerly supported model can change its submission shape.
                // Retire that opt-in before returning to native terrain, so an
                // idle forced renderer cannot later draw a duplicate rest mesh.
                _owner.ReleaseRenderer(_utility);
                Flush();
            }
        }

        private void Flush()
        {
            if (_mesh == null) return;
            switch (_kind)
            {
                case 1: _destination.AddMeshData(_mesh, _transform!, _lod); break;
                case 2: _destination.AddMeshData(_mesh, _colorMap!.Value, _lod); break;
                default: _destination.AddMeshData(_mesh, _lod); break;
            }

            _mesh = null;
        }
    }
}