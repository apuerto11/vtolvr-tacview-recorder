using System;

namespace VTOLVRTacviewSPRecorder.Core;

public sealed class EntityRemovalSequence
{
    private readonly TrackedEntity _entity;
    private readonly TacviewEntityRemovalReason _reason;
    private readonly string _label;
    private readonly EntityTransform? _mandatoryTransform;
    private readonly bool _objectWasMaterialized;
    private bool _mandatorySnapshotEmitted;

    public EntityRemovalSequence(
        TrackedEntity entity,
        TacviewEntityRemovalReason reason,
        string label,
        EntityTransform? mandatoryTransform,
        bool objectWasMaterialized)
    {
        _entity = entity ?? throw new ArgumentNullException(nameof(entity));
        if (string.IsNullOrWhiteSpace(label))
        {
            throw new ArgumentException("A non-empty removal label is required.", nameof(label));
        }

        if (mandatoryTransform.HasValue && !mandatoryTransform.Value.HasFiniteValues)
        {
            throw new ArgumentOutOfRangeException(
                nameof(mandatoryTransform),
                "A mandatory lifecycle transform must be finite.");
        }

        _reason = reason;
        _label = label.Trim();
        _mandatoryTransform = mandatoryTransform;
        _objectWasMaterialized = objectWasMaterialized;
    }

    public bool IsComplete { get; private set; }

    public TrackedEntity Entity => _entity;

    public bool WasSuppressed { get; private set; }

    public bool TryDequeue(out TacviewEntityUpdate update)
    {
        update = default;
        if (IsComplete)
        {
            return false;
        }

        if (!_mandatorySnapshotEmitted && _mandatoryTransform.HasValue)
        {
            _mandatorySnapshotEmitted = true;
            update = TacviewEntityUpdate.Upsert(
                _entity.ObjectId,
                _mandatoryTransform.Value,
                _entity.Metadata);
            return true;
        }

        if (!_objectWasMaterialized && !_mandatorySnapshotEmitted)
        {
            WasSuppressed = true;
            IsComplete = true;
            return false;
        }

        update = TacviewEntityUpdate.Delete(_entity.ObjectId, _reason, _label);
        IsComplete = true;
        return true;
    }
}
