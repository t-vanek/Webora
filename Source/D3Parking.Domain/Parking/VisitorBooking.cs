using D3Parking.Domain.Common;

namespace D3Parking.Domain.Parking;

/// <summary>
/// A reception-made booking of a Visitor-type spot for a guest without an account. Visitor spots
/// live outside the employee pool entirely (no credits, no incentives, no no-show machinery) —
/// the reception is accountable for the booking, the guest just parks.
/// </summary>
public class VisitorBooking : Entity
{
    public Guid SpotId { get; private set; }

    /// <summary>Who the spot is for — a person or company name the reception can recognize.</summary>
    public string VisitorName { get; private set; } = string.Empty;

    public string? Company { get; private set; }

    /// <summary>The guest's plate, when known — lets the mismatch view identify a visiting car.</summary>
    public string? LicensePlate { get; private set; }

    /// <summary>The employee being visited (optional); they get a bell notification.</summary>
    public Guid? HostUserId { get; private set; }

    public DateTimeOffset StartUtc { get; private set; }

    public DateTimeOffset EndUtc { get; private set; }

    public VisitorBookingStatus Status { get; private set; } = VisitorBookingStatus.Booked;

    /// <summary>The reception/admin account that created the booking.</summary>
    public Guid CreatedById { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    private VisitorBooking() { }

    public VisitorBooking(Guid spotId, string visitorName, string? company, string? licensePlate,
        Guid? hostUserId, DateTimeOffset startUtc, DateTimeOffset endUtc, Guid createdById, DateTimeOffset createdAtUtc)
    {
        if (endUtc <= startUtc)
            throw new ArgumentException("Visitor booking end must be after its start.", nameof(endUtc));

        SpotId = spotId;
        VisitorName = visitorName;
        Company = company;
        LicensePlate = licensePlate;
        HostUserId = hostUserId;
        StartUtc = startUtc;
        EndUtc = endUtc;
        CreatedById = createdById;
        CreatedAtUtc = createdAtUtc;
    }

    public void Cancel() => Status = VisitorBookingStatus.Cancelled;
}

public enum VisitorBookingStatus
{
    Booked,
    Cancelled,
}
