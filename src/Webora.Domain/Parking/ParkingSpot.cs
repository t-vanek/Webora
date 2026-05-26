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
}
