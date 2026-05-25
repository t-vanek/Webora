using Webora.Application.Accounts;

namespace Webora.Application.Administration;

/// <summary>
/// Administrative management of user accounts: listing, creation, deletion and role assignment.
/// Status changes (block/unblock) live on <see cref="IAccountService"/>. Every mutating operation
/// carries the acting administrator's id for auditing and self-action guards.
/// </summary>
public interface IUserAdminService
{
    Task<IReadOnlyList<UserSummary>> ListAsync(string? search, CancellationToken cancellationToken = default);

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
