using D3Parking.Domain.Common;

namespace D3Parking.Domain.Email;

public enum EmailDeliveryStatus { Pending, Sent, Failed }

/// <summary>Durable transport work; the encrypted payload may contain account recovery tokens.</summary>
public sealed class EmailDelivery : Entity
{
    public string? ProtectedPayload { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public DateTimeOffset ExpiresAtUtc { get; private set; }
    public DateTimeOffset NextAttemptUtc { get; private set; }
    public EmailDeliveryStatus Status { get; private set; }
    public int Attempts { get; private set; }
    public Guid? LeaseId { get; private set; }
    public DateTimeOffset? LeasedUntilUtc { get; private set; }
    public DateTimeOffset? CompletedAtUtc { get; private set; }
    public string? LastError { get; private set; }

    private EmailDelivery() { }

    public EmailDelivery(string protectedPayload, DateTimeOffset now)
    {
        ProtectedPayload = protectedPayload;
        CreatedAtUtc = NextAttemptUtc = now;
        ExpiresAtUtc = now.AddDays(1);
    }
}
