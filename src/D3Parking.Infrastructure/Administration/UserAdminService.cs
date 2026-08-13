using System.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using D3Parking.Application;
using D3Parking.Application.Accounts;
using D3Parking.Application.Administration;
using D3Parking.Domain.Accounts;
using D3Parking.Domain.Authorization;
using D3Parking.Domain.Parking;
using D3Parking.Infrastructure.Identity;
using D3Parking.Infrastructure.Persistence;

namespace D3Parking.Infrastructure.Administration;

public sealed class UserAdminService(
    UserManager<ApplicationUser> userManager,
    // The scoped context is the one UserManager's store writes through; transactions that must
    // cover its saves (role swaps, the last-administrator guard) have to open on this instance.
    // The factory below serves independent reads/writes.
    D3ParkingDbContext identityDbContext,
    IDbContextFactory<D3ParkingDbContext> dbContextFactory,
    IStringLocalizer<AccountMessages> messages,
    TimeProvider timeProvider,
    ILogger<UserAdminService> logger) : IUserAdminService
{
    private const int ListLimit = 500;

    public async Task<IReadOnlyList<UserSummary>> ListAsync(string? search, CancellationToken cancellationToken = default)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);

        return await SummariseAsync(
            dbContext,
            Matching(dbContext, search).OrderBy(u => u.Email).Take(ListLimit),
            cancellationToken);
    }

    public async Task<PagedResult<UserSummary>> ListPageAsync(
        string? search,
        int pageIndex,
        int pageSize,
        CancellationToken cancellationToken = default) =>
        await ListFilteredPageAsync(new UserListQuery(Search: search), pageIndex, pageSize, cancellationToken);

    public async Task<PagedResult<UserSummary>> ListFilteredPageAsync(
        UserListQuery query,
        int pageIndex,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        // Clamped here, not trusted from the caller: the page size decides how much a single request
        // can ask the database to materialise.
        var size = Math.Clamp(pageSize, 1, 100);
        var index = Math.Max(0, pageIndex);

        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var matching = Matching(dbContext, query);

        var total = await matching.CountAsync(cancellationToken);
        if (total == 0)
        {
            return PagedResult<UserSummary>.Empty(size);
        }

        // A page past the end walks back to the last page that exists rather than rendering an empty
        // table — the search narrows the directory under the reader's feet.
        index = Math.Min(index, (total - 1) / size);

        // Email is unique (Identity is configured with RequireUniqueEmail), so it is already a total
        // order; the id rides along for the theoretical row that has none, because a tie under
        // Skip/Take drops rows off one page and repeats them on the next.
        var ordered = query.Sort switch
        {
            UserListSort.Name => matching
                .OrderBy(u => u.DisplayName ?? u.Email)
                .ThenBy(u => u.Email)
                .ThenBy(u => u.Id),
            UserListSort.Status => matching
                .OrderBy(u => u.Status)
                .ThenBy(u => u.Email)
                .ThenBy(u => u.Id),
            UserListSort.RecentActivity => matching
                .OrderByDescending(u => dbContext.AccountAuditEvents
                    .Where(e => e.UserId == u.Id)
                    .Select(e => (DateTimeOffset?)e.OccurredAtUtc)
                    .Max())
                .ThenBy(u => u.Email)
                .ThenBy(u => u.Id),
            _ => matching.OrderBy(u => u.Email).ThenBy(u => u.Id),
        };

        var items = await SummariseAsync(
            dbContext,
            ordered.Skip(index * size).Take(size),
            cancellationToken);

        return new PagedResult<UserSummary>(items, total, index, size);
    }

    public async Task<UserDirectorySummary> GetSummaryAsync(CancellationToken cancellationToken = default)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);

        return await dbContext.Users.AsNoTracking()
            .GroupBy(_ => 1)
            .Select(group => new UserDirectorySummary(
                group.Count(),
                group.Count(user => user.Status == AccountStatus.Active),
                group.Count(user => user.Status == AccountStatus.PendingActivation),
                group.Count(user => user.Status == AccountStatus.Blocked)))
            .SingleOrDefaultAsync(cancellationToken)
            ?? new UserDirectorySummary(0, 0, 0, 0);
    }

    private static IQueryable<ApplicationUser> Matching(D3ParkingDbContext dbContext, string? search)
    {
        var query = dbContext.Users.AsNoTracking();
        if (string.IsNullOrWhiteSpace(search))
        {
            return query;
        }

        // LIKE is case-insensitive under SQL Server's default (CI) collation. On a
        // case-sensitive database collation the search would turn case-sensitive —
        // give the columns an explicit CI collation there.
        var term = $"%{search.Trim()}%";
        return query.Where(u =>
            EF.Functions.Like(u.Email!, term) ||
            (u.DisplayName != null && EF.Functions.Like(u.DisplayName, term)));
    }

    private static IQueryable<ApplicationUser> Matching(D3ParkingDbContext dbContext, UserListQuery filter)
    {
        var query = Matching(dbContext, filter.Search);

        if (filter.Status is { } status)
        {
            query = query.Where(user => user.Status == status);
        }

        if (!string.IsNullOrWhiteSpace(filter.Role))
        {
            var role = filter.Role;
            query = query.Where(user =>
                (from userRole in dbContext.UserRoles
                 join candidateRole in dbContext.Roles on userRole.RoleId equals candidateRole.Id
                 where userRole.UserId == user.Id && candidateRole.Name == role
                 select userRole).Any());
        }

        query = filter.Source switch
        {
            UserAccountSourceFilter.Local => query.Where(user => user.ExternalProvider == null),
            UserAccountSourceFilter.Federated => query.Where(user => user.ExternalProvider != null),
            _ => query,
        };

        return query;
    }

    /// <summary>
    /// Runs an already-ordered-and-sliced account query and shapes the rows, with each account's
    /// roles resolved in the same round trip.
    /// </summary>
    /// <remarks>
    /// The projection is written out here rather than factored into a helper the two callers share:
    /// a lambda handed straight to <c>Select</c> is an expression tree EF translates, but the moment
    /// it becomes a call to a method taking an entity, EF gives up on translating it, materialises
    /// every account and runs the roles subquery client-side against a context it is mid-enumeration
    /// on. That fails outright — which is how this was caught.
    /// </remarks>
    private static async Task<UserSummary[]> SummariseAsync(
        D3ParkingDbContext dbContext,
        IQueryable<ApplicationUser> accounts,
        CancellationToken cancellationToken)
    {
        var rows = await accounts
            .Select(u => new
            {
                u.Id,
                u.Email,
                u.DisplayName,
                u.Status,
                u.EmailConfirmed,
                u.ExternalProvider,
                LastActivityUtc = dbContext.AccountAuditEvents
                    .Where(audit => audit.UserId == u.Id)
                    .Select(audit => (DateTimeOffset?)audit.OccurredAtUtc)
                    .Max(),
                Roles = (from ur in dbContext.UserRoles
                         join r in dbContext.Roles on ur.RoleId equals r.Id
                         where ur.UserId == u.Id
                         orderby r.Name
                         select r.Name!).ToList(),
            })
            .ToListAsync(cancellationToken);

        return rows
            .Select(r => new UserSummary(
                r.Id, r.Email!, r.DisplayName, r.Status, r.Roles,
                r.EmailConfirmed, r.ExternalProvider, r.LastActivityUtc))
            .ToArray();
    }

    public async Task<UserDetail?> GetAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var user = await dbContext.Users.AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == userId, cancellationToken);
        if (user is null)
        {
            return null;
        }

        var roles = await (from ur in dbContext.UserRoles
                           join r in dbContext.Roles on ur.RoleId equals r.Id
                           where ur.UserId == userId
                           orderby r.Name
                           select r.Name!).ToListAsync(cancellationToken);

        var isLastAdministrator = roles.Contains(Roles.Administrator, StringComparer.Ordinal)
            && await IsLastAdministratorAsync(dbContext, cancellationToken);

        return new UserDetail(
            user.Id, user.Email!, user.DisplayName, user.PhoneNumber, user.Status,
            user.EmailConfirmed, user.StatusChangedAtUtc, user.StatusReason, roles, isLastAdministrator,
            user.ExternalProvider, user.ExternalSyncedAtUtc);
    }

    public async Task<EmployeeDeletionImpact?> GetDeletionImpactAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        if (!await dbContext.Users.AnyAsync(u => u.Id == userId, cancellationToken))
        {
            return null;
        }

        return await EmployeeLifecycleCleanup.PreviewAsync(dbContext, userId, cancellationToken);
    }

    public async Task<AccountResult> CreateAsync(
        string email,
        string? displayName,
        string password,
        IReadOnlyList<string> roles,
        Guid adminId,
        CancellationToken cancellationToken = default)
    {
        if (await userManager.FindByEmailAsync(email) is not null)
        {
            return AccountResult.Failure(messages["Error_EmailExists"]);
        }

        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        if (await ValidateRolesAsync(dbContext, roles, cancellationToken) is { } invalid)
        {
            return invalid;
        }

        if (await GuardAssignableAsync(dbContext, roles, adminId, cancellationToken) is { } forbidden)
        {
            return forbidden;
        }

        var user = new ApplicationUser
        {
            UserName = email,
            Email = email,
            DisplayName = displayName,
            EmailConfirmed = true,
            Status = AccountStatus.Active,
            StatusChangedAtUtc = timeProvider.GetUtcNow(),
        };

        var created = await userManager.CreateAsync(user, password);
        if (!created.Succeeded)
        {
            return ToFailure(created);
        }

        if (roles.Count > 0)
        {
            var assigned = await userManager.AddToRolesAsync(user, roles);
            if (!assigned.Succeeded)
            {
                return ToFailure(assigned);
            }
        }

        await AuditAsync(dbContext, user.Id, AccountAuditEventType.Registered, adminId, $"created by admin; roles: {Join(roles)}", cancellationToken);
        logger.LogInformation("Admin {AdminId} created account {UserId}", adminId, user.Id);
        return AccountResult.Success;
    }

    public async Task<AccountResult> SetRolesAsync(
        Guid userId,
        IReadOnlyList<string> roles,
        Guid adminId,
        CancellationToken cancellationToken = default)
    {
        var user = await userManager.FindByIdAsync(userId.ToString());
        if (user is null)
        {
            return AccountResult.Failure(messages["Error_AccountNotFound"]);
        }

        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        if (await ValidateRolesAsync(dbContext, roles, cancellationToken) is { } invalid)
        {
            return invalid;
        }

        var current = await userManager.GetRolesAsync(user);
        var target = roles.ToHashSet(StringComparer.Ordinal);
        var toAdd = target.Except(current, StringComparer.Ordinal).ToArray();
        var toRemove = current.Except(target, StringComparer.Ordinal).ToArray();

        if (toAdd.Length == 0 && toRemove.Length == 0)
        {
            return AccountResult.Success;
        }

        if (await GuardAssignableAsync(dbContext, toAdd, adminId, cancellationToken) is { } forbidden)
        {
            return forbidden;
        }

        // One serializable transaction on the scoped context covers the guard and both role
        // writes. Without it, (a) two admins stripping the two last administrators could both
        // pass the count and leave zero, and (b) a failure between remove and add would strand
        // the user with roles removed but none added.
        await using var transaction = await identityDbContext.Database
            .BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);

        // Guard against locking everyone out of administration.
        if (toRemove.Contains(Roles.Administrator, StringComparer.Ordinal) && await IsLastAdministratorAsync(identityDbContext, cancellationToken))
        {
            return AccountResult.Failure(messages["Error_LastAdministrator"]);
        }

        if (toRemove.Length > 0)
        {
            var removed = await userManager.RemoveFromRolesAsync(user, toRemove);
            if (!removed.Succeeded)
            {
                return ToFailure(removed);
            }
        }

        if (toAdd.Length > 0)
        {
            var added = await userManager.AddToRolesAsync(user, toAdd);
            if (!added.Succeeded)
            {
                return ToFailure(added);
            }
        }

        // Force the user's signed-in claims to be re-issued so role changes take effect promptly.
        await userManager.UpdateSecurityStampAsync(user);
        await transaction.CommitAsync(cancellationToken);

        await AuditAsync(dbContext, user.Id, AccountAuditEventType.RolesChanged, adminId, Join(roles), cancellationToken);
        logger.LogInformation("Admin {AdminId} set roles of {UserId} to [{Roles}]", adminId, user.Id, Join(roles));
        return AccountResult.Success;
    }

    public async Task<AccountResult> DeleteAsync(Guid userId, Guid adminId, CancellationToken cancellationToken = default)
    {
        if (userId == adminId)
        {
            return AccountResult.Failure(messages["Error_CannotDeleteSelf"]);
        }

        var user = await userManager.FindByIdAsync(userId.ToString());
        if (user is null)
        {
            return AccountResult.Failure(messages["Error_AccountNotFound"]);
        }

        // Guard and delete in one serializable transaction (see SetRolesAsync for the rationale):
        // concurrent deletes of the two last administrators must not both slip past the count.
        await using var transaction = await identityDbContext.Database
            .BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);

        if (await userManager.IsInRoleAsync(user, Roles.Administrator)
            && await IsLastAdministratorAsync(identityDbContext, cancellationToken))
        {
            return AccountResult.Failure(messages["Error_LastAdministrator"]);
        }

        // The cascade and the Identity row are one unit. Previously the account was committed first
        // and parking cleanup happened through another context; a transient failure left ghost
        // reservations and ownership that no retry could reach from the deleted account screen.
        await EmployeeLifecycleCleanup.CleanOperationalAsync(
            identityDbContext, userId, user.Email, adminId, timeProvider.GetUtcNow(),
            revokeAccess: false, cancellationToken);

        // Keep the event sequence and timestamps, but remove free-form values that may contain an
        // old e-mail address, phone number or administrator note. The unresolved user id is the
        // anonymous correlation key used by reports after the Identity row is gone.
        await identityDbContext.AccountAuditEvents
            .Where(a => a.UserId == userId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(a => a.Actor, a => a.Actor == "self" ? "deleted" : a.Actor)
                .SetProperty(a => a.Detail, (string?)null), cancellationToken);

        var deleted = await userManager.DeleteAsync(user);
        if (!deleted.Succeeded)
        {
            return ToFailure(deleted);
        }

        identityDbContext.AccountAuditEvents.Add(new AccountAuditEvent(
            userId, AccountAuditEventType.Deleted, $"admin:{adminId}",
            "Identity removed; operational data deleted and retained history anonymized.",
            timeProvider.GetUtcNow()));
        await identityDbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        logger.LogInformation("Admin {AdminId} deleted account {UserId}", adminId, userId);
        return AccountResult.Success;
    }

    private async Task<AccountResult?> ValidateRolesAsync(D3ParkingDbContext dbContext, IReadOnlyList<string> roles, CancellationToken cancellationToken)
    {
        foreach (var role in roles)
        {
            if (!await dbContext.Roles.AnyAsync(r => r.Name == role, cancellationToken))
            {
                return AccountResult.Failure(messages["Error_UnknownRole", role]);
            }
        }

        return null;
    }

    /// <summary>
    /// Refuses to hand someone a role that grants more than the actor holds themselves. This is
    /// what keeps <see cref="Permissions.Users.AssignRoles"/> from being a synonym for
    /// Administrator: a user manager can hand out Employee, because everything Employee grants
    /// they already have, and cannot hand out Administrator or Lot manager, because those reach
    /// past them. Administrators hold the whole catalog, so nothing constrains them.
    /// </summary>
    private async Task<AccountResult?> GuardAssignableAsync(
        D3ParkingDbContext dbContext,
        IReadOnlyCollection<string> rolesToAdd,
        Guid adminId,
        CancellationToken cancellationToken)
    {
        if (rolesToAdd.Count == 0)
        {
            return null;
        }

        var held = await EffectivePermissions.ForUserAsync(dbContext, adminId, cancellationToken);
        var grants = await EffectivePermissions.ForRolesAsync(dbContext, rolesToAdd, cancellationToken);

        foreach (var role in rolesToAdd)
        {
            if (!grants.TryGetValue(role, out var granted) || granted.IsSubsetOf(held))
            {
                continue;
            }

            logger.LogWarning("Admin {AdminId} attempted to assign role {Role}, which grants permissions they do not hold",
                adminId, role);
            return AccountResult.Failure(messages["Error_RoleNotAssignable", role]);
        }

        return null;
    }

    // The candidate is already known to hold the role at both call sites, so only the count is
    // needed — and it has to run on the caller's context to stay inside its serializable range locks.
    private static Task<bool> IsLastAdministratorAsync(D3ParkingDbContext dbContext, CancellationToken cancellationToken) =>
        AdministratorCover.IsLastAdministratorCountAsync(dbContext, cancellationToken);

    private async Task AuditAsync(D3ParkingDbContext dbContext, Guid userId, AccountAuditEventType type, Guid adminId, string? detail, CancellationToken cancellationToken)
    {
        dbContext.AccountAuditEvents.Add(new AccountAuditEvent(userId, type, $"admin:{adminId}", detail, timeProvider.GetUtcNow()));
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private static string Join(IReadOnlyList<string> values) => values.Count == 0 ? "—" : string.Join(", ", values);

    private static AccountResult ToFailure(IdentityResult result) =>
        AccountResult.Failure(result.Errors.Select(e => e.Description).ToArray());
}
