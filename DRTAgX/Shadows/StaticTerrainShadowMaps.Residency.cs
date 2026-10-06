using System;
using System.Diagnostics;
using Vintagestory.API.Client;
using Vintagestory.API.MathTools;

namespace DRTAgX;

internal sealed partial class StaticTerrainShadowMaps
{
    // Render-thread notifications remain live while contribution is disabled.
    // Keep completed maps, cancel partial transactions, and defer all GL work.
    internal void Pause(StaticLightSources sources, bool justDisabled)
    {
        if (justDisabled) { CancelStage(); _nextSchedulerWake = 0; }
        if (sources.ChangedChunks.Count == 0 && sources.BlockEdits.Count == 0) return;
        for (int i = 0; i < Capacity; i++)
        {
            Slot slot = _slots[i];
            if (!slot.Occupied || !(PlacedLightGeometry.EditsTouch(slot.Source, sources.BlockEdits) ||
                PlacedLightGeometry.ChunksTouch(slot.Source, sources.ChangedChunks))) continue;
            slot.Revision++;
            slot.Dirty = true;
            slot.DirtySince = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
            slot.RecheckAt = sources.NativeGeometryNotifications &&
                PlacedLightGeometry.ChunksTouch(slot.Source, sources.ChangedChunks) ? 0 :
                slot.DirtySince + PlacedLightGeometry.EditFallbackSeconds;
            _nextSchedulerWake = 0;
        }
    }

    // All queue, slot and camera mutations execute on the native render thread.
    internal void UpdateAndRender(ICoreClientAPI api, StaticLightSources sources, Vec3d cameraWorld,
        double[] view, float[] projection, float deltaTime)
    {
        long start = Stopwatch.GetTimestamp();
        _time = start / (double)Stopwatch.Frequency;
        FacesBakedThisFrame = 0;
        LastBakeCpuMilliseconds = 0;
        UpdatedThisFrame = false;
        if (PlacedLightGpuProfile.Enabled) PollGpuTimers();
        if (_sourceGeneration != sources.WorldGeneration)
        {
            _world = api.World;
            _sourceGeneration = sources.WorldGeneration;
            Invalidate();
        }
        if (!Ensure(api))
        {
            Ready = false;
            LastSchedulerCpuMilliseconds = LastTotalCpuMilliseconds = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            return;
        }
        // A settled area owns reusable slots/publication. Only actual input
        // changes, unfinished work or transition deadlines wake the scheduler.
        if (_lastSourceRevision == sources.Revision && _lastViewRevision == sources.ViewRevision &&
            _lastResidencyRevision == _residencyRevision && sources.ChangedChunks.Count == 0 && sources.BlockEdits.Count == 0 &&
            !_pending.DiscoveryPending && !_bake.Active && _time < _nextSchedulerWake)
        {
            LastSchedulerCpuMilliseconds = LastTotalCpuMilliseconds = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            return;
        }
        UpdatedThisFrame = true;
        // Protect around the character even in third person. Snapshot once
        // per scheduler wake; camera distance still determines baking order.
        var playerPosition = api.World.Player?.Entity?.Pos;
        _retentionWorld.Set(playerPosition?.X ?? cameraWorld.X,
            playerPosition?.InternalY ?? cameraWorld.Y, playerPosition?.Z ?? cameraWorld.Z);
        _lastSourceRevision = sources.Revision; _lastViewRevision = sources.ViewRevision;
        UpdateSlots(sources, cameraWorld, view, projection);
        _pending.ResidencyRevision = _residencyRevision;
        _pending.WeakestResidentPriority = WeakestResidentPriority();
        _pending.Update(sources, cameraWorld, view, projection, _residents, _time);
        _lastResidencyRevision = _residencyRevision;
        int pendingVisible = _pending.VisibleCount;
        CandidateCount += pendingVisible;
        PendingVisibleWork += pendingVisible;
        int placement = _pending.ChoosePlacement();
        if (placement >= 0 && _free.Count == 0 && ChoosePendingVictim(_pending.At(placement)) < 0)
            placement = -1;
        int placedMissing = ChoosePlacementMissing();
        int visible = _pending.Choose(true, _free.Count == 0, _time, _preloadSuppressed);
        int dirty = ChooseDirty();
        if (_bake.Active && !_bake.Immediate && !_bake.Complete && (placement >= 0 || placedMissing >= 0))
        {
            // Partial refreshes have not touched sampled depth. Yield to a new
            // placement, keeping completed blends and other placements intact.
            int cancelled = _stagingSlot;
            bool admission = _stageKind == StageKind.Admission;
            CancelStage();
            if (admission) ReleaseSlot(cancelled);
        }
        if (_bake.Active && _bake.Background && (visible >= 0 || dirty >= 0))
        {
            // An incomplete background map owns no sampled data and can yield immediately.
            int cancelled = _stagingSlot;
            CancelStage();
            ReleaseSlot(cancelled);
        }
        ValidateStage(sources, cameraWorld, view, projection);
        if (!_bake.Active)
        {
            int missing = ChooseMissing(true);
            if (placedMissing >= 0)
                BeginBake(placedMissing, _slots[placedMissing].Source, StageKind.Admission, false);
            else if (placement >= 0)
                AdmitPending(placement, false);
            else if (dirty >= 0 && _time - _slots[dirty].DirtySince >= 1)
                BeginBake(dirty, _slots[dirty].Source, StageKind.Refresh, false);
            // Compare ordinary visible refreshes/admissions by camera distance;
            // intentional placements and the existing aged-refresh guard stay first.
            else if (missing >= 0 && (visible < 0 || _slots[missing].CameraDistance <= _pending.At(visible).Distance) &&
                (dirty < 0 || _slots[missing].CameraDistance < _slots[dirty].CameraDistance))
                BeginBake(missing, _slots[missing].Source, StageKind.Admission, false);
            else if (visible >= 0 && (dirty < 0 || _pending.At(visible).Distance < _slots[dirty].CameraDistance))
                AdmitPending(visible, false);
            else if (dirty >= 0)
                BeginBake(dirty, _slots[dirty].Source, StageKind.Refresh, false);
            else
            {
                int background = ChooseMissing(false);
                if (background >= 0) BeginBake(background, _slots[background].Source, StageKind.Admission, true);
                else if (_free.Count > 0)
                {
                    background = _pending.Choose(false, false, _time, _preloadSuppressed);
                    if (background >= 0) AdmitPending(background, true);
                }
            }
        }
        LastSchedulerCpuMilliseconds = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        AdvanceBake(api);
        Publish();
        ScheduleNextWake();
        LastTotalCpuMilliseconds = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
    }

