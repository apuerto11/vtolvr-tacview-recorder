using System;
using System.Collections.Generic;

namespace VTOLVRTacviewSPRecorder.Core;

public sealed class EntityRegistry
{
    public const ulong InitialOwnshipObjectId = 0x100;
    public const ulong OwnshipObjectId = InitialOwnshipObjectId;

    private readonly Dictionary<object, TrackedEntity> _byIdentity =
        new Dictionary<object, TrackedEntity>(ReferenceEqualityComparer.Instance);
    private ulong _nextObjectId = InitialOwnshipObjectId + 1;
    private TrackedEntity _ownship;
    private bool _hasAllocatedOwnshipId;

    public int Count => _byIdentity.Count;

    public TrackedEntity Register(
        object identity,
        EntityKind kind,
        bool isOwnship,
        EntityMetadata metadata)
    {
        if (identity == null)
        {
            throw new ArgumentNullException(nameof(identity));
        }

        if (metadata == null)
        {
            throw new ArgumentNullException(nameof(metadata));
        }

        if (_byIdentity.TryGetValue(identity, out TrackedEntity existing))
        {
            if (existing.IsOwnship != isOwnship)
            {
                throw new InvalidOperationException(
                    "An entity identity cannot change its ownship status during a session.");
            }

            if (existing.Kind != kind)
            {
                throw new InvalidOperationException(
                    "An entity identity cannot change its kind during a session.");
            }

            existing.UpdateMetadata(metadata);
            return existing;
        }

        if (isOwnship && _ownship != null)
        {
            throw new InvalidOperationException(
                "Only one active ownship can be registered in a session.");
        }

        ulong objectId = isOwnship && !_hasAllocatedOwnshipId
            ? InitialOwnshipObjectId
            : AllocateFreshObjectId();
        var tracked = new TrackedEntity(identity, objectId, kind, isOwnship, metadata);
        _byIdentity.Add(identity, tracked);

        if (isOwnship)
        {
            _ownship = tracked;
            _hasAllocatedOwnshipId = true;
        }

        return tracked;
    }

    public bool TryGet(object identity, out TrackedEntity tracked)
    {
        if (identity == null)
        {
            tracked = null;
            return false;
        }

        return _byIdentity.TryGetValue(identity, out tracked);
    }

    public bool Remove(object identity, out TrackedEntity removed)
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
        EntityMetadata metadata,
        out TrackedEntity previous,
        out TrackedEntity promoted)
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

        if (previous.Kind != EntityKind.Aircraft)
        {
            throw new InvalidOperationException("Only aircraft entities can be promoted to ownship.");
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
                "Cannot promote a second active entity to ownship.");
        }

        _byIdentity.Remove(identity);
        ulong objectId = !_hasAllocatedOwnshipId
            ? InitialOwnshipObjectId
            : AllocateFreshObjectId();
        promoted = new TrackedEntity(identity, objectId, previous.Kind, true, metadata);
        _byIdentity.Add(identity, promoted);
        _ownship = promoted;
        _hasAllocatedOwnshipId = true;
        return true;
    }

    public IReadOnlyList<TrackedEntity> SnapshotActive()
    {
        return new List<TrackedEntity>(_byIdentity.Values);
    }

    public void Clear()
    {
        _byIdentity.Clear();
        _ownship = null;
        _nextObjectId = InitialOwnshipObjectId + 1;
        _hasAllocatedOwnshipId = false;
    }

    private ulong AllocateFreshObjectId()
    {
        if (_nextObjectId == ulong.MaxValue)
        {
            throw new InvalidOperationException("No Tacview object IDs remain for this session.");
        }

        return _nextObjectId++;
    }
}
