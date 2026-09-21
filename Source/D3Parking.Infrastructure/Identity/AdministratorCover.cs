using Microsoft.EntityFrameworkCore;
using D3Parking.Domain.Accounts;
using D3Parking.Domain.Authorization;
using D3Parking.Infrastructure.Persistence;

namespace D3Parking.Infrastructure.Identity;

/// <summary>
/// Answers the one question every path that could remove administration has to ask first.
/// </summary>
/// <remarks>
/// Only <see cref="AccountStatus.Active"/> members count. A blocked or deactivated administrator is
/// a row in a table, not somebody who can administer anything — counting them would let the last
/// working administrator be stripped, blocked or deleted and leave the installation with no way
/// back in. The rule lives here rather than in each caller because it is now enforced from four
/// directions (role edits, deletion, status changes, and directory sync) and must not drift.
/// </remarks>
internal static class AdministratorCover
{
    /// <summary>True when this account is the only administrator who could still sign in.</summary>
    public static async Task<bool> IsLastAdministratorAsync(
        D3ParkingDbContext dbContext,
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        var isAdministrator = await (from ur in dbContext.UserRoles
                                     join r in dbContext.Roles on ur.RoleId equals r.Id
                                     where ur.UserId == userId && r.Name == Roles.Administrator
                                     select ur.UserId).AnyAsync(cancellationToken);

        return isAdministrator && await IsLastAdministratorCountAsync(dbContext, cancellationToken);
    }

    /// <summary>
    /// The count half of the rule, for callers that have already established the candidate holds
    /// the role (and whose query must stay inside their own transaction's range locks).
    /// </summary>
    public static async Task<bool> IsLastAdministratorCountAsync(
        D3ParkingDbContext dbContext,
        CancellationToken cancellationToken = default)
    {
        var activeAdministrators = await (from ur in dbContext.UserRoles
                                          join r in dbContext.Roles on ur.RoleId equals r.Id
                                          join u in dbContext.Users on ur.UserId equals u.Id
                                          where r.Name == Roles.Administrator && u.Status == AccountStatus.Active
                                          select ur.UserId).CountAsync(cancellationToken);

        return activeAdministrators <= 1;
    }
}
