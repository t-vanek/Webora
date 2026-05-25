using Webora.Domain.Accounts;

namespace Webora.Application.Administration;

/// <summary>A single row in the user administration list.</summary>
public sealed record UserSummary(
    Guid Id,
    string Email,
    string? DisplayName,
    AccountStatus Status,
    IReadOnlyList<string> Roles);

/// <summary>Full detail of a user shown on the administration detail page.</summary>
public sealed record UserDetail(
    Guid Id,
    string Email,
    string? DisplayName,
    string? PhoneNumber,
    AccountStatus Status,
    bool EmailConfirmed,
    DateTimeOffset? StatusChangedAtUtc,
    string? StatusReason,
    IReadOnlyList<string> Roles);
