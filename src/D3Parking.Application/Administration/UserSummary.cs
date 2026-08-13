using D3Parking.Domain.Accounts;

namespace D3Parking.Application.Administration;

/// <summary>A single row in the user administration list.</summary>
public sealed record UserSummary(
    Guid Id,
    string Email,
    string? DisplayName,
    AccountStatus Status,
    IReadOnlyList<string> Roles,
    bool EmailConfirmed,
    string? ExternalProvider,
    DateTimeOffset? LastActivityUtc)
{
    public bool IsFederated => ExternalProvider is not null;
}

public enum UserAccountSourceFilter
{
    All,
    Local,
    Federated,
}

public enum UserListSort
{
    Email,
    Name,
    Status,
    RecentActivity,
}

public sealed record UserListQuery(
    string? Search = null,
    AccountStatus? Status = null,
    string? Role = null,
    UserAccountSourceFilter Source = UserAccountSourceFilter.All,
    UserListSort Sort = UserListSort.Email);

public sealed record UserDirectorySummary(
    int Total,
    int Active,
    int PendingActivation,
    int Blocked);

/// <summary>Full detail of a user shown on the administration detail page.</summary>
/// <param name="IsLastAdministrator">
/// True when this account is the only administrator who could still sign in. The service refuses
/// to take the role away in that case; the flag lets the screen say so up front instead of letting
/// someone uncheck the box and find out on save.
/// </param>
public sealed record UserDetail(
    Guid Id,
    string Email,
    string? DisplayName,
    string? PhoneNumber,
    AccountStatus Status,
    bool EmailConfirmed,
    DateTimeOffset? StatusChangedAtUtc,
    string? StatusReason,
    IReadOnlyList<string> Roles,
    bool IsLastAdministrator,
    string? ExternalProvider = null,
    DateTimeOffset? ExternalSyncedAtUtc = null)
{
    /// <summary>True when sign-in and role assignment are driven by an external directory.</summary>
    public bool IsFederated => ExternalProvider is not null;
}
