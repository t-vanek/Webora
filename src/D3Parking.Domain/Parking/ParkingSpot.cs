using D3Parking.Domain.Common;

namespace D3Parking.Domain.Parking;

/// <summary>A single physical parking space that can be reserved.</summary>
public class ParkingSpot : Entity
{
    /// <summary>Human-readable identifier such as "A-12" or "P2-08". Unique across the lot.</summary>
    public string Code { get; private set; } = string.Empty;

    public ParkingSpotType Type { get; private set; }

    /// <summary>Inactive spots are hidden from booking (maintenance, reassignment, …).</summary>
    public bool IsActive { get; private set; } = true;

    public string? Notes { get; private set; }

    /// <summary>The resident this spot is reserved for, or null when it is a shared pool spot.</summary>
    /// <remarks>
    /// Compatibility pointer to the primary resident. New code uses <see cref="ParkingSpotResident"/>
    /// memberships; keeping this value during the transition lets older databases and integrations
    /// continue to classify a spot as resident-owned.
    /// </remarks>
    public Guid? OwnerId { get; private set; }

    /// <summary>Maximum number of active resident memberships on this physical spot.</summary>
    public int ResidentCapacity { get; private set; } = 1;

    /// <summary>
    /// Legacy persisted value retained while older databases are upgraded. It no longer limits
    /// releases or changes their reward and is reset to zero by the planner migration.
    /// </summary>
    public int MonthlyShareAllowance { get; private set; }

    /// <summary>The day the resident was last reminded to confirm arrival, so it is sent once a day.</summary>
    public DateOnly? LastResidentReminderDate { get; private set; }

    /// <summary>The day the resident was last told their spot auto-shared, so it is sent once a day.</summary>
    public DateOnly? LastAutoShareNoticeDate { get; private set; }

    /// <summary>Weekdays the resident needs the spot; the rest are candidates for advance release.</summary>
    public Weekday PlannedUseDays { get; private set; } = Weekday.Workdays;

    /// <summary>Whether the days outside <see cref="PlannedUseDays"/> are released ahead of time.</summary>
    public bool AutoReleaseUnplannedDays { get; private set; }

    /// <summary>
    /// The last day the planner has already decided about. It only ever looks past this watermark, so
    /// a day the resident took back by hand is never re-released and each day is released once —
    /// however often the maintenance loop runs. Cleared whenever the plan changes, which is what
    /// re-applies the new plan across the whole horizon.
    /// </summary>
    public DateOnly? PlanAppliedThrough { get; private set; }

    public bool HasOwner => OwnerId is not null;

    private ParkingSpot() { }

    public ParkingSpot(string code, ParkingSpotType type, string? notes = null)
    {
        Rename(code);
        Type = type;
        Notes = notes;
    }

    public void Rename(string code)
    {
        if (string.IsNullOrWhiteSpace(code))
            throw new ArgumentException("Spot code must not be empty.", nameof(code));

        Code = code.Trim();
    }

    public void ChangeType(ParkingSpotType type) => Type = type;

    public void UpdateNotes(string? notes) => Notes = notes;

    public void Activate() => IsActive = true;

    public void Deactivate() => IsActive = false;

    /// <summary>Assigns a resident. Passing null clears ownership and resets the sharing settings.</summary>
    public void AssignOwner(Guid? ownerId)
    {
        OwnerId = ownerId;
        if (ownerId is null)
        {
            MonthlyShareAllowance = 0;
            LastResidentReminderDate = null;
            LastAutoShareNoticeDate = null;
            PlannedUseDays = Weekday.Workdays;
            AutoReleaseUnplannedDays = false;
            PlanAppliedThrough = null;
        }
    }

    public void SetResidentCapacity(int capacity)
    {
        if (capacity is < 1 or > 20)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity), "Resident capacity must be between 1 and 20.");
        }

        ResidentCapacity = capacity;
    }

    /// <summary>Compatibility hook for legacy data and migration regression tests only.</summary>
    public void SetShareAllowance(int allowance) => MonthlyShareAllowance = allowance < 0 ? 0 : allowance;

    /// <summary>
    /// Sets the standing usage plan. The applied-through watermark is dropped so the new plan takes
    /// effect across the whole horizon rather than only from wherever the old one had got to.
    /// </summary>
    public void SetUsagePlan(Weekday plannedUseDays, bool autoReleaseUnplannedDays)
    {
        PlannedUseDays = plannedUseDays.Sanitize();
        AutoReleaseUnplannedDays = autoReleaseUnplannedDays;
        PlanAppliedThrough = null;
    }

    /// <summary>Records that the planner has decided about every day up to and including <paramref name="date"/>.</summary>
    public void MarkPlanApplied(DateOnly date)
    {
        if (PlanAppliedThrough is null || date > PlanAppliedThrough)
        {
            PlanAppliedThrough = date;
        }
    }

    /// <summary>Forces the standing plan to be evaluated again after global calendar rules change.</summary>
    public void ResetPlanApplication() => PlanAppliedThrough = null;

    public void MarkResidentReminded(DateOnly date) => LastResidentReminderDate = date;

    public void MarkAutoShareNoticed(DateOnly date) => LastAutoShareNoticeDate = date;
}
