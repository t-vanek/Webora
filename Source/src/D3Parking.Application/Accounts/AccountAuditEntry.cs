using D3Parking.Domain.Accounts;

namespace D3Parking.Application.Accounts;

public sealed record AccountAuditEntry(
    Guid Id,
    Guid UserId,
    AccountAuditEventType Type,
    DateTimeOffset OccurredAtUtc,
    string Actor,
    string? Detail);
