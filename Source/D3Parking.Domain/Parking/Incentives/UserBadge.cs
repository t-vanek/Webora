using D3Parking.Domain.Common;

namespace D3Parking.Domain.Parking.Incentives;

/// <summary>Records that a user has earned a particular badge. One row per user and badge.</summary>
public class UserBadge : Entity
{
    public Guid UserId { get; private set; }

    public ParkingBadge Badge { get; private set; }

    public DateTimeOffset AwardedAtUtc { get; private set; }

    private UserBadge() { }

    public UserBadge(Guid userId, ParkingBadge badge, DateTimeOffset awardedAtUtc)
    {
        UserId = userId;
        Badge = badge;
        AwardedAtUtc = awardedAtUtc;
    }
}