    private void UpdateSlots(StaticLightSources sources, Vec3d camera, double[] view, float[] projection)
    {
        ValidCount = CandidateCount = PendingVisibleWork = 0;
        OldestVisibleWait = 0;
        for (int i = 0; i < Capacity; i++)
        {
            Slot slot = _slots[i];
            if (!slot.Occupied) continue;
            if (!sources.TryGet(slot.Source, out var current)) { ReleaseSlot(i); continue; }
            slot.Source = current;
            double distance = PlacedLightPendingQueue.Distance(slot.Source, camera);
            slot.CameraDistance = distance;
            slot.PlayerDistance = PlacedLightPendingQueue.Distance(slot.Source, _retentionWorld);
            bool inRange = PlacedLightPendingQueue.InRange(slot.Source, distance);
            if (!inRange)
            {
                if (!slot.Valid) { ReleaseSlot(i); continue; }
                if (slot.ExitAt < 0) slot.ExitAt = _time;
            }
            else
            {
                slot.ExitAt = -1;
            }
            // Cold maps keep their six faces in the fixed allocation. Distance
            // affects publication/victim priority rather than destroying depth.
            slot.Bounds = inRange ? PlacedLightView.ForShadowCulling(
                PlacedLightView.Project(slot.Source, view, projection), ViewCullingEnabled) :
                new PlacedLightView.Bounds(0, 0, 0, 0, 2, 0, 0, 0);
            slot.Priority = inRange ? PlacedLightPolicy.Priority(slot.Bounds, distance) : PlacedLightPolicy.Offscreen;
            slot.Fade = slot.Valid ? PlacedLightPolicy.Smooth((_time - slot.FadeAt) / PlacedLightPolicy.FadeSeconds) : 0;
            if (slot.ExitAt >= 0) slot.Fade *= 1 - PlacedLightPolicy.Smooth((_time - slot.ExitAt) / PlacedLightPolicy.ExitSeconds);
            // The same cached bounds drive hit accounting and compute tile coverage.
            if (slot.Valid && slot.Fade > 0 && slot.Priority < PlacedLightPolicy.Offscreen) slot.LastHit = _time;
            bool edited = PlacedLightGeometry.EditsTouch(slot.Source, sources.BlockEdits);
            bool uploaded = PlacedLightGeometry.ChunksTouch(slot.Source, sources.ChangedChunks);
            if (edited || uploaded)
            {
                slot.Revision++;
                if (!slot.Dirty) slot.DirtySince = _time;
                slot.Dirty = true;
                // Exact edits survive a missed upload. Wait for usable geometry
                // or one bounded fallback; keep sampling the complete old map.
                slot.RecheckAt = uploaded && sources.NativeGeometryNotifications ? 0 :
                    _time + PlacedLightGeometry.EditFallbackSeconds;
            }
            if (slot.RecheckAt > 0 && _time >= slot.RecheckAt)
            {
                slot.RecheckAt = 0;
            }
            if (slot.Valid) ValidCount++;
            if (slot.Priority < PlacedLightPolicy.Offscreen)
            {
                CandidateCount++;
                if (!slot.Valid || slot.Dirty)
                {
                    PendingVisibleWork++;
                    OldestVisibleWait = Math.Max(OldestVisibleWait, (float)(_time - slot.DirtySince));
                }
            }
        }
    }

