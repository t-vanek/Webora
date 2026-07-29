using D3Parking.Domain.Common;

namespace D3Parking.Domain.Parking;

/// <summary>
/// A driver arrived at their reserved spot and could not physically park there. Deliberately a
/// record of the spot's state, not an accusation — there is no offender in the data, and admins
/// work with per-spot trends. The blocked reservation itself is voided penalty-free for the
/// reporter (see ReservationService.ReportBlockedSpotAsync).
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

    private OccupancyMismatch() { }

    public OccupancyMismatch(Guid spotId, Guid reservationId, Guid reporterId, DateTimeOffset startUtc, DateTimeOffset endUtc, DateTimeOffset reportedAtUtc)
    {
        SpotId = spotId;
        ReservationId = reservationId;
        ReporterId = reporterId;
        StartUtc = startUtc;
        EndUtc = endUtc;
        ReportedAtUtc = reportedAtUtc;
    }

    public void MarkRelocated(Guid spotId) => RelocatedToSpotId = spotId;
}
