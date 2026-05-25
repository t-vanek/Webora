using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using Webora.Application.Accounts;
using Webora.Application.Administration;
using Webora.Domain.Accounts;
using Webora.Domain.Authorization;
using Webora.Infrastructure.Identity;
using Webora.Infrastructure.Persistence;

namespace Webora.Infrastructure.Administration;

public sealed class UserAdminService(
    UserManager<ApplicationUser> userManager,
    WeboraDbContext dbContext,
    IStringLocalizer<AccountMessages> messages,
    TimeProvider timeProvider,
    ILogger<UserAdminService> logger) : IUserAdminService
{
    private const int ListLimit = 500;

    public async Task<IReadOnlyList<UserSummary>> ListAsync(string? search, CancellationToken cancellationToken = default)
    {
        var query = dbContext.Users.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = $"%{search.Trim()}%";
            query = query.Where(u =>
                EF.Functions.ILike(u.Email!, term) ||
                (u.DisplayName != null && EF.Functions.ILike(u.DisplayName, term)));
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

        if (await ValidateRolesAsync(roles) is { } invalid)
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

        await AuditAsync(user.Id, AccountAuditEventType.Registered, adminId, $"created by admin; roles: {Join(roles)}", cancellationToken);
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

        if (await ValidateRolesAsync(roles) is { } invalid)
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

        // Guard against locking everyone out of administration.
        if (toRemove.Contains(Roles.Administrator, StringComparer.Ordinal) && await IsLastAdministratorAsync(user.Id))
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

        await AuditAsync(user.Id, AccountAuditEventType.RolesChanged, adminId, Join(roles), cancellationToken);
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

        if (await userManager.IsInRoleAsync(user, Roles.Administrator) && await IsLastAdministratorAsync(user.Id))
        {
            return AccountResult.Failure(messages["Error_LastAdministrator"]);
        }

        var deleted = await userManager.DeleteAsync(user);
        if (!deleted.Succeeded)
        {
            return ToFailure(deleted);
        }

        logger.LogInformation("Admin {AdminId} deleted account {UserId}", adminId, userId);
        return AccountResult.Success;
    }

    private async Task<AccountResult?> ValidateRolesAsync(IReadOnlyList<string> roles)
    {
        foreach (var role in roles)
        {
            if (!await dbContext.Roles.AnyAsync(r => r.Name == role))
            {
                return AccountResult.Failure(messages["Error_UnknownRole", role]);
            }
        }

        return null;
    }

    private async Task<bool> IsLastAdministratorAsync(Guid candidateUserId)
    {
        var adminCount = await (from ur in dbContext.UserRoles
                                join r in dbContext.Roles on ur.RoleId equals r.Id
                                where r.Name == Roles.Administrator
                                select ur.UserId).CountAsync();

        // The candidate is currently an administrator; they're the last one if no one else is.
        return adminCount <= 1;
    }

    private async Task AuditAsync(Guid userId, AccountAuditEventType type, Guid adminId, string? detail, CancellationToken cancellationToken)
    {
        dbContext.AccountAuditEvents.Add(new AccountAuditEvent(userId, type, $"admin:{adminId}", detail, timeProvider.GetUtcNow()));
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private static string Join(IReadOnlyList<string> values) => values.Count == 0 ? "—" : string.Join(", ", values);

    private static AccountResult ToFailure(IdentityResult result) =>
        AccountResult.Failure(result.Errors.Select(e => e.Description).ToArray());
}
