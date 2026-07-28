using D3Parking.Domain.Common;

namespace D3Parking.Domain.Accounts;

/// <summary>An immutable record of a single account lifecycle or credential event.</summary>
public class AccountAuditEvent : Entity
{
    public Guid UserId { get; private set; }

    public AccountAuditEventType Type { get; private set; }

    public DateTimeOffset OccurredAtUtc { get; private set; }

    /// <summary>Who performed the action: "self", "admin:&lt;id&gt;" or "system".</summary>
    public string Actor { get; private set; } = "system";

    public string? Detail { get; private set; }

    private AccountAuditEvent() { }

    public AccountAuditEvent(Guid userId, AccountAuditEventType type, string actor, string? detail, DateTimeOffset occurredAtUtc)
    {
        UserId = userId;
        Type = type;
        Actor = actor;
        Detail = detail;
        OccurredAtUtc = occurredAtUtc;
    }
}
