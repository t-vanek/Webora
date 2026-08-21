using D3Parking.Domain.Common;

namespace D3Parking.Domain.Parking;

/// <summary>A resident's active membership on a physical parking spot.</summary>
public sealed class ParkingSpotResident : Entity
{
    public Guid SpotId { get; private set; }

    public Guid UserId { get; private set; }

    public DateTimeOffset AssignedAtUtc { get; private set; }

    public DateTimeOffset? RemovedAtUtc { get; private set; }

    /// <summary>Weekdays the resident expects to use their allocated days.</summary>
    public Weekday PlannedUseDays { get; private set; } = Weekday.Workdays;

    public bool AutoReleaseUnplannedDays { get; private set; }

    public DateOnly? PlanAppliedThrough { get; private set; }

    public bool IsActive => RemovedAtUtc is null;

    private ParkingSpotResident() { }

    public ParkingSpotResident(Guid spotId, Guid userId, DateTimeOffset assignedAtUtc)
    {
        SpotId = spotId;
        UserId = userId;
        AssignedAtUtc = assignedAtUtc;
    }

    public void Reactivate(DateTimeOffset at)
    {
        AssignedAtUtc = at;
        RemovedAtUtc = null;
    }

    public void Remove(DateTimeOffset at) => RemovedAtUtc = at;

    public void SetUsagePlan(Weekday plannedUseDays, bool autoReleaseUnplannedDays)
    {
        PlannedUseDays = plannedUseDays.Sanitize();
        AutoReleaseUnplannedDays = autoReleaseUnplannedDays;
        PlanAppliedThrough = null;
    }

    public void MarkPlanApplied(DateOnly date)
    {
        if (PlanAppliedThrough is null || date > PlanAppliedThrough)
        {
            PlanAppliedThrough = date;
        }
    }

    public void ResetPlanApplication() => PlanAppliedThrough = null;
}
