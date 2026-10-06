using System;
using Vintagestory.API.Client;
using Vintagestory.API.MathTools;

namespace DRTAgX;

internal sealed partial class StaticTerrainShadowMaps
{
    private PlacedLightView.Bounds _stagedBounds;
    private double _incomingLastHit, _incomingCompletedAt;

    private void CancelStage()
    {
        _bake = default;
        _stageKind = StageKind.None;
        _stagingSlot = -1;
        _blendStart = -1;
    }

    private void BeginBake(int index, StaticLightSources.Source source, StageKind kind, bool background)
    {
        Slot owner = _slots[index];
        _stagingSlot = index;
        _stageKind = kind;
        _blendStart = -1;
        _incomingLastHit = double.NegativeInfinity;
        _bake = new PlacedLightBake { Active = true, Background = background, Slot = index,
            Source = source, Generation = owner.Generation, Revision = owner.Revision,
            Immediate = kind == StageKind.Admission && owner.Immediate };
    }

    private void AdmitPending(int pendingIndex, bool background)
    {
        var entry = _pending.At(pendingIndex);
        int index;
        if (_free.Count > 0)
        {
            index = _free.Pop();
            Assign(index, entry.Source);
            _slots[index].Immediate = entry.Immediate;
            _slots[index].Bounds = entry.Bounds;
            _slots[index].Priority = entry.Priority;
            _slots[index].CameraDistance = entry.Distance;
            _pending.Remove(pendingIndex);
            BeginBake(index, entry.Source, StageKind.Admission, background);
        }
        else
        {
            if (background) return;
            index = ChoosePendingVictim(entry);
            if (index < 0) return;
            BeginBake(index, entry.Source, StageKind.Replacement, false);
            _bake.Immediate = entry.Immediate;
            // Keep placement priority across geometry invalidation or reload.
            // Successful promotion makes it resident; Update then removes it.
            // Keep every replacement candidate until successful promotion.
            // Cancellation must not depend on a continuously rotating cursor.
        }
        _stagedBounds = entry.Bounds;
    }

    private int ChoosePendingVictim(PlacedLightPendingQueue.Entry entry)
    {
        for (int i = 0; i < Capacity; i++)
        {
            Slot slot = _slots[i];
            _victims[i] = new PlacedLightPolicy.Resident(slot.Priority, slot.LastHit,
                !slot.Occupied || _time < slot.FailedUntil || ProtectNearbyResident(slot),
                slot.Source.X, slot.Source.Y, slot.Source.Z);
        }
        return entry.Immediate
            ? PlacedLightPolicy.ChoosePlacementVictim(_victims.AsSpan(0, Capacity), entry.Priority)
            : PlacedLightPolicy.ChooseVictim(_victims.AsSpan(0, Capacity), entry.Priority);
    }

    private void ValidateStage(StaticLightSources sources, Vec3d camera, double[] view, float[] projection)
    {
        if (!_bake.Active) return;
        Slot owner = _slots[_stagingSlot];
        // A removal elsewhere can lower the valid count after replacement
        // started. Recheck before promotion so nearby completed depth survives.
        if (_stageKind == StageKind.Replacement && ProtectNearbyResident(owner))
        { CancelStage(); return; }
        bool incomingExists = sources.TryGet(_bake.Source, out var current);
        if (!owner.Occupied || !_bake.Matches(_stagingSlot, owner.Generation, owner.Revision, _bake.Source) ||
            !incomingExists || !PlacedLightPendingQueue.InRange(current, PlacedLightPendingQueue.Distance(current, camera)))
        { CancelStage(); return; }
        _bake.Source = current; // HSV changes never invalidate a depth transaction.
        _stagedBounds = _stageKind == StageKind.Replacement ? PlacedLightView.ForShadowCulling(
            PlacedLightView.Project(current, view, projection), ViewCullingEnabled) : owner.Bounds;
        bool visible = PlacedLightPolicy.Visible(_stagedBounds);
        if (visible) _bake.Background = false;
        if (_bake.Complete && visible && Blend > 0) _incomingLastHit = _time;
        if (_stageKind == StageKind.Replacement)
            if (PlacedLightGeometry.ChunksTouch(current, sources.ChangedChunks) ||
                PlacedLightGeometry.EditsTouch(current, sources.BlockEdits, _bake.Immediate)) CancelStage();
    }

