using D3Parking.Domain.Common;

namespace D3Parking.Domain.Parking;

/// <summary>
/// One sent "the lot is wide open" campaign. Doubles as the dedup record (one campaign per
/// period, one evaluation per day) and as the base for measuring whether campaigns actually
/// convert into bookings.
/// </summary>
public class AvailabilityCampaign : Entity
{
    /// <summary>First local day of the advertised wide-open stretch.</summary>
    public DateOnly PeriodStart { get; private set; }

    /// <summary>Last local day of the advertised stretch (inclusive).</summary>
    public DateOnly PeriodEnd { get; private set; }

    /// <summary>Average projected occupancy of the stretch at send time, in percent.</summary>
    public int OccupancyPercent { get; private set; }

    public int RecipientCount { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    private AvailabilityCampaign() { }

    public AvailabilityCampaign(DateOnly periodStart, DateOnly periodEnd, int occupancyPercent, int recipientCount, DateTimeOffset createdAtUtc)
    {
        PeriodStart = periodStart;
        PeriodEnd = periodEnd;
        OccupancyPercent = occupancyPercent;
        RecipientCount = recipientCount;
        CreatedAtUtc = createdAtUtc;
    }
}
