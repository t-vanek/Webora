namespace D3Parking.Domain.Parking;

/// <summary>The reservation lifecycle state machine: which status transitions are permitted.</summary>
public static class ReservationStatusTransitions
{
    private static readonly IReadOnlyDictionary<ReservationStatus, IReadOnlySet<ReservationStatus>> Allowed =
        new Dictionary<ReservationStatus, IReadOnlySet<ReservationStatus>>
        {
            [ReservationStatus.Reserved] = new HashSet<ReservationStatus>
            {
                ReservationStatus.CheckedIn,
                ReservationStatus.Released,
                ReservationStatus.NoShow,
                ReservationStatus.Cancelled,
            },
            [ReservationStatus.CheckedIn] = new HashSet<ReservationStatus>
            {
                ReservationStatus.Completed,
            },
            [ReservationStatus.Completed] = new HashSet<ReservationStatus>(),
            [ReservationStatus.Released] = new HashSet<ReservationStatus>(),
            [ReservationStatus.NoShow] = new HashSet<ReservationStatus>(),
            [ReservationStatus.Cancelled] = new HashSet<ReservationStatus>(),
        };

    public static bool IsAllowed(ReservationStatus from, ReservationStatus to) =>
        Allowed.TryGetValue(from, out var targets) && targets.Contains(to);
}
