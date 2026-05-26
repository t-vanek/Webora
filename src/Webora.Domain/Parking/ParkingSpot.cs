using Webora.Domain.Common;

namespace Webora.Domain.Parking;

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
    public Guid? OwnerId { get; private set; }

    /// <summary>How many times a month the resident is willing to share the spot; scales the reward.</summary>
    public int MonthlyShareAllowance { get; private set; }

    /// <summary>The day the resident was last reminded to confirm arrival, so it is sent once a day.</summary>
    public DateOnly? LastResidentReminderDate { get; private set; }

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
        }
    }

    public void SetShareAllowance(int allowance) => MonthlyShareAllowance = allowance < 0 ? 0 : allowance;

    public void MarkResidentReminded(DateOnly date) => LastResidentReminderDate = date;
}
