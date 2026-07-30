namespace D3Parking.Application.Parking;

/// <summary>
/// The spot manager's view of the lot: the board of spots with what each is doing, one spot's
/// calendar and history, utilization analytics, and the manual interventions for when the physical
/// lot and the system disagree. Read-only except for the two override operations at the bottom.
/// </summary>
public interface ILotDashboardService
{
    /// <summary>
    /// The board for one local day: lot-level counts plus every spot as a row, ordered by the section
    /// its code implies and then by that code read as a number. For today the rows reflect the live
    /// moment (who is checked in right now); for another day they reflect what is booked on it. Each
    /// row also carries its own recent load, so the table can show a sparkline per spot.
    /// </summary>
    Task<LotBoardDto> GetBoardAsync(DateOnly date, CancellationToken cancellationToken = default);

    /// <summary>
    /// The daily series the summary table draws. Deliberately not part of the board: it is always the
    /// last <see cref="LotBoard.SummaryTrendDays"/> days ending today and has nothing to do with the
    /// day being looked at, so stepping through days must not recompute it. Cached briefly — it is
    /// history, unlike the board, which is a live picture and never cached.
    /// </summary>
    Task<LotSummaryTrendsDto> GetSummaryTrendsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// One spot in full: its settings, the resident's plan, the calendar for
    /// <paramref name="days"/> days from <paramref name="from"/> (reservations, visitor bookings and
    /// released resident days), the blocked-spot reports against it, and its utilization. Null when
    /// the spot does not exist.
    /// </summary>
    Task<SpotDetailDto?> GetSpotDetailAsync(Guid spotId, DateOnly from, int days, CancellationToken cancellationToken = default);

    /// <summary>
    /// Utilization per spot over [from, to] (busiest first) and the weekday × hour demand map, so the
    /// bottleneck spots and the dead capacity are both visible. The window is inclusive and capped.
    /// Cached briefly per window: it scans every booking in the range, and the answer is history.
    /// </summary>
    Task<LotAnalyticsDto> GetAnalyticsAsync(DateOnly from, DateOnly to, CancellationToken cancellationToken = default);

    /// <summary>
    /// Active non-visitor spots free for the whole window of the given reservation, so the manager
    /// can move it somewhere real. Empty when the reservation cannot be moved (or does not exist).
    /// </summary>
    Task<IReadOnlyList<MoveTargetDto>> GetMoveTargetsAsync(Guid reservationId, CancellationToken cancellationToken = default);

    /// <summary>
    /// The manager calls a booking off on the holder's behalf — the lot disagreed with the system and
    /// the holder is not at fault, so the charge is refunded in full however late it is (and any
    /// voucher that paid for it comes back). The holder is notified and the override is audited.
    /// Only a not-yet-arrived (Reserved) booking can be cancelled; a checked-in one is moved instead.
    /// </summary>
    Task<ParkingResult> CancelReservationAsync(Guid reservationId, Guid actingUserId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Moves a booking to another spot for the same window, keeping its price, status and history —
    /// the booking is re-pointed, not re-made, so nothing moves in the wallet. Fails when the target
    /// is not free for the window. The holder is notified and the override is audited.
    /// </summary>
    Task<ParkingResult> MoveReservationAsync(Guid reservationId, Guid targetSpotId, Guid actingUserId, CancellationToken cancellationToken = default);
}
