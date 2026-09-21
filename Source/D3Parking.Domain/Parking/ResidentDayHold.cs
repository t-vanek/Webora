using D3Parking.Domain.Common;

namespace D3Parking.Domain.Parking;

/// <summary>An explicit decision to keep a local day, independent of the recurring usage plan.</summary>
public sealed class ResidentDayHold : Entity
{
    public Guid SpotId { get; private set; }
    public Guid UserId { get; private set; }
    public DateOnly Date { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }

    private ResidentDayHold() { }

    public ResidentDayHold(Guid spotId, Guid userId, DateOnly date, DateTimeOffset createdAtUtc)
    {
        SpotId = spotId;
        UserId = userId;
        Date = date;
        CreatedAtUtc = createdAtUtc;
    }
}
