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

public sealed class ExternalRoleMappingService(
    D3ParkingDbContext dbContext,
    IDbContextFactory<D3ParkingDbContext> dbContextFactory,
    IStringLocalizer<AccountMessages> messages,
    TimeProvider timeProvider,
    ILogger<ExternalRoleMappingService> logger) : IExternalRoleMappingService
{
    public async Task<IReadOnlyList<ExternalRoleMappingSummary>> ListAsync(string provider, CancellationToken cancellationToken = default)
    {
        await using var readContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);

        return await (from mapping in readContext.ExternalRoleMappings.AsNoTracking()
                      join role in readContext.Roles on mapping.RoleId equals role.Id
                      where mapping.Provider == provider
                      orderby mapping.ExternalRole
                      select new ExternalRoleMappingSummary(
                          mapping.Id,
                          mapping.Provider,
                          mapping.ExternalRole,
                          role.Id,
                          role.Name!,
                          readContext.ExternalRoleAssignments.Count(a => a.RoleId == role.Id && a.Provider == provider)))
            .ToListAsync(cancellationToken);
    }

    public async Task<AccountResult> CreateAsync(
        string provider,
        string externalRole,
        Guid roleId,
        Guid actorId,
        CancellationToken cancellationToken = default)
    {
        externalRole = externalRole?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(externalRole))
        {
            return AccountResult.Failure(messages["Error_ExternalRoleRequired"]);
        }

        if (await dbContext.ExternalRoleMappings.AnyAsync(
                m => m.Provider == provider && m.ExternalRole == externalRole, cancellationToken))
        {
            return AccountResult.Failure(messages["Error_ExternalRoleMapped", externalRole]);
        }

        if (await GuardAssignableAsync(roleId, actorId, cancellationToken) is { } forbidden)
        {
            return forbidden;
        }

        dbContext.ExternalRoleMappings.Add(new ExternalRoleMapping(provider, externalRole, roleId));
        await dbContext.SaveChangesAsync(cancellationToken);

        await AuditAsync(actorId, $"mapped {provider} role {externalRole} to {await RoleNameAsync(roleId, cancellationToken)}", cancellationToken);
        logger.LogInformation("Actor {ActorId} mapped {Provider} role {ExternalRole} to {RoleId}",
            actorId, provider, externalRole, roleId);
        return AccountResult.Success;
    }

    public async Task<AccountResult> RetargetAsync(Guid mappingId, Guid roleId, Guid actorId, CancellationToken cancellationToken = default)
    {
        var mapping = await dbContext.ExternalRoleMappings.FirstOrDefaultAsync(m => m.Id == mappingId, cancellationToken);
        if (mapping is null)
        {
            return AccountResult.Failure(messages["Error_ExternalRoleMappingNotFound"]);
        }

        if (mapping.RoleId == roleId)
        {
            return AccountResult.Success;
        }

        if (await GuardAssignableAsync(roleId, actorId, cancellationToken) is { } forbidden)
        {
            return forbidden;
        }

        var before = await RoleNameAsync(mapping.RoleId, cancellationToken);
        mapping.Retarget(roleId);
        await dbContext.SaveChangesAsync(cancellationToken);

        // Existing members keep the old role until their next sign-in or provisioning push; the
        // mapping decides what future syncs grant, not what already happened.
        await AuditAsync(actorId,
            $"remapped {mapping.Provider} role {mapping.ExternalRole}: {before} → {await RoleNameAsync(roleId, cancellationToken)}",
            cancellationToken);
        return AccountResult.Success;
    }

    public async Task<AccountResult> DeleteAsync(Guid mappingId, Guid actorId, CancellationToken cancellationToken = default)
    {
        var mapping = await dbContext.ExternalRoleMappings.FirstOrDefaultAsync(m => m.Id == mappingId, cancellationToken);
        if (mapping is null)
        {
            return AccountResult.Failure(messages["Error_ExternalRoleMappingNotFound"]);
        }

        var describe = $"{mapping.Provider} role {mapping.ExternalRole}";
        dbContext.ExternalRoleMappings.Remove(mapping);
        await dbContext.SaveChangesAsync(cancellationToken);

        await AuditAsync(actorId, $"removed the mapping for {describe}", cancellationToken);
        logger.LogInformation("Actor {ActorId} removed the mapping for {Describe}", actorId, describe);
        return AccountResult.Success;
    }

    /// <summary>
    /// Refuses to point a directory role at a local role that grants more than the actor holds.
    /// The check mirrors role assignment itself: a mapping is an assignment made in advance, for
    /// everyone the directory will ever put in that role.
    /// </summary>
    private async Task<AccountResult?> GuardAssignableAsync(Guid roleId, Guid actorId, CancellationToken cancellationToken)
    {
        var role = await dbContext.Roles.FirstOrDefaultAsync(r => r.Id == roleId, cancellationToken);
        if (role is null)
        {
            return AccountResult.Failure(messages["Error_UnknownRole", roleId]);
        }

        var held = await EffectivePermissions.ForUserAsync(dbContext, actorId, cancellationToken);
        var grants = await EffectivePermissions.ForRolesAsync(dbContext, [role.Name!], cancellationToken);

        if (grants.TryGetValue(role.Name!, out var granted) && !granted.IsSubsetOf(held))
        {
            logger.LogWarning("Actor {ActorId} attempted to map a directory role onto {Role}, which grants permissions they do not hold",
                actorId, role.Name);
            return AccountResult.Failure(messages["Error_RoleNotAssignable", role.Name!]);
        }

        return null;
    }

    private async Task<string> RoleNameAsync(Guid roleId, CancellationToken cancellationToken) =>
        await dbContext.Roles.Where(r => r.Id == roleId).Select(r => r.Name!).FirstOrDefaultAsync(cancellationToken) ?? "?";

    private async Task AuditAsync(Guid actorId, string detail, CancellationToken cancellationToken)
    {
        await using var auditContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        auditContext.AccountAuditEvents.Add(new AccountAuditEvent(
            actorId,
            AccountAuditEventType.RolePermissionsChanged,
            $"admin:{actorId}",
            AuditDetail.Truncate(detail),
            timeProvider.GetUtcNow()));
        await auditContext.SaveChangesAsync(cancellationToken);
    }
}
