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
    /// <summary>
    /// One page of the trail, newest first, narrowed by an optional free-text term and event type.
    /// </summary>
    /// <remarks>
    /// Paged rather than capped: the trail is append-only and the questions asked of it ("who was
    /// handing out roles in March") are answered by rows that a cap puts out of reach. A capped read
    /// cannot even say it happened — a full page of results looks the same whether it is the whole
    /// answer or the first slice of one.
    /// </remarks>
    Task<PagedResult<AuditLogEntry>> SearchAsync(
        string? search,
        AccountAuditEventType? type,
        int pageIndex,
        int pageSize,
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
