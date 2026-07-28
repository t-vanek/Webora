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

    public DateTimeOffset? CheckedInAtUtc { get; private set; }

    public DateTimeOffset? ReleasedAtUtc { get; private set; }

    public DateTimeOffset? CompletedAtUtc { get; private set; }

    /// <summary>When the "confirm arrival or release" reminder was sent, so it is sent only once.</summary>
    public DateTimeOffset? ReminderSentAtUtc { get; private set; }

    /// <summary>Credits debited at booking; refunded in full on an early enough cancel or release.</summary>
    public int CreditsCharged { get; private set; }

    /// <summary>True when this booking was claimed from the waitlist; a no-show on it is punished harder.</summary>
    public bool FromQueue { get; private set; }

    private Reservation() { }

    public Reservation(Guid spotId, Guid userId, DateTimeOffset startUtc, DateTimeOffset endUtc, bool isOffPeak, DateTimeOffset createdAtUtc, int creditsCharged = 0, bool fromQueue = false)
    {
        if (endUtc <= startUtc)
            throw new ArgumentException("Reservation end must be after its start.", nameof(endUtc));

        SpotId = spotId;
        UserId = userId;
        StartUtc = startUtc;
        EndUtc = endUtc;
        IsOffPeak = isOffPeak;
        CreatedAtUtc = createdAtUtc;
        CreditsCharged = creditsCharged;
        FromQueue = fromQueue;
    }

    /// <summary>Whether the two windows overlap on the same spot (used to prevent double-booking).</summary>
    public bool Overlaps(DateTimeOffset startUtc, DateTimeOffset endUtc) =>
        startUtc < EndUtc && endUtc > StartUtc;

    public void CheckIn(DateTimeOffset at)
    {
        TransitionTo(ReservationStatus.CheckedIn);
        CheckedInAtUtc = at;
    }

    /// <summary>The holder gives the spot up ahead of time so someone else can take it.</summary>
    public void Release(DateTimeOffset at)
    {
        TransitionTo(ReservationStatus.Released);
        ReleasedAtUtc = at;
    }

    public void Cancel()
    {
        TransitionTo(ReservationStatus.Cancelled);
    }

    public void MarkNoShow()
    {
        TransitionTo(ReservationStatus.NoShow);
    }

    public void Complete(DateTimeOffset at)
    {
        TransitionTo(ReservationStatus.Completed);
        CompletedAtUtc = at;
    }

    public void MarkReminderSent(DateTimeOffset at) => ReminderSentAtUtc ??= at;

    private void TransitionTo(ReservationStatus target)
    {
        if (!ReservationStatusTransitions.IsAllowed(Status, target))
            throw new InvalidOperationException($"Cannot move a reservation from {Status} to {target}.");

        Status = target;
    }
}
