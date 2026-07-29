using System.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
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
        var query = dbContext.Users.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(search))
        {
            // LIKE is case-insensitive under SQL Server's default (CI) collation. On a
            // case-sensitive database collation the search would turn case-sensitive —
            // give the columns an explicit CI collation there.
            var term = $"%{search.Trim()}%";
            query = query.Where(u =>
                EF.Functions.Like(u.Email!, term) ||
                (u.DisplayName != null && EF.Functions.Like(u.DisplayName, term)));
        }

        var rows = await query
            .OrderBy(u => u.Email)
            .Take(ListLimit)
            .Select(u => new
            {
                u.Id,
                u.Email,
                u.DisplayName,
                u.Status,
                Roles = (from ur in dbContext.UserRoles
                         join r in dbContext.Roles on ur.RoleId equals r.Id
                         where ur.UserId == u.Id
                         orderby r.Name
                         select r.Name!).ToList(),
            })
            .ToListAsync(cancellationToken);

        return rows
            .Select(r => new UserSummary(r.Id, r.Email!, r.DisplayName, r.Status, r.Roles))
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

        return new UserDetail(
            user.Id, user.Email!, user.DisplayName, user.PhoneNumber, user.Status,
            user.EmailConfirmed, user.StatusChangedAtUtc, user.StatusReason, roles);
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

        var deleted = await userManager.DeleteAsync(user);
        if (!deleted.Succeeded)
        {
            return ToFailure(deleted);
        }

        await transaction.CommitAsync(cancellationToken);

        await CleanUpParkingFootprintAsync(userId, cancellationToken);

        logger.LogInformation("Admin {AdminId} deleted account {UserId}", adminId, userId);
        return AccountResult.Success;
    }

    /// <summary>
    /// Severs a deleted account's footprint in the parking domain (nothing references Users by
    /// FK): owned spots return to the shared pool, active bookings and waitlist entries stop
    /// blocking spots for a ghost, and notification/push rows addressed to the nonexistent user
    /// are dropped. Historical rows (completed reservations, the points ledger) are kept.
    /// </summary>
    private async Task CleanUpParkingFootprintAsync(Guid userId, CancellationToken cancellationToken)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);

        // Owned spots go back to the pool. Their upcoming releases are void with the owner gone —
        // for an ownerless spot they no longer gate booking, but the reconcile sweep would keep
        // processing them and write clawbacks/notifications for the deleted account. UTC "today"
        // is close enough here; at worst one boundary day is treated as history.
        var ownedSpotIds = await dbContext.ParkingSpots
            .Where(s => s.OwnerId == userId)
            .Select(s => s.Id)
            .ToListAsync(cancellationToken);
        if (ownedSpotIds.Count > 0)
        {
            var todayUtc = DateOnly.FromDateTime(timeProvider.GetUtcNow().UtcDateTime.Date);
            await dbContext.ParkingSpots
                .Where(s => ownedSpotIds.Contains(s.Id))
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.OwnerId, (Guid?)null), cancellationToken);
            await dbContext.SpotReleases
                .Where(r => ownedSpotIds.Contains(r.SpotId) && r.Date >= todayUtc)
                .ExecuteDeleteAsync(cancellationToken);
        }

        // Active bookings would otherwise hold spots until the no-show sweep penalized a ghost.
        await dbContext.Reservations
            .Where(r => r.UserId == userId
                && (r.Status == ReservationStatus.Reserved || r.Status == ReservationStatus.CheckedIn))
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, ReservationStatus.Cancelled), cancellationToken);

        await dbContext.QueueEntries
            .Where(q => q.UserId == userId
                && (q.Status == QueueEntryStatus.Waiting || q.Status == QueueEntryStatus.Offered))
            .ExecuteUpdateAsync(s => s.SetProperty(q => q.Status, QueueEntryStatus.Cancelled), cancellationToken);

        await dbContext.PushSubscriptions.Where(p => p.UserId == userId).ExecuteDeleteAsync(cancellationToken);
        await dbContext.Notifications.Where(n => n.UserId == userId).ExecuteDeleteAsync(cancellationToken);
        await dbContext.NotificationPreferences.Where(p => p.UserId == userId).ExecuteDeleteAsync(cancellationToken);
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

    private static async Task<bool> IsLastAdministratorAsync(D3ParkingDbContext dbContext, CancellationToken cancellationToken)
    {
        var adminCount = await (from ur in dbContext.UserRoles
                                join r in dbContext.Roles on ur.RoleId equals r.Id
                                where r.Name == Roles.Administrator
                                select ur.UserId).CountAsync(cancellationToken);

        // The candidate is currently an administrator; they're the last one if no one else is.
        return adminCount <= 1;
    }

    private async Task AuditAsync(D3ParkingDbContext dbContext, Guid userId, AccountAuditEventType type, Guid adminId, string? detail, CancellationToken cancellationToken)
    {
        dbContext.AccountAuditEvents.Add(new AccountAuditEvent(userId, type, $"admin:{adminId}", detail, timeProvider.GetUtcNow()));
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private static string Join(IReadOnlyList<string> values) => values.Count == 0 ? "—" : string.Join(", ", values);

    private static AccountResult ToFailure(IdentityResult result) =>
        AccountResult.Failure(result.Errors.Select(e => e.Description).ToArray());
}
