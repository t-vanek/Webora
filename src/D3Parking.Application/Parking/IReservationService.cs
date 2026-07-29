namespace D3Parking.Application.Parking;

/// <summary>
/// Booking and lifecycle of reservations. Mutating operations apply the incentive rules
/// (off-peak bonus on booking, reward on an in-time release, penalty on a no-show) as part of the
/// same transaction.
/// </summary>
public interface IReservationService
{
    /// <summary>Active spots with no conflicting reservation in the given window.</summary>
    Task<IReadOnlyList<ParkingSpotDto>> GetAvailableSpotsAsync(DateTimeOffset startUtc, DateTimeOffset endUtc, CancellationToken cancellationToken = default);

    /// <summary>
    /// The dynamic credit price to book in the given window (peak surcharge × projected occupancy of
    /// the lot for that window), together with the user's spendable balance and whether they can pay.
    /// </summary>
    Task<ReservationQuoteDto> GetQuoteAsync(Guid userId, DateTimeOffset startUtc, DateTimeOffset endUtc, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ReservationDto>> GetMyReservationsAsync(Guid userId, bool upcomingOnly = false, CancellationToken cancellationToken = default);

    /// <summary>
    /// Books a spot. With <paramref name="useVoucher"/> the caller's apology voucher covers the
    /// whole dynamic price (peak included) instead of the wallet.
    /// </summary>
    Task<ParkingResult> ReserveAsync(Guid userId, Guid spotId, DateTimeOffset startUtc, DateTimeOffset endUtc, bool useVoucher = false, CancellationToken cancellationToken = default);

    /// <summary>The caller's usable apology voucher (one free reservation), or null.</summary>
    Task<ApologyVoucherDto?> GetMyApologyVoucherAsync(Guid userId, CancellationToken cancellationToken = default);

    Task<ParkingResult> CheckInAsync(Guid userId, Guid reservationId, CancellationToken cancellationToken = default);

    /// <summary>The holder used the spot and is now leaving; closes the reservation.</summary>
    Task<ParkingResult> CompleteAsync(Guid userId, Guid reservationId, CancellationToken cancellationToken = default);

    /// <summary>The holder gives the spot up; rewarded when done early enough to free it.</summary>
    Task<ParkingResult> ReleaseAsync(Guid userId, Guid reservationId, CancellationToken cancellationToken = default);

    Task<ParkingResult> CancelAsync(Guid userId, Guid reservationId, CancellationToken cancellationToken = default);

    /// <summary>
    /// The holder arrived and cannot physically park (the spot is blocked by another car).
    /// Records an occupancy mismatch, voids the reservation penalty-free with a full refund, and —
    /// when <paramref name="relocate"/> is set — books the first free spot for the same window
    /// with the original charge carried over.
    /// </summary>
    Task<BlockedSpotOutcome> ReportBlockedSpotAsync(Guid userId, Guid reservationId, bool relocate, CancellationToken cancellationToken = default);

    /// <summary>
    /// Marks every still-reserved booking whose grace period has elapsed as a no-show and applies the
    /// penalty. Intended to be run on a schedule; also exposed for an administrator to trigger.
    /// Returns the number of reservations resolved.
    /// </summary>
    Task<int> SweepNoShowsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends a one-time "confirm arrival or release" reminder for reservations whose start is near and
    /// that have not been checked in or released yet. Intended to be run on a schedule.
    /// Returns the number of reminders sent.
    /// </summary>
    Task<int> SendDueRemindersAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Tops every user's wallet up with the monthly credit allowance, once per calendar month.
    /// Intended to be run on a schedule. Returns the number of wallets granted this run.
    /// </summary>
    Task<int> GrantDueMonthlyCreditsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Fades reputation toward zero once per decay interval so the score reflects recent behaviour.
    /// Intended to be run on a schedule. Returns the number of scores decayed this run.
    /// </summary>
    Task<int> DecayReputationAsync(CancellationToken cancellationToken = default);

    /// <summary>The projected occupancy of the lot during today's peak window (0–1), for the adaptive controller.</summary>
    Task<double> MeasurePeakOccupancyAsync(CancellationToken cancellationToken = default);

    /// <summary>The caller's active (and recent) waitlist entries with their queue position and any offer.</summary>
    Task<IReadOnlyList<QueueEntryDto>> GetMyQueueAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>Join the waitlist for a window. Allowed only when the window is currently full.</summary>
    Task<ParkingResult> JoinQueueAsync(Guid userId, DateTimeOffset startUtc, DateTimeOffset endUtc, CancellationToken cancellationToken = default);

    /// <summary>Leave the waitlist; releases any spot currently held for the entry.</summary>
    Task<ParkingResult> LeaveQueueAsync(Guid userId, Guid queueEntryId, CancellationToken cancellationToken = default);

    /// <summary>Claim the spot held for an offered waitlist entry by reserving it (charged as usual).</summary>
    Task<ParkingResult> ClaimQueueOfferAsync(Guid userId, Guid queueEntryId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Expires stale offers and past-window entries, then offers freed spots to the earliest waiting
    /// entries they fit. Runs on free-up events and on the maintenance loop. Returns offers made.
    /// </summary>
    Task<int> ProcessQueueAsync(CancellationToken cancellationToken = default);
}