    private float Blend => _blendStart < 0 ? 0 : PlacedLightPolicy.Smooth((_time - _blendStart) /
        (_stageKind == StageKind.Refresh ? PlacedLightPolicy.RefreshSeconds : PlacedLightPolicy.FadeSeconds));

    private void AdvanceBake(ICoreClientAPI api)
    {
        if (!_bake.Active) return;
        if (!_bake.Complete)
        {
            int faces = Math.Min(_bake.Budget, 6 - _bake.NextFace);
            if (!RenderSource(api, new ActiveLight(_bake.Source, Capacity, 1, 0)))
            {
                _slots[_stagingSlot].FailedUntil = _time + 1;
                BakeFailures++;
                CancelStage();
                return;
            }
            _bake.Advance(faces);
            if (!_bake.Complete) return;
            _incomingCompletedAt = _time;
            _blendStart = _time;
        }
        Slot owner = _slots[_stagingSlot];
        // Block edits change real occluders: switch to the complete six-face
        // refresh immediately. Only ordinary resident replacements crossfade.
        if (!_bake.Immediate && _stageKind == StageKind.Replacement &&
            owner.Priority != PlacedLightPolicy.Offscreen && Blend < 1) return;
        PromoteStaging(_stagingSlot);
        if (_stageKind == StageKind.Replacement)
        {
            bool offscreen = owner.Priority == PlacedLightPolicy.Offscreen;
            if (offscreen) OffViewEvictions++;
            else DistanceEvictions++;
            _residents.Remove(StaticLightSources.Identity(owner.Source));
            Assign(_stagingSlot, _bake.Source);
            owner.Bounds = _stagedBounds;
            owner.CompletedAt = _incomingCompletedAt;
            owner.LastHit = _incomingLastHit;
            owner.FadeAt = offscreen && !_bake.Immediate ? _time : _time - PlacedLightPolicy.FadeSeconds;
            owner.Fade = offscreen && !_bake.Immediate ? 0 : 1;
        }
        else if (_stageKind == StageKind.Admission)
        {
            owner.CompletedAt = owner.FadeAt = _time;
            // A placement is an intentional visible change. Publish its first
            // complete map at full strength rather than adding the travel fade.
            if (_bake.Immediate)
            { owner.FadeAt -= PlacedLightPolicy.FadeSeconds; owner.Fade = 1; }
            ValidCount++;
        }
        owner.Valid = true;
        owner.BakedRevision = owner.Revision;
        owner.Dirty = false;
        owner.Immediate = false;
        CancelStage();
    }

    private void Publish()
    {
        _active.Clear();
        _readyLights.Clear();
        ValidCount = 0;
        for (int i = 0; i < Capacity; i++)
        {
            Slot slot = _slots[i];
            if (!slot.Occupied || !slot.Valid) continue;
            ValidCount++;
            bool transition = _bake.Complete && _stagingSlot == i;
            bool pair = transition && _stageKind == StageKind.Replacement;
            float fade = pair ? slot.Fade * (1 - Blend) : slot.Fade;
            var light = new ActiveLight(slot.Source, i, fade,
                transition && _stageKind == StageKind.Refresh ? Blend : 0, Capacity, pair) { Bounds = slot.Bounds };
            _readyLights.Add(light);
            if (PlacedLightPolicy.Visible(slot.Bounds)) _active.Add(light);
        }
        if (_bake.Complete && _stageKind == StageKind.Replacement)
        {
            var incoming = new ActiveLight(_bake.Source, Capacity, Blend, 0, -1, true) { Bounds = _stagedBounds };
            _readyLights.Add(incoming);
            if (PlacedLightPolicy.Visible(_stagedBounds)) _active.Add(incoming);
        }
        Ready = _readyLights.Count > 0;
        // Geometry can change while the published source records stay equal.
        // Depth writes need no new source upload or coarse tile dispatch.
        bool changed = _publishedCount != _active.Count;
        for (int i = 0; i < _active.Count && !changed; i++) changed = _published[i] != _active[i];
        if (changed)
        {
            for (int i = 0; i < _active.Count; i++) _published[i] = _active[i];
            _publishedCount = _active.Count; PublicationRevision++;
        }
    }
}
