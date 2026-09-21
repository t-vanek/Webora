using D3Parking.Domain.Common;

namespace D3Parking.Domain.Parking;

/// <summary>
/// One sent occupancy campaign. Doubles as the dedup record and as the base for measuring whether
/// low-occupancy suggestions convert into bookings or high-occupancy reminders free capacity.
/// </summary>
public class AvailabilityCampaign : Entity
{
    public AvailabilityCampaignKind Kind { get; private set; }

    /// <summary>Local day on which this campaign was selected for sending.</summary>
    public DateOnly CampaignDate { get; private set; }

    /// <summary>First local day of the advertised occupancy stretch.</summary>
    public DateOnly PeriodStart { get; private set; }

    /// <summary>Last local day of the advertised stretch (inclusive).</summary>
    public DateOnly PeriodEnd { get; private set; }

    /// <summary>Average projected occupancy of the stretch at send time, in percent.</summary>
    public int OccupancyPercent { get; private set; }

    public int RecipientCount { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    private AvailabilityCampaign() { }

    public AvailabilityCampaign(AvailabilityCampaignKind kind, DateOnly campaignDate,
        DateOnly periodStart, DateOnly periodEnd, int occupancyPercent, int recipientCount,
        DateTimeOffset createdAtUtc)
    {
        Kind = kind;
        CampaignDate = campaignDate;
        PeriodStart = periodStart;
        PeriodEnd = periodEnd;
        OccupancyPercent = occupancyPercent;
        RecipientCount = recipientCount;
        CreatedAtUtc = createdAtUtc;
    }
}
