using D3Parking.Domain.Parking.Incentives;

namespace D3Parking.Application.Parking;

/// <summary>Recorded reservations, not measured physical occupancy. Capacity uses currently active spots.</summary>
public sealed record PlanningAnalysisDto(int ActiveSpots, IReadOnlyList<PlanningDayDto> Days)
{
    public IReadOnlyList<PlanningChartBucket> LeadTimes { get; init; } = [];
    public IReadOnlyList<PlanningChartBucket> Outcomes { get; init; } = [];
    public IncentivePolicy Policy { get; init; } = IncentivePolicy.Default;
    public DateOnly Today { get; init; }
    public DateOnly LastBookableDate => Today.AddDays(Math.Clamp(Policy.ReservationHorizonDays, 1, 366));
    public bool IsHistory => Days.All(d => d.Date < Today);
    // Historical rules are not versioned. Do not apply today's calendar retrospectively.
    public IReadOnlyList<PlanningDayDto> AverageDays => Days
        .Where(d => d.Date < Today || Availability(d.Date) == ReservationDateAvailability.Allowed).ToList();
    public double? AverageBookedSpots => AverageDays.Count == 0 ? null : AverageDays.Average(d => d.BookedSpots);
    public ReservationDateAvailability Availability(DateOnly date) => Policy.GetReservationDateAvailability(date, Today);
}

public sealed record PlanningChartBucket(string Key, int Count);

public sealed record PlanningDayDto(DateOnly Date, int BookedSpots, int WaitingRequests)
{
    public int BookingPercent(int capacity) =>
        capacity <= 0 ? 0 : (int)Math.Round(BookedSpots * 100.0 / capacity);
}
