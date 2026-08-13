using D3Parking.Application.Accounts;

namespace D3Parking.Application.Administration;

/// <summary>
/// Administrative management of user accounts: listing, creation, deletion and role assignment.
/// Status changes (block/unblock) live on <see cref="IAccountService"/>. Every mutating operation
/// carries the acting administrator's id for auditing and self-action guards.
/// </summary>
public interface IUserAdminService
{
    /// <summary>
    /// Accounts matching the search, capped. For filling a picker, where the whole directory is the
    /// point and the cap is a backstop; the administration list uses <see cref="ListPageAsync"/>,
    /// which can reach past it.
    /// </summary>
    Task<IReadOnlyList<UserSummary>> ListAsync(string? search, CancellationToken cancellationToken = default);

    /// <summary>
    /// One page of the accounts matching the search, by email. The total comes back with it, so a
    /// directory larger than one screen says how much larger — the capped read above cannot tell
    /// "these are all of them" apart from "here are the first five hundred".
    /// </summary>
    Task<PagedResult<UserSummary>> ListPageAsync(
        string? search,
        int pageIndex,
        int pageSize,
        CancellationToken cancellationToken = default);

    Task<PagedResult<UserSummary>> ListFilteredPageAsync(
        UserListQuery query,
        int pageIndex,
        int pageSize,
        CancellationToken cancellationToken = default);

    Task<UserDirectorySummary> GetSummaryAsync(CancellationToken cancellationToken = default);

    Task<UserDetail?> GetAsync(Guid userId, CancellationToken cancellationToken = default);

    Task<AccountResult> CreateAsync(
        string email,
        string? displayName,
        string password,
        IReadOnlyList<string> roles,
        Guid adminId,
        CancellationToken cancellationToken = default);

    Task<AccountResult> SetRolesAsync(
        Guid userId,
        IReadOnlyList<string> roles,
        Guid adminId,
        CancellationToken cancellationToken = default);

    Task<AccountResult> DeleteAsync(Guid userId, Guid adminId, CancellationToken cancellationToken = default);
}
