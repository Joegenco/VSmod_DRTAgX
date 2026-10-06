using System.Collections.Generic;

namespace DRTAgX;

internal sealed partial class StaticLightSources
{
    private readonly HashSet<Position> _unconfirmedRemovals = new(64);

    private bool TryReadIndexedEmitter(ChunkKey key, Position position, out Source source)
    {
        if (TryReadEmitter(position, out source))
        {
            _unconfirmedRemovals.Remove(position);
            return true;
        }
        if (!_byPosition.TryGetValue(position, out var previous)) return false;
        if (_failedQueries.Contains(position))
        {
            source = previous;
            return true;
        }
        // Async metadata can briefly report zero HSV while the loaded emitter
        // remains present. Confirm once in the next budgeted scan instead of
        // deleting a usable map and waiting for another chunk notification.
        // Exact BlockChanged removals bypass this grace and remain immediate.
        if (_unconfirmedRemovals.Add(position))
        {
            RetryScan(key);
            source = previous;
            return true;
        }
        _unconfirmedRemovals.Remove(position);
        return false;
    }
}
