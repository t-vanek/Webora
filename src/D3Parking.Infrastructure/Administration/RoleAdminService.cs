using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using D3Parking.Application;
using D3Parking.Application.Accounts;
using D3Parking.Application.Administration;
using D3Parking.Domain.Accounts;
using D3Parking.Domain.Authorization;
using D3Parking.Infrastructure.Identity;
using D3Parking.Infrastructure.Persistence;

namespace D3Parking.Infrastructure.Administration;

public sealed class RoleAdminService(
    RoleManager<ApplicationRole> roleManager,
    // The scoped context the materializer and role manager both write through, so a recomposition
    // and the claims it derives land together.
    D3ParkingDbContext identityDbContext,
    IDbContextFactory<D3ParkingDbContext> dbContextFactory,
    RolePermissionMaterializer materializer,
    IStringLocalizer<AccountMessages> messages,
    TimeProvider timeProvider,
    ILogger<RoleAdminService> logger) : IRoleAdminService
{
    public async Task<IReadOnlyList<RoleSummary>> ListAsync(CancellationToken cancellationToken = default)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);

        var rows = await dbContext.Roles.AsNoTracking()
            .OrderBy(r => r.Name)
            .Select(r => new
            {
                r.Id,
                r.Name,
                Permissions = dbContext.RoleClaims
                    .Where(c => c.RoleId == r.Id && c.ClaimType == D3ParkingClaimTypes.Permission && c.ClaimValue != null)
                    .Select(c => c.ClaimValue!)
                    .ToList(),
                UserCount = dbContext.UserRoles.Count(ur => ur.RoleId == r.Id),
            })
            .ToListAsync(cancellationToken);

        var groups = await LoadGroupsByRoleAsync(dbContext, cancellationToken);

        return rows
            .Select(r => new RoleSummary(
                r.Id,
                r.Name!,
                groups.TryGetValue(r.Id, out var held) ? held : [],
                Permissions.All.Where(r.Permissions.Contains).ToArray(),
                r.UserCount,
                Roles.IsDefault(r.Name!)))
            .ToArray();
    }

    public async Task<RoleDetail?> GetAsync(Guid roleId, CancellationToken cancellationToken = default)
    {
        var role = await roleManager.FindByIdAsync(roleId.ToString());
        if (role is null)
        {
            return null;
        }

        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);

        var permissions = (await roleManager.GetClaimsAsync(role))
            .Where(c => c.Type == D3ParkingClaimTypes.Permission)
            .Select(c => c.Value)
            .ToArray();

        var groups = await LoadGroupsByRoleAsync(dbContext, cancellationToken);
        var memberCount = await dbContext.UserRoles.CountAsync(ur => ur.RoleId == roleId, cancellationToken);

        return new RoleDetail(
            role.Id,
            role.Name!,
            Roles.IsDefault(role.Name!),
            groups.TryGetValue(roleId, out var held) ? held : [],
            Permissions.All.Where(permissions.Contains).ToArray(),
            memberCount);
    }

    public async Task<PagedResult<RoleMember>> ListMembersPageAsync(
        Guid roleId,
        string? search,
        int pageIndex,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        // Clamped here rather than trusted from the caller, the same way the account directory does
        // it: the page size decides how much one request can ask the database to materialise.
        var size = Math.Clamp(pageSize, 1, 100);
        var index = Math.Max(0, pageIndex);

        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var matching = MatchingMembers(dbContext, roleId, search);

        var total = await matching.CountAsync(cancellationToken);
        if (total == 0)
        {
            return PagedResult<RoleMember>.Empty(size);
        }

        // A search narrows the list under the reader's feet, so a page past the end walks back to
        // the last one that exists instead of rendering an empty table.
        index = Math.Min(index, (total - 1) / size);

        // Email is unique (Identity is configured with RequireUniqueEmail) and therefore already a
        // total order; the id rides along for the theoretical account without one, because a tie
        // under Skip/Take drops rows off one page and repeats them on the next.
        var items = await matching
            .OrderBy(u => u.Email)
            .ThenBy(u => u.Id)
            .Skip(index * size)
            .Take(size)
            .Select(u => new RoleMember(u.Id, u.Email!, u.DisplayName))
            .ToListAsync(cancellationToken);

        return new PagedResult<RoleMember>(items, total, index, size);
    }

    /// <summary>
    /// The accounts holding the role, narrowed by the search term. Matches email and display name —
    /// the two columns the members table shows.
    /// </summary>
    private static IQueryable<ApplicationUser> MatchingMembers(
        D3ParkingDbContext dbContext,
        Guid roleId,
        string? search)
    {
        var members = dbContext.Users.AsNoTracking()
            .Where(u => dbContext.UserRoles.Any(ur => ur.RoleId == roleId && ur.UserId == u.Id));

        if (string.IsNullOrWhiteSpace(search))
        {
            return members;
        }

        // LIKE is case-insensitive under SQL Server's default (CI) collation; on a case-sensitive
        // database collation the search would turn case-sensitive, same caveat as the directory.
        var term = $"%{search.Trim()}%";
        return members.Where(u =>
            EF.Functions.Like(u.Email!, term) ||
            (u.DisplayName != null && EF.Functions.Like(u.DisplayName, term)));
    }

    public async Task<AccountResult> CreateAsync(string name, IReadOnlyList<Guid> groupIds, Guid actorId, CancellationToken cancellationToken = default)
    {
        name = name?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(name))
        {
            return AccountResult.Failure(messages["Error_RoleNameRequired"]);
        }

        if (Roles.IsDefault(name))
        {
            return AccountResult.Failure(messages["Error_DefaultRoleProtected"]);
        }

        if (await GuardComposeAsync(groupIds, actorId, cancellationToken) is { } forbidden)
        {
            return forbidden;
        }

        var role = new ApplicationRole(name);
        var created = await roleManager.CreateAsync(role);
        if (!created.Succeeded)
        {
            return ToFailure(created);
        }

        foreach (var groupId in groupIds.Distinct())
        {
            identityDbContext.RolePermissionGroups.Add(new RolePermissionGroup(role.Id, groupId));
        }

        await identityDbContext.SaveChangesAsync(cancellationToken);
        await materializer.MaterializeAsync(role.Id, cancellationToken);

        await AuditAsync(actorId, $"created role {name} from groups: {await DescribeAsync(groupIds, cancellationToken)}", cancellationToken);
        logger.LogInformation("Actor {ActorId} created role {Role} from {Count} groups", actorId, name, groupIds.Count);
        return AccountResult.Success;
    }

    public async Task<AccountResult> RenameAsync(Guid roleId, string name, CancellationToken cancellationToken = default)
    {
        var role = await roleManager.FindByIdAsync(roleId.ToString());
        if (role is null)
        {
            return AccountResult.Failure(messages["Error_RoleNotFound"]);
        }

        if (Roles.IsDefault(role.Name!))
        {
            return AccountResult.Failure(messages["Error_DefaultRoleProtected"]);
        }

        name = name?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(name))
        {
            return AccountResult.Failure(messages["Error_RoleNameRequired"]);
        }

        // Taking a built-in name would make a custom role look system-managed and, once the seeder
        // ran, hand it that role's whole composition.
        if (Roles.IsDefault(name))
        {
            return AccountResult.Failure(messages["Error_DefaultRoleProtected"]);
        }

        var renamed = await roleManager.SetRoleNameAsync(role, name);
        if (!renamed.Succeeded)
        {
            return ToFailure(renamed);
        }

        var updated = await roleManager.UpdateAsync(role);
        return updated.Succeeded ? AccountResult.Success : ToFailure(updated);
    }

    public async Task<AccountResult> SetGroupsAsync(Guid roleId, IReadOnlyList<Guid> groupIds, Guid actorId, CancellationToken cancellationToken = default)
    {
        var role = await roleManager.FindByIdAsync(roleId.ToString());
        if (role is null)
        {
            return AccountResult.Failure(messages["Error_RoleNotFound"]);
        }

        // Built-in roles are re-asserted from the seeder's map on every start, so an edit here
        // would silently revert. Duplicating one produces a custom role that does stick.
        if (Roles.IsDefault(role.Name!))
        {
            return AccountResult.Failure(messages["Error_BuiltInRolePermissions"]);
        }

        var target = groupIds.ToHashSet();
        var current = await identityDbContext.RolePermissionGroups
            .Where(l => l.RoleId == roleId)
            .ToListAsync(cancellationToken);
        var held = current.Select(l => l.PermissionGroupId).ToHashSet();

        var added = target.Except(held).ToArray();
        var removed = held.Except(target).ToArray();
        if (added.Length == 0 && removed.Length == 0)
        {
            return AccountResult.Success;
        }

        // Only additions are bounded by the actor's own permissions — taking a group away lowers
        // privilege and stays open to anyone who may recompose the role at all.
        if (await GuardComposeAsync(added, actorId, cancellationToken) is { } forbidden)
        {
            return forbidden;
        }

        identityDbContext.RolePermissionGroups.RemoveRange(current.Where(l => !target.Contains(l.PermissionGroupId)));
        foreach (var groupId in added)
        {
            identityDbContext.RolePermissionGroups.Add(new RolePermissionGroup(roleId, groupId));
        }

        await identityDbContext.SaveChangesAsync(cancellationToken);

        if (await materializer.MaterializeAsync(roleId, cancellationToken))
        {
            await materializer.RefreshMembersAsync(roleId, cancellationToken);
        }

        await AuditAsync(actorId,
            $"role {role.Name}: +[{await DescribeAsync(added, cancellationToken)}] -[{await DescribeAsync(removed, cancellationToken)}]",
            cancellationToken);
        logger.LogInformation("Actor {ActorId} recomposed role {Role}: +{Added} -{Removed} groups",
            actorId, role.Name, added.Length, removed.Length);
        return AccountResult.Success;
    }

    public async Task<AccountResult> DeleteAsync(Guid roleId, Guid actorId, CancellationToken cancellationToken = default)
    {
        var role = await roleManager.FindByIdAsync(roleId.ToString());
        if (role is null)
        {
            return AccountResult.Failure(messages["Error_RoleNotFound"]);
        }

        if (Roles.IsDefault(role.Name!))
        {
            return AccountResult.Failure(messages["Error_DefaultRoleProtected"]);
        }

        if (await identityDbContext.UserRoles.AnyAsync(ur => ur.RoleId == roleId, cancellationToken))
        {
            return AccountResult.Failure(messages["Error_RoleHasMembers"]);
        }

        var name = role.Name!;
        var deleted = await roleManager.DeleteAsync(role);
        if (!deleted.Succeeded)
        {
            return ToFailure(deleted);
        }

        await AuditAsync(actorId, $"deleted role {name}", cancellationToken);
        logger.LogInformation("Actor {ActorId} deleted role {Role}", actorId, name);
        return AccountResult.Success;
    }

    private static async Task<Dictionary<Guid, IReadOnlyList<RoleGroupRef>>> LoadGroupsByRoleAsync(
        D3ParkingDbContext dbContext,
        CancellationToken cancellationToken)
    {
        var rows = await (from link in dbContext.RolePermissionGroups.AsNoTracking()
                          join grp in dbContext.PermissionGroups.AsNoTracking() on link.PermissionGroupId equals grp.Id
                          orderby grp.Name
                          select new
                          {
                              link.RoleId,
                              grp.Id,
                              grp.Name,
                              grp.IsBuiltIn,
                              Permissions = grp.Entries.Select(e => e.Permission).ToList(),
                          })
            .ToListAsync(cancellationToken);

        return rows
            .GroupBy(r => r.RoleId)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<RoleGroupRef>)g
                    .Select(r => new RoleGroupRef(r.Id, r.Name, r.IsBuiltIn, Permissions.All.Where(r.Permissions.Contains).ToArray()))
                    .ToArray());
    }

    /// <summary>
    /// Refuses to compose a role out of a group that reaches past the actor. Without this, holding
    /// <c>Roles.Edit</c> would be indistinguishable from holding every permission any group carries:
    /// add the group to a role you are already in, and the member refresh issues it to you.
    /// </summary>
    private async Task<AccountResult?> GuardComposeAsync(
        IReadOnlyCollection<Guid> groupIds,
        Guid actorId,
        CancellationToken cancellationToken)
    {
        if (groupIds.Count == 0)
        {
            return null;
        }

        var groups = await identityDbContext.PermissionGroups
            .Where(g => groupIds.Contains(g.Id))
            .Select(g => new { g.Id, g.Name, Permissions = g.Entries.Select(e => e.Permission).ToList() })
            .ToListAsync(cancellationToken);

        var missing = groupIds.Where(id => groups.All(g => g.Id != id)).ToArray();
        if (missing.Length > 0)
        {
            return AccountResult.Failure(messages["Error_PermissionGroupNotFound"]);
        }

        var held = await EffectivePermissions.ForUserAsync(identityDbContext, actorId, cancellationToken);
        foreach (var group in groups)
        {
            var overreach = group.Permissions.Where(p => !held.Contains(p)).ToArray();
            if (overreach.Length == 0)
            {
                continue;
            }

            logger.LogWarning("Actor {ActorId} attempted to compose a role from group {Group}, which grants permissions they do not hold: {Permissions}",
                actorId, group.Name, string.Join(", ", overreach));
            return AccountResult.Failure(messages["Error_GroupNotComposable", group.Name]);
        }

        return null;
    }

    private async Task<string> DescribeAsync(IReadOnlyCollection<Guid> groupIds, CancellationToken cancellationToken)
    {
        if (groupIds.Count == 0)
        {
            return "—";
        }

        var names = await identityDbContext.PermissionGroups
            .Where(g => groupIds.Contains(g.Id))
            .OrderBy(g => g.Name)
            .Select(g => g.Name)
            .ToListAsync(cancellationToken);

        return string.Join(", ", names);
    }

    private async Task AuditAsync(Guid actorId, string detail, CancellationToken cancellationToken)
    {
        // Recorded against the actor: a role is not an account, and the question this trail has to
        // answer is "who changed the authorization model", not "what happened to this user".
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        dbContext.AccountAuditEvents.Add(new AccountAuditEvent(
            actorId,
            AccountAuditEventType.RolePermissionsChanged,
            $"admin:{actorId}",
            AuditDetail.Truncate(detail),
            timeProvider.GetUtcNow()));
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private static AccountResult ToFailure(IdentityResult result) =>
        AccountResult.Failure(result.Errors.Select(e => e.Description).ToArray());
}
