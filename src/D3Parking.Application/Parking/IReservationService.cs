namespace D3Parking.Application.Parking;

/// <summary>
/// Plans parking time windows and manages their budget. A plan stays reserved for its whole window
/// and becomes history by time; no physical arrival or departure confirmation is required.
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

    /// <summary>
    /// The caller's reservations. Residency and ownership of a parking spot never filter this list;
    /// with <paramref name="upcomingOnly"/>, both currently active and future plans are returned.
    /// </summary>
    Task<IReadOnlyList<ReservationDto>> GetMyReservationsAsync(Guid userId, bool upcomingOnly = false, CancellationToken cancellationToken = default);

    /// <summary>
    /// One page of the caller's whole booking history, newest first. Exists because the unpaged read
    /// above silently stops at its cap: a driver with more bookings than that had no way to reach the
    /// older ones, and no way to tell they were being hidden. Page size is clamped server-side.
    /// </summary>
    Task<PagedResult<ReservationDto>> GetMyReservationsPageAsync(Guid userId, int pageIndex, int pageSize, CancellationToken cancellationToken = default);

    /// <summary>A single reservation of the caller (ownership enforced), e.g. for the calendar export.</summary>
    Task<ReservationDto?> GetMyReservationAsync(Guid userId, Guid reservationId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Books a spot. With <paramref name="useVoucher"/> the caller's apology voucher covers the
    /// whole dynamic price (peak included) instead of the wallet.
    /// </summary>
    Task<ParkingResult> ReserveAsync(Guid userId, Guid spotId, DateTimeOffset startUtc, DateTimeOffset endUtc, bool useVoucher = false, CancellationToken cancellationToken = default);

    /// <summary>
    /// The caller's live apology voucher, or null: an approved one (redeemable for one free
    /// reservation) wins over a pending one still awaiting the spot manager's review.
    /// </summary>
    Task<ApologyVoucherDto?> GetMyApologyVoucherAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>The holder removes the plan; an early release returns its planning budget.</summary>
    Task<ParkingResult> ReleaseAsync(Guid userId, Guid reservationId, CancellationToken cancellationToken = default);

    Task<ParkingResult> CancelAsync(Guid userId, Guid reservationId, CancellationToken cancellationToken = default);

    /// <summary>
    /// The holder arrived and cannot physically park (the spot is blocked by another car).
    /// Records an occupancy mismatch (optionally with the blocking car's license plate), voids the
    /// reservation penalty-free with a full refund, and — when <paramref name="relocate"/> is set —
    /// books the first free spot for the same window with the original charge carried over.
    /// The <paramref name="photo"/> proof is mandatory and single-use: a picture whose bytes were
    /// ever used for another report is refused. Any apology voucher is granted pending the spot
    /// manager's approval.
    /// </summary>
    Task<BlockedSpotOutcome> ReportBlockedSpotAsync(Guid userId, Guid reservationId, bool relocate, BlockedSpotPhoto? photo, string? blockerPlate = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends a one-time planning reminder for reservations whose start is near. The reminder is
    /// informational and offers the holder a chance to review or release the booking; it never asks
    /// them to confirm physical arrival and never triggers a no-show penalty.
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
    /// <summary>
    /// The occupancy of today's peak window (Completed included), or null while the window has
    /// not finished yet — the adaptive controller must judge a whole peak, not a live moment.
    /// </summary>
    Task<double?> MeasurePeakOccupancyAsync(CancellationToken cancellationToken = default);

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
