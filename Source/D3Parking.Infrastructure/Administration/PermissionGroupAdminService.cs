using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using D3Parking.Application.Accounts;
using D3Parking.Application.Administration;
using D3Parking.Domain.Accounts;
using D3Parking.Domain.Authorization;
using D3Parking.Infrastructure.Identity;
using D3Parking.Infrastructure.Persistence;

namespace D3Parking.Infrastructure.Administration;

public sealed class PermissionGroupAdminService(
    // The scoped context, so a group edit and the role claims it re-derives commit together.
    D3ParkingDbContext identityDbContext,
    IDbContextFactory<D3ParkingDbContext> dbContextFactory,
    RolePermissionMaterializer materializer,
    IStringLocalizer<AccountMessages> messages,
    TimeProvider timeProvider,
    ILogger<PermissionGroupAdminService> logger) : IPermissionGroupAdminService
{
    public IReadOnlyList<PermissionArea> PermissionCatalog { get; } = Permissions.Areas;

    public async Task<IReadOnlyList<PermissionGroupSummary>> ListAsync(CancellationToken cancellationToken = default)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);

        var rows = await dbContext.PermissionGroups.AsNoTracking()
            .OrderBy(g => g.Name)
            .Select(g => new
            {
                g.Id,
                g.Name,
                g.Description,
                g.IsBuiltIn,
                Permissions = g.Entries.Select(e => e.Permission).ToList(),
                RoleCount = dbContext.RolePermissionGroups.Count(l => l.PermissionGroupId == g.Id),
            })
            .ToListAsync(cancellationToken);

        return rows
            .Select(g => new PermissionGroupSummary(
                g.Id, g.Name, g.Description, g.IsBuiltIn,
                Permissions.All.Where(g.Permissions.Contains).ToArray(),
                g.RoleCount))
            .ToArray();
    }

    public async Task<PermissionGroupDetail?> GetAsync(Guid groupId, CancellationToken cancellationToken = default)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);

        var group = await dbContext.PermissionGroups.AsNoTracking()
            .Include(g => g.Entries)
            .FirstOrDefaultAsync(g => g.Id == groupId, cancellationToken);
        if (group is null)
        {
            return null;
        }

        // Which roles carry the group — and how many people are in each — is the blast radius of
        // any edit made here, so it belongs on the same screen as the checkboxes.
        var roles = await (from link in dbContext.RolePermissionGroups.AsNoTracking()
                           join role in dbContext.Roles on link.RoleId equals role.Id
                           where link.PermissionGroupId == groupId
                           orderby role.Name
                           select new GroupUsage(
                               role.Id,
                               role.Name!,
                               dbContext.UserRoles.Count(ur => ur.RoleId == role.Id)))
            .ToListAsync(cancellationToken);

        return new PermissionGroupDetail(
            group.Id, group.Name, group.Description, group.IsBuiltIn, group.Permissions, roles);
    }

    public async Task<AccountResult> CreateAsync(
        string name,
        string? description,
        IReadOnlyList<string> permissions,
        Guid actorId,
        CancellationToken cancellationToken = default)
    {
        name = name?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(name))
        {
            return AccountResult.Failure(messages["Error_PermissionGroupNameRequired"]);
        }

        if (await identityDbContext.PermissionGroups.AnyAsync(g => g.Name == name, cancellationToken))
        {
            return AccountResult.Failure(messages["Error_PermissionGroupNameTaken", name]);
        }

        if (Validate(permissions) is { } invalid)
        {
            return invalid;
        }

        // Closed over the catalog's dependencies here rather than trusted from the caller, so a
        // hand-crafted request cannot store Settings.Edit without the Settings.View it needs.
        var target = Permissions.Expand(permissions);
        if (await GuardGrantAsync(target, actorId, cancellationToken) is { } forbidden)
        {
            return forbidden;
        }

        identityDbContext.PermissionGroups.Add(new PermissionGroup(name, description, target));
        await identityDbContext.SaveChangesAsync(cancellationToken);

        await AuditAsync(actorId, $"created permission group {name}: {Join(target)}", cancellationToken);
        logger.LogInformation("Actor {ActorId} created permission group {Group} with {Count} permissions",
            actorId, name, target.Count);
        return AccountResult.Success;
    }

    public async Task<AccountResult> UpdateAsync(
        Guid groupId,
        string name,
        string? description,
        IReadOnlyList<string> permissions,
        Guid actorId,
        CancellationToken cancellationToken = default)
    {
        var group = await identityDbContext.PermissionGroups
            .Include(g => g.Entries)
            .FirstOrDefaultAsync(g => g.Id == groupId, cancellationToken);
        if (group is null)
        {
            return AccountResult.Failure(messages["Error_PermissionGroupNotFound"]);
        }

        if (group.IsBuiltIn)
        {
            return AccountResult.Failure(messages["Error_BuiltInGroupProtected"]);
        }

        name = name?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(name))
        {
            return AccountResult.Failure(messages["Error_PermissionGroupNameRequired"]);
        }

        if (await identityDbContext.PermissionGroups.AnyAsync(g => g.Name == name && g.Id != groupId, cancellationToken))
        {
            return AccountResult.Failure(messages["Error_PermissionGroupNameTaken", name]);
        }

        if (Validate(permissions) is { } invalid)
        {
            return invalid;
        }

        var target = Permissions.Expand(permissions);
        var before = group.Permissions;
        var granted = target.Except(before, StringComparer.Ordinal).ToArray();
        var revoked = before.Except(target, StringComparer.Ordinal).ToArray();

        if (await GuardGrantAsync(granted, actorId, cancellationToken) is { } forbidden)
        {
            return forbidden;
        }

        group.Rename(name, description);
        group.SetPermissions(target);
        await identityDbContext.SaveChangesAsync(cancellationToken);

        // Editing a group is the one action whose effect is not the thing on screen: every role
        // composed of it changes, and so does what its members may do right now.
        await materializer.MaterializeGroupUsageAsync(groupId, cancellationToken);

        if (granted.Length > 0 || revoked.Length > 0)
        {
            await AuditAsync(actorId, $"group {name}: {Describe(granted, revoked)}", cancellationToken);
        }

        logger.LogInformation("Actor {ActorId} updated permission group {Group}: +{Granted} -{Revoked}",
            actorId, name, granted.Length, revoked.Length);
        return AccountResult.Success;
    }

    public async Task<AccountResult> DeleteAsync(Guid groupId, Guid actorId, CancellationToken cancellationToken = default)
    {
        var group = await identityDbContext.PermissionGroups
            .FirstOrDefaultAsync(g => g.Id == groupId, cancellationToken);
        if (group is null)
        {
            return AccountResult.Failure(messages["Error_PermissionGroupNotFound"]);
        }

        if (group.IsBuiltIn)
        {
            return AccountResult.Failure(messages["Error_BuiltInGroupProtected"]);
        }

        // Cascade would work, but silently stripping every role composed of the group is exactly
        // the kind of change nobody expects from a delete button.
        if (await identityDbContext.RolePermissionGroups.AnyAsync(l => l.PermissionGroupId == groupId, cancellationToken))
        {
            return AccountResult.Failure(messages["Error_PermissionGroupInUse"]);
        }

        var name = group.Name;
        identityDbContext.PermissionGroups.Remove(group);
        await identityDbContext.SaveChangesAsync(cancellationToken);

        await AuditAsync(actorId, $"deleted permission group {name}", cancellationToken);
        logger.LogInformation("Actor {ActorId} deleted permission group {Group}", actorId, name);
        return AccountResult.Success;
    }

    private AccountResult? Validate(IReadOnlyList<string> permissions)
    {
        foreach (var permission in permissions)
        {
            if (!Permissions.IsKnown(permission))
            {
                return AccountResult.Failure(messages["Error_UnknownPermission", permission]);
            }
        }

        return null;
    }

    /// <summary>
    /// Refuses to put a permission into a group when the actor does not hold it themselves. Group
    /// administration is where power is defined, so without this it would be the shortest path to
    /// every permission: add what you want to a group your own role is composed of.
    /// </summary>
    private async Task<AccountResult?> GuardGrantAsync(
        IReadOnlyCollection<string> granted,
        Guid actorId,
        CancellationToken cancellationToken)
    {
        if (granted.Count == 0)
        {
            return null;
        }

        var held = await EffectivePermissions.ForUserAsync(identityDbContext, actorId, cancellationToken);
        var overreach = granted.Where(p => !held.Contains(p)).ToArray();
        if (overreach.Length == 0)
        {
            return null;
        }

        logger.LogWarning("Actor {ActorId} attempted to put permissions they do not hold into a group: {Permissions}",
            actorId, string.Join(", ", overreach));
        return AccountResult.Failure(messages["Error_PermissionNotHeld", string.Join(", ", overreach)]);
    }

    private async Task AuditAsync(Guid actorId, string detail, CancellationToken cancellationToken)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        dbContext.AccountAuditEvents.Add(new AccountAuditEvent(
            actorId,
            AccountAuditEventType.RolePermissionsChanged,
            $"admin:{actorId}",
            AuditDetail.Truncate(detail),
            timeProvider.GetUtcNow()));
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private static string Join(IReadOnlyCollection<string> permissions) =>
        permissions.Count == 0 ? "—" : string.Join(", ", permissions);

    private static string Describe(IReadOnlyCollection<string> granted, IReadOnlyCollection<string> revoked)
    {
        var parts = granted.Select(p => $"+{p}").Concat(revoked.Select(p => $"-{p}"));
        return parts.Any() ? string.Join(", ", parts) : "no change";
    }
}
