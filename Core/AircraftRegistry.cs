using System;
using System.Collections.Generic;

namespace VTOLVRTacviewSPRecorder.Core;

public sealed class AircraftRegistry
{
    public const ulong OwnshipObjectId = TacviewAcmiWriter.OwnshipObjectId;

    private readonly Dictionary<object, TrackedAircraft> _byIdentity =
        new Dictionary<object, TrackedAircraft>(ReferenceEqualityComparer.Instance);
    private ulong _nextObjectId = OwnshipObjectId + 1;
    private TrackedAircraft _ownship;
    private bool _hasAllocatedOwnshipId;

    public int Count => _byIdentity.Count;

    public TrackedAircraft Register(object identity, bool isOwnship, AircraftMetadata metadata)
    {
        if (identity == null)
        {
            throw new ArgumentNullException(nameof(identity));
        }

        if (metadata == null)
        {
            throw new ArgumentNullException(nameof(metadata));
        }

        if (_byIdentity.TryGetValue(identity, out TrackedAircraft existing))
        {
            if (existing.IsOwnship != isOwnship)
            {
                throw new InvalidOperationException("An aircraft identity cannot change its ownship status during a session.");
            }

            existing.UpdateMetadata(metadata);
            return existing;
        }

        if (isOwnship && _ownship != null)
        {
            throw new InvalidOperationException(
                "Only one active ownship can be registered in a session.");
        }

        // Tacview object IDs cannot be reused after a delete. The first
        // ownship uses the conventional 0x100; a later player respawn gets
        // a new monotonically allocated ID while retaining ownship sampling.
        ulong objectId = isOwnship && !_hasAllocatedOwnshipId
            ? OwnshipObjectId
            : AllocateOtherObjectId();
        var tracked = new TrackedAircraft(identity, objectId, isOwnship, metadata);
        _byIdentity.Add(identity, tracked);

        if (isOwnship)
        {
            _ownship = tracked;
            _hasAllocatedOwnshipId = true;
        }

        return tracked;
    }

    public bool TryGet(object identity, out TrackedAircraft tracked)
    {
        if (identity == null)
        {
            tracked = null;
            return false;
        }

        return _byIdentity.TryGetValue(identity, out tracked);
    }

    public bool Remove(object identity, out TrackedAircraft removed)
    {
        if (identity == null || !_byIdentity.TryGetValue(identity, out removed))
        {
            removed = null;
            return false;
        }

        _byIdentity.Remove(identity);
        if (removed.IsOwnship)
        {
            _ownship = null;
        }

        return true;
    }

    public bool PromoteToOwnship(
        object identity,
        AircraftMetadata metadata,
        out TrackedAircraft previous,
        out TrackedAircraft promoted)
    {
        if (identity == null)
        {
            throw new ArgumentNullException(nameof(identity));
        }

        if (metadata == null)
        {
            throw new ArgumentNullException(nameof(metadata));
        }

        if (!_byIdentity.TryGetValue(identity, out previous))
        {
            promoted = null;
            return false;
        }

        if (previous.IsOwnship)
        {
            previous.UpdateMetadata(metadata);
            promoted = previous;
            return true;
        }

        if (_ownship != null)
        {
            throw new InvalidOperationException(
                "Cannot promote a second active aircraft to ownship.");
        }

        _byIdentity.Remove(identity);
        ulong objectId = _hasAllocatedOwnshipId
            ? AllocateOtherObjectId()
            : OwnshipObjectId;
        promoted = new TrackedAircraft(identity, objectId, true, metadata);
        _byIdentity.Add(identity, promoted);
        _ownship = promoted;
        _hasAllocatedOwnshipId = true;
        return true;
    }

    public IReadOnlyList<TrackedAircraft> SnapshotActive()
    {
        return new List<TrackedAircraft>(_byIdentity.Values);
    }

    public void Clear()
    {
        _byIdentity.Clear();
        _ownship = null;
        _nextObjectId = OwnshipObjectId + 1;
        _hasAllocatedOwnshipId = false;
    }

    private ulong AllocateOtherObjectId()
    {
        if (_nextObjectId == ulong.MaxValue)
        {
            throw new InvalidOperationException("No Tacview object IDs remain for this session.");
        }

        return _nextObjectId++;
    }
}
