using D3Parking.Domain.Common;

namespace D3Parking.Domain.Parking;

/// <summary>
/// The single resident entitled to use a shared-residency spot on a local calendar day.
/// The unique (SpotId, Date) database index is the physical collision backstop.
/// </summary>
public sealed class SpotDayAssignment : Entity
{
    public Guid SpotId { get; private set; }

    public Guid ResidentId { get; private set; }

    public DateOnly Date { get; private set; }

    public DateTimeOffset AssignedAtUtc { get; private set; }

    private SpotDayAssignment() { }

    public SpotDayAssignment(Guid spotId, Guid residentId, DateOnly date, DateTimeOffset assignedAtUtc)
    {
        SpotId = spotId;
        ResidentId = residentId;
        Date = date;
        AssignedAtUtc = assignedAtUtc;
    }
}
