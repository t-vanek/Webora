using Webora.Domain.Accounts;

namespace Webora.Application.Accounts;

public sealed record AccountAuditEntry(
    Guid Id,
    Guid UserId,
    AccountAuditEventType Type,
    DateTimeOffset OccurredAtUtc,
    string Actor,
    string? Detail);
