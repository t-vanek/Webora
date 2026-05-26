namespace Webora.Domain.Parking.Incentives;

/// <summary>The thresholds that turn accumulated behaviour into earned badges.</summary>
public static class ParkingBadgeRules
{
    public const int ConsiderateColleagueReleases = 5;
    public const int OffPeakChampionReservations = 10;
    public const int ReliableParkerCompletions = 20;
    public const int CenturyClubPoints = 100;

    /// <summary>Every badge the given standing currently qualifies for.</summary>
    public static IEnumerable<ParkingBadge> Earned(ParkerScore score)
    {
        if (score.ReservationsReleased >= ConsiderateColleagueReleases)
            yield return ParkingBadge.ConsiderateColleague;

        if (score.OffPeakReservations >= OffPeakChampionReservations)
            yield return ParkingBadge.OffPeakChampion;

        if (score.ReservationsCompleted >= ReliableParkerCompletions && score.NoShows == 0)
            yield return ParkingBadge.ReliableParker;

        if (score.Points >= CenturyClubPoints)
            yield return ParkingBadge.CenturyClub;
    }
}
