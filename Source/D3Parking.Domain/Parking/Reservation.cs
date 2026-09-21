using D3Parking.Domain.Common;

namespace D3Parking.Domain.Parking;

/// <summary>A booking of a single <see cref="ParkingSpot"/> for a time window by one user.</summary>
public class Reservation : Entity
{
    public Guid SpotId { get; private set; }

    public Guid UserId { get; private set; }

    public DateTimeOffset StartUtc { get; private set; }

    public DateTimeOffset EndUtc { get; private set; }

    public ReservationStatus Status { get; private set; } = ReservationStatus.Reserved;

    /// <summary>Captured at booking time: whether this window avoids the high-demand peak (rewarded).</summary>
    public bool IsOffPeak { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    /// <summary>
    /// Monotonically increasing revision exposed to subscribed calendars. Keeping the same UID and
    /// increasing SEQUENCE lets calendar clients replace an older copy after a move or cancellation.
    /// </summary>
    public int CalendarSequence { get; private set; }

    /// <summary>The instant of the latest change that affects the calendar representation.</summary>
    public DateTimeOffset CalendarUpdatedAtUtc { get; private set; }

    public DateTimeOffset? CheckedInAtUtc { get; private set; }

    public DateTimeOffset? ReleasedAtUtc { get; private set; }

    public DateTimeOffset? CompletedAtUtc { get; private set; }

    /// <summary>When the "confirm arrival or release" reminder was sent, so it is sent only once.</summary>
    public DateTimeOffset? ReminderSentAtUtc { get; private set; }

    /// <summary>Credits debited at booking; refunded in full on an early enough cancel or release.</summary>
    public int CreditsCharged { get; private set; }

    /// <summary>Captured refund terms; null only for bookings created before this field existed.</summary>
    public DateTimeOffset? RefundDeadlineUtc { get; private set; }

    /// <summary>True when this booking was claimed from the waitlist; a no-show on it is punished harder.</summary>
    public bool FromQueue { get; private set; }

    /// <summary>
    /// Captured at booking time. Reservations that use a resident's own allocation are an
    /// entitlement; reservations from shared capacity consume the person's weekly quota.
    /// </summary>
    public bool CountsTowardWeeklyLimit { get; private set; } = true;

    /// <summary>
    /// Resident who released the capacity used by this booking, captured at booking time so later
    /// membership changes cannot rewrite trust, rewards or collusion history.
    /// </summary>
    public Guid? SharedByResidentId { get; private set; }

    private Reservation() { }

    public Reservation(Guid spotId, Guid userId, DateTimeOffset startUtc, DateTimeOffset endUtc, bool isOffPeak,
        DateTimeOffset createdAtUtc, int creditsCharged = 0, bool fromQueue = false,
        bool countsTowardWeeklyLimit = true, DateTimeOffset? refundDeadlineUtc = null)
    {
        if (endUtc <= startUtc)
            throw new ArgumentException("Reservation end must be after its start.", nameof(endUtc));

        SpotId = spotId;
        SharedByResidentId = null;
        UserId = userId;
        StartUtc = startUtc;
        EndUtc = endUtc;
        IsOffPeak = isOffPeak;
        CreatedAtUtc = createdAtUtc;
        CalendarUpdatedAtUtc = createdAtUtc;
        CreditsCharged = creditsCharged;
        RefundDeadlineUtc = refundDeadlineUtc;
        FromQueue = fromQueue;
        CountsTowardWeeklyLimit = countsTowardWeeklyLimit;
    }

    /// <summary>Whether the two windows overlap on the same spot (used to prevent double-booking).</summary>
    public bool Overlaps(DateTimeOffset startUtc, DateTimeOffset endUtc) =>
        startUtc < EndUtc && endUtc > StartUtc;

    public bool IsProtectedFromDisplacement(DateTimeOffset now) =>
        ReservationWindowRules.IsProtectedFromDisplacement(StartUtc, EndUtc, Status, now);

    /// <summary>
    /// Re-points a future booking at another spot, keeping its window, price and history. Nothing
    /// is refunded or re-charged and any voucher is preserved. Started, checked-in and finished
    /// bookings cannot be moved, even if a caller omits the application-level protection check.
    /// </summary>
    public void MoveTo(Guid spotId, DateTimeOffset at)
    {
        if (Status != ReservationStatus.Reserved || StartUtc <= at)
            throw new InvalidOperationException("Only a future, unstarted reservation can be moved.");

        SpotId = spotId;
        SharedByResidentId = null;
        MarkCalendarChanged(at);
    }

    public void CheckIn(DateTimeOffset at)
    {
        TransitionTo(ReservationStatus.CheckedIn);
        CheckedInAtUtc = at;
        MarkCalendarChanged(at);
    }

    /// <summary>The holder gives the spot up ahead of time so someone else can take it.</summary>
    public void Release(DateTimeOffset at)
    {
        TransitionTo(ReservationStatus.Released);
        ReleasedAtUtc = at;
        MarkCalendarChanged(at);
    }

    public void Cancel(DateTimeOffset at)
    {
        TransitionTo(ReservationStatus.Cancelled);
        MarkCalendarChanged(at);
    }

    public void MarkNoShow(DateTimeOffset at)
    {
        TransitionTo(ReservationStatus.NoShow);
        MarkCalendarChanged(at);
    }

    public void Complete(DateTimeOffset at)
    {
        TransitionTo(ReservationStatus.Completed);
        CompletedAtUtc = at;
        MarkCalendarChanged(at);
    }

    public void MarkReminderSent(DateTimeOffset at) => ReminderSentAtUtc ??= at;

    public void AttributeSharedCapacity(Guid? residentId) => SharedByResidentId = residentId;

    private void TransitionTo(ReservationStatus target)
    {
        if (!ReservationStatusTransitions.IsAllowed(Status, target))
            throw new InvalidOperationException($"Cannot move a reservation from {Status} to {target}.");

        Status = target;
    }

    private void MarkCalendarChanged(DateTimeOffset at)
    {
        CalendarSequence++;
        CalendarUpdatedAtUtc = at;
    }
}