    private void Assign(int index, StaticLightSources.Source source)
    {
        Slot slot = _slots[index];
        slot.Source = source;
        slot.Occupied = true;
        slot.Valid = slot.Dirty = false;
        slot.Immediate = false;
        slot.Fade = 0;
        slot.Generation++;
        slot.Revision = slot.BakedRevision = 0;
        slot.LastHit = double.NegativeInfinity;
        slot.CompletedAt = slot.FadeAt = slot.DirtySince = _time;
        slot.ExitAt = -1;
        slot.CameraDistance = double.PositiveInfinity;
        slot.PlayerDistance = double.PositiveInfinity;
        slot.RecheckAt = slot.FailedUntil = 0;
        _residents.Add(StaticLightSources.Identity(source), index);
        _residencyRevision++;
        Admissions++;
    }

    private void ReleaseSlot(int index)
    {
        Slot slot = _slots[index];
        if (!slot.Occupied) return;
        if (_stagingSlot == index) CancelStage();
        _residents.Remove(StaticLightSources.Identity(slot.Source));
        _residencyRevision++;
        slot.Occupied = slot.Valid = slot.Dirty = false;
        slot.Generation++;
        _free.Push(index);
    }

    private int ChooseMissing(bool visible)
    {
        int chosen = -1;
        for (int i = 0; i < Capacity; i++)
        {
            Slot slot = _slots[i];
            if (!slot.Occupied || slot.Valid || _time < slot.FailedUntil || _time < slot.RecheckAt ||
                (slot.Priority < PlacedLightPolicy.Offscreen) != visible) continue;
            if (chosen < 0 || BakeBefore(slot, _slots[chosen])) chosen = i;
        }
        return chosen;
    }

    private int ChoosePlacementMissing()
    {
        int chosen = -1;
        for (int i = 0; i < Capacity; i++)
        {
            Slot slot = _slots[i];
            if (!slot.Occupied || slot.Valid || !slot.Immediate || _time < slot.FailedUntil ||
                _time < slot.RecheckAt) continue;
            if (chosen < 0 || BakeBefore(slot, _slots[chosen])) chosen = i;
        }
        return chosen;
    }

    private int ChooseDirty()
    {
        int chosen = -1;
        for (int i = 0; i < Capacity; i++)
        {
            Slot slot = _slots[i];
            if (!slot.Occupied || !slot.Valid || !slot.Dirty || slot.ExitAt >= 0 ||
                slot.Priority == PlacedLightPolicy.Offscreen || _time < slot.FailedUntil ||
                _time < slot.RecheckAt) continue;
            if (chosen < 0 || BakeBefore(slot, _slots[chosen])) chosen = i;
        }
        return chosen;
    }

    private static bool BakeBefore(Slot a, Slot b)
    {
        // Bake outward from the camera while retaining stable residency/victim
        // tiers. Near visible maps precede distant centred maps.
        bool visibleA = a.Priority < PlacedLightPolicy.Offscreen, visibleB = b.Priority < PlacedLightPolicy.Offscreen;
        if (visibleA != visibleB) return visibleA;
        if (a.CameraDistance != b.CameraDistance) return a.CameraDistance < b.CameraDistance;
        if (a.DirtySince != b.DirtySince) return a.DirtySince < b.DirtySince;
        if (a.Source.X != b.Source.X) return a.Source.X < b.Source.X;
        if (a.Source.Y != b.Source.Y) return a.Source.Y < b.Source.Y;
        return a.Source.Z < b.Source.Z;
    }
}
