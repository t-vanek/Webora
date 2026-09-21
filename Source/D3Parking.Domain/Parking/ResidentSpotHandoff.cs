using D3Parking.Domain.Common;

namespace D3Parking.Domain.Parking;

public enum ResidentSpotHandoffKind
{
    ResidentOffer,
    UserRequest,
}

public enum ResidentSpotHandoffStatus
{
    PendingResident,
    Offered,
    Accepted,
    Declined,
    Cancelled,
    Expired,
    Superseded,
}

/// <summary>
/// A private, one-off handoff of resident capacity to a named user. Pending handoffs do not release
/// the spot into the shared pool; an accepted handoff points at the ordinary reservation created for
/// the recipient, so pricing, planner limits and calendar behaviour stay in one place.
/// </summary>
public sealed class ResidentSpotHandoff : Entity
{
    public Guid SpotId { get; private set; }

    public Guid ResidentId { get; private set; }

    public Guid RecipientId { get; private set; }

    public ResidentSpotHandoffKind Kind { get; private set; }

    public ResidentSpotHandoffStatus Status { get; private set; }

    public DateTimeOffset StartUtc { get; private set; }

    public DateTimeOffset EndUtc { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public DateTimeOffset ExpiresAtUtc { get; private set; }

    public DateTimeOffset? RespondedAtUtc { get; private set; }

    /// <summary>
    /// The gross planning price the requester approved when sending a request. A later approval may
    /// charge less (including an automatic voucher), but never more without a new request.
    /// </summary>
    public int? MaxCreditsAuthorized { get; private set; }

    public Guid? ReservationId { get; private set; }

    public bool IsActive => Status is ResidentSpotHandoffStatus.PendingResident or ResidentSpotHandoffStatus.Offered;

    private ResidentSpotHandoff() { }

    private ResidentSpotHandoff(
        Guid spotId,
        Guid residentId,
        Guid recipientId,
        ResidentSpotHandoffKind kind,
        ResidentSpotHandoffStatus status,
        DateTimeOffset startUtc,
        DateTimeOffset endUtc,
        DateTimeOffset createdAtUtc,
        DateTimeOffset expiresAtUtc,
        int? maxCreditsAuthorized)
    {
        if (residentId == recipientId)
            throw new ArgumentException("A resident cannot hand a spot to themselves.", nameof(recipientId));
        if (endUtc <= startUtc)
            throw new ArgumentException("Handoff end must be after its start.", nameof(endUtc));
        if (expiresAtUtc <= createdAtUtc || expiresAtUtc > endUtc)
            throw new ArgumentException("Handoff expiry must be after creation and no later than its end.", nameof(expiresAtUtc));

        SpotId = spotId;
        ResidentId = residentId;
        RecipientId = recipientId;
        Kind = kind;
        Status = status;
        StartUtc = startUtc;
        EndUtc = endUtc;
        CreatedAtUtc = createdAtUtc;
        ExpiresAtUtc = expiresAtUtc;
        MaxCreditsAuthorized = maxCreditsAuthorized;
    }

    public static ResidentSpotHandoff CreateOffer(
        Guid spotId, Guid residentId, Guid recipientId,
        DateTimeOffset startUtc, DateTimeOffset endUtc,
        DateTimeOffset createdAtUtc, DateTimeOffset expiresAtUtc) =>
        new(spotId, residentId, recipientId, ResidentSpotHandoffKind.ResidentOffer,
            ResidentSpotHandoffStatus.Offered, startUtc, endUtc, createdAtUtc, expiresAtUtc, null);

    public static ResidentSpotHandoff CreateRequest(
        Guid spotId, Guid residentId, Guid recipientId,
        DateTimeOffset startUtc, DateTimeOffset endUtc,
        DateTimeOffset createdAtUtc, DateTimeOffset expiresAtUtc, int maxCreditsAuthorized) =>
        new(spotId, residentId, recipientId, ResidentSpotHandoffKind.UserRequest,
            ResidentSpotHandoffStatus.PendingResident, startUtc, endUtc, createdAtUtc, expiresAtUtc,
            Math.Max(0, maxCreditsAuthorized));

    public void Accept(Guid reservationId, DateTimeOffset at)
    {
        if (!IsActive)
            throw new InvalidOperationException($"Cannot accept a {Status} handoff.");

        Status = ResidentSpotHandoffStatus.Accepted;
        ReservationId = reservationId;
        RespondedAtUtc = at;
    }

    public void Decline(DateTimeOffset at)
    {
        if (!IsActive)
            throw new InvalidOperationException($"Cannot decline a {Status} handoff.");
        Status = ResidentSpotHandoffStatus.Declined;
        RespondedAtUtc = at;
    }

    public void Cancel(DateTimeOffset at)
    {
        if (!IsActive)
            throw new InvalidOperationException($"Cannot cancel a {Status} handoff.");
        Status = ResidentSpotHandoffStatus.Cancelled;
        RespondedAtUtc = at;
    }

    public void Expire(DateTimeOffset at)
    {
        if (!IsActive)
            return;
        Status = ResidentSpotHandoffStatus.Expired;
        RespondedAtUtc = at;
    }

    public void Supersede(DateTimeOffset at)
    {
        if (!IsActive)
            return;
        Status = ResidentSpotHandoffStatus.Superseded;
        RespondedAtUtc = at;
    }
}
