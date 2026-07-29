using D3Parking.Domain.Common;

namespace D3Parking.Domain.Parking;

/// <summary>
/// A driver arrived at their reserved spot and could not physically park there. The record stores
/// the state of the spot — the driver-facing flow never names or accuses anyone. For follow-up,
/// the admin view joins in the reservations that met the window on the same spot at query time
/// (typically the no-show whose car may still be there). The blocked reservation itself is voided
/// penalty-free for the reporter (see ReservationService.ReportBlockedSpotAsync).
/// </summary>
public class OccupancyMismatch : Entity
{
    public Guid SpotId { get; private set; }

    public Guid ReservationId { get; private set; }

    public Guid ReporterId { get; private set; }

    /// <summary>The blocked reservation's window, denormalized so trends survive data cleanup.</summary>
    public DateTimeOffset StartUtc { get; private set; }

    public DateTimeOffset EndUtc { get; private set; }

    public DateTimeOffset ReportedAtUtc { get; private set; }

    /// <summary>The replacement spot booked for the reporter, when one was free.</summary>
    public Guid? RelocatedToSpotId { get; private set; }

    /// <summary>
    /// License plate of the blocking vehicle, as the reporter read it off the car (optional).
    /// The admin view matches it against registered plates to tell an employee from an outsider.
    /// </summary>
    public string? BlockerPlate { get; private set; }

    private OccupancyMismatch() { }

    public OccupancyMismatch(Guid spotId, Guid reservationId, Guid reporterId, DateTimeOffset startUtc, DateTimeOffset endUtc, DateTimeOffset reportedAtUtc, string? blockerPlate = null)
    {
        SpotId = spotId;
        ReservationId = reservationId;
        ReporterId = reporterId;
        StartUtc = startUtc;
        EndUtc = endUtc;
        ReportedAtUtc = reportedAtUtc;
        BlockerPlate = blockerPlate;
    }

    public void MarkRelocated(Guid spotId) => RelocatedToSpotId = spotId;
}
