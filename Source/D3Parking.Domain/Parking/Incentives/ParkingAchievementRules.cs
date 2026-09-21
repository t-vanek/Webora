namespace D3Parking.Domain.Parking.Incentives;

/// <summary>
/// Positive-only milestones. They describe what a person contributed; none represents a low or
/// failing standing, and an earned achievement is permanent.
/// </summary>
public static class ParkingAchievementRules
{
    private static readonly IReadOnlySet<ParkingBadge> PositiveAchievements = new HashSet<ParkingBadge>
    {
        ParkingBadge.PlanningStarted,
        ParkingBadge.ActivePlanner,
        ParkingBadge.PlaceForColleague,
        ParkingBadge.ParkingHelper,
        ParkingBadge.BigHelper,
        ParkingBadge.FreeSpotHero,
        ParkingBadge.QueueHelper,
        ParkingBadge.ShortensWaiting,
        ParkingBadge.SharesWhenPossible,
        ParkingBadge.GenerousResident,
    };

    public static bool IsPositiveAchievement(ParkingBadge badge) => PositiveAchievements.Contains(badge);

    public static IEnumerable<ParkingBadge> ForPlans(int count)
    {
        if (count >= 1) yield return ParkingBadge.PlanningStarted;
        if (count >= 10) yield return ParkingBadge.ActivePlanner;
    }

    public static IEnumerable<ParkingBadge> ForUsefulReleases(int count)
    {
        if (count >= 1) yield return ParkingBadge.PlaceForColleague;
        if (count >= 5) yield return ParkingBadge.ParkingHelper;
        if (count >= 15) yield return ParkingBadge.BigHelper;
        if (count >= 30) yield return ParkingBadge.FreeSpotHero;
    }

    public static IEnumerable<ParkingBadge> ForQueueHelps(int count)
    {
        if (count >= 1) yield return ParkingBadge.QueueHelper;
        if (count >= 5) yield return ParkingBadge.ShortensWaiting;
    }

    public static IEnumerable<ParkingBadge> ForResidentSharesUsed(int count)
    {
        if (count >= 1) yield return ParkingBadge.SharesWhenPossible;
        if (count >= 10) yield return ParkingBadge.GenerousResident;
    }
}
