using D3Parking.Domain.Accounts;

namespace D3Parking.Application.Accounts;

/// <summary>
/// Reads the account audit trail across every account.
/// </summary>
/// <remarks>
/// The events were already being written; until now the only way to see any of them was to open
/// one user's detail page, which cannot answer the questions the trail exists for — "who changed
/// what today", "who has been handing out roles".
/// </remarks>
public interface IAuditService
{
    Task<IReadOnlyList<AuditLogEntry>> SearchAsync(
        string? search,
        AccountAuditEventType? type,
        int limit,
        CancellationToken cancellationToken = default);
}

/// <summary>One audit event, carrying the subject account's identity for display.</summary>
public sealed record AuditLogEntry(
    Guid Id,
    Guid UserId,
    string? UserEmail,
    string? UserDisplayName,
    AccountAuditEventType Type,
    DateTimeOffset OccurredAtUtc,
    string Actor,
    string? Detail);
