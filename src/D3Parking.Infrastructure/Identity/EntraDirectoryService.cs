using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using D3Parking.Application.Accounts;
using D3Parking.Application.Identity;
using D3Parking.Domain.Accounts;
using D3Parking.Domain.Authorization;
using D3Parking.Infrastructure.Persistence;

namespace D3Parking.Infrastructure.Identity;

/// <summary>
/// The single path from "Entra says this person exists" to an account in this application.
/// </summary>
public sealed class EntraDirectoryService(
    UserManager<ApplicationUser> userManager,
    // The scoped context the user manager writes through, so the account, its role changes and the
    // provenance records commit together.
    D3ParkingDbContext dbContext,
    IOptions<EntraIdOptions> options,
    IStringLocalizer<AccountMessages> messages,
    TimeProvider timeProvider,
    ILogger<EntraDirectoryService> logger) : IExternalDirectoryService
{
    private readonly EntraIdOptions _options = options.Value;

    public async Task<Guid?> FindAsync(string provider, string externalObjectId, CancellationToken cancellationToken = default)
    {
        var id = await dbContext.Users
            .Where(u => u.ExternalProvider == provider && u.ExternalObjectId == externalObjectId)
            .Select(u => (Guid?)u.Id)
            .FirstOrDefaultAsync(cancellationToken);

        return id;
    }

    public async Task<ExternalSyncResult> SyncAsync(ExternalIdentity identity, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(identity.ObjectId) || string.IsNullOrWhiteSpace(identity.Email))
        {
            return ExternalSyncResult.Failure(messages["Error_ExternalIdentityIncomplete"]);
        }

        // A token from another directory must never reach an account here, whatever else it claims.
        if (!string.IsNullOrWhiteSpace(_options.TenantId)
            && !string.IsNullOrWhiteSpace(identity.TenantId)
            && !string.Equals(_options.TenantId, identity.TenantId, StringComparison.OrdinalIgnoreCase))
        {
            logger.LogWarning("Rejected an identity from tenant {Tenant}; this installation accepts {Expected}",
                identity.TenantId, _options.TenantId);
            return ExternalSyncResult.Failure(messages["Error_ExternalTenantNotAllowed"]);
        }

        var (user, created, linked) = await ResolveAsync(identity, cancellationToken);
        if (user is null)
        {
            return ExternalSyncResult.Failure(messages["Error_ExternalAccountUnknown"]);
        }

        await UpdateProfileAsync(user, identity, cancellationToken);

        // Null roles means "this caller has nothing to say about roles" — SCIM pushes accounts
        // without them. Treating that as an empty set would revoke everything on every push.
        if (identity.Roles is not null)
        {
            await ApplyRolesAsync(user, identity.Provider, identity.Roles, cancellationToken);
        }
        else if (created && _options.DefaultRoles.Count > 0)
        {
            await ApplyRolesAsync(user, identity.Provider, _options.DefaultRoles.ToArray(), cancellationToken);
        }

        if (created || linked)
        {
            await AuditAsync(user.Id, AccountAuditEventType.Registered,
                created ? $"created from {identity.Provider}" : $"linked to {identity.Provider}", cancellationToken);
        }

        return ExternalSyncResult.Success(user.Id, created, linked);
    }

    public async Task<AccountResult> SetActiveAsync(
        string provider,
        string externalObjectId,
        bool active,
        CancellationToken cancellationToken = default)
    {
        var user = await dbContext.Users
            .FirstOrDefaultAsync(u => u.ExternalProvider == provider && u.ExternalObjectId == externalObjectId, cancellationToken);
        if (user is null)
        {
            return AccountResult.Failure(messages["Error_AccountNotFound"]);
        }

        var target = active ? AccountStatus.Active : AccountStatus.Blocked;
        if (user.Status == target)
        {
            return AccountResult.Success;
        }

        // The directory does not know this installation needs someone able to administer it. A
        // departure that would leave nobody is refused and logged loudly rather than obeyed.
        if (!active && await AdministratorCover.IsLastAdministratorAsync(dbContext, user.Id, cancellationToken))
        {
            logger.LogError("Refused to deactivate {UserId} from {Provider}: they are the last administrator who can sign in",
                user.Id, provider);
            return AccountResult.Failure(messages["Error_LastAdministrator"]);
        }

        user.Status = target;
        user.StatusChangedAtUtc = timeProvider.GetUtcNow();
        user.StatusReason = active ? null : $"deprovisioned by {provider}";
        user.ExternalSyncedAtUtc = timeProvider.GetUtcNow();

        var updated = await userManager.UpdateAsync(user);
        if (!updated.Succeeded)
        {
            return AccountResult.Failure(updated.Errors.Select(e => e.Description).ToArray());
        }

        // Blocking has to end live sessions, not just close the front door.
        await userManager.UpdateSecurityStampAsync(user);

        await AuditAsync(user.Id, active ? AccountAuditEventType.Unblocked : AccountAuditEventType.Blocked,
            $"{(active ? "reactivated" : "deprovisioned")} by {provider}", cancellationToken);
        logger.LogInformation("{Provider} set {UserId} to {Status}", provider, user.Id, target);
        return AccountResult.Success;
    }

    /// <summary>Finds the account this identity belongs to, adopting or creating one if allowed.</summary>
    private async Task<(ApplicationUser? User, bool Created, bool Linked)> ResolveAsync(
        ExternalIdentity identity,
        CancellationToken cancellationToken)
    {
        var byObjectId = await dbContext.Users.FirstOrDefaultAsync(
            u => u.ExternalProvider == identity.Provider && u.ExternalObjectId == identity.ObjectId,
            cancellationToken);
        if (byObjectId is not null)
        {
            return (byObjectId, false, false);
        }

        if (_options.LinkByVerifiedEmail && await userManager.FindByEmailAsync(identity.Email) is { } byEmail)
        {
            // Only an account whose owner already proved the address, and which is not already tied
            // to some other directory object. Without the confirmation check, anyone who could get
            // an Entra account with a given address would inherit whatever that address registered
            // here — the classic account-takeover-by-email-claim.
            if (!byEmail.EmailConfirmed)
            {
                logger.LogWarning("Refused to link {Email} to {Provider}: the local account's email is unconfirmed",
                    identity.Email, identity.Provider);
                return (null, false, false);
            }

            if (byEmail.ExternalObjectId is not null)
            {
                logger.LogWarning("Refused to link {Email} to {Provider}: already linked to another directory object",
                    identity.Email, identity.Provider);
                return (null, false, false);
            }

            byEmail.ExternalProvider = identity.Provider;
            byEmail.ExternalObjectId = identity.ObjectId;
            byEmail.ExternalTenantId = identity.TenantId;
            return (byEmail, false, true);
        }

        if (!_options.AllowJustInTimeProvisioning)
        {
            logger.LogWarning("Refused an unprovisioned {Provider} identity {ObjectId}; just-in-time creation is off",
                identity.Provider, identity.ObjectId);
            return (null, false, false);
        }

        var user = new ApplicationUser
        {
            UserName = identity.Email,
            Email = identity.Email,
            // The directory owns the address and has already verified it; asking the person to
            // confirm an email they sign in with would be theatre.
            EmailConfirmed = true,
            DisplayName = identity.DisplayName,
            Department = identity.Department,
            Status = identity.Active ? AccountStatus.Active : AccountStatus.Blocked,
            StatusChangedAtUtc = timeProvider.GetUtcNow(),
            ExternalProvider = identity.Provider,
            ExternalObjectId = identity.ObjectId,
            ExternalTenantId = identity.TenantId,
        };

        // No password: sign-in for this account goes through the directory, and a local password
        // would be a second door nobody is watching.
        var created = await userManager.CreateAsync(user);
        if (!created.Succeeded)
        {
            logger.LogError("Failed to create an account for {Provider} identity {ObjectId}: {Errors}",
                identity.Provider, identity.ObjectId, string.Join("; ", created.Errors.Select(e => e.Description)));
            return (null, false, false);
        }

        return (user, true, false);
    }

    private async Task UpdateProfileAsync(ApplicationUser user, ExternalIdentity identity, CancellationToken cancellationToken)
    {
        user.ExternalProvider = identity.Provider;
        user.ExternalObjectId = identity.ObjectId;
        user.ExternalTenantId = identity.TenantId;
        user.ExternalSyncedAtUtc = timeProvider.GetUtcNow();

        if (!string.IsNullOrWhiteSpace(identity.DisplayName))
        {
            user.DisplayName = identity.DisplayName;
        }

        if (!string.IsNullOrWhiteSpace(identity.Department))
        {
            user.Department = identity.Department;
        }

        // A rename in the directory follows through, but a blocked account is not quietly revived:
        // that decision belongs to SetActiveAsync, where the administrator guard lives.
        if (!string.Equals(user.Email, identity.Email, StringComparison.OrdinalIgnoreCase))
        {
            user.Email = identity.Email;
            user.UserName = identity.Email;
        }

        await userManager.UpdateAsync(user);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Makes the user's directory-granted roles match what the directory currently says.
    /// </summary>
    /// <remarks>
    /// Only assignments this application previously made on the directory's behalf are taken back;
    /// a role an administrator granted by hand survives every sync. That provenance is what makes
    /// the two kinds of grant coexist instead of overwriting each other.
    /// </remarks>
    private async Task ApplyRolesAsync(
        ApplicationUser user,
        string provider,
        IReadOnlyList<string> externalRoles,
        CancellationToken cancellationToken)
    {
        var mapped = await (from mapping in dbContext.ExternalRoleMappings
                            join role in dbContext.Roles on mapping.RoleId equals role.Id
                            where mapping.Provider == provider && externalRoles.Contains(mapping.ExternalRole)
                            select new { role.Id, Name = role.Name! })
            .ToListAsync(cancellationToken);

        var unmapped = externalRoles.Except(
            await dbContext.ExternalRoleMappings
                .Where(m => m.Provider == provider)
                .Select(m => m.ExternalRole)
                .ToListAsync(cancellationToken),
            StringComparer.OrdinalIgnoreCase).ToArray();
        if (unmapped.Length > 0)
        {
            // Not an error — a directory may carry roles that mean nothing here — but silence would
            // make "why did nothing happen" unanswerable.
            logger.LogInformation("{Provider} sent roles with no mapping, ignored: {Roles}",
                provider, string.Join(", ", unmapped));
        }

        var granted = mapped.ToDictionary(m => m.Id, m => m.Name);
        var managed = await dbContext.ExternalRoleAssignments
            .Where(a => a.UserId == user.Id && a.Provider == provider)
            .ToListAsync(cancellationToken);
        var currentNames = await userManager.GetRolesAsync(user);

        var addNames = granted.Values
            .Where(name => !currentNames.Contains(name, StringComparer.Ordinal))
            .ToList();

        var revokedIds = managed.Select(a => a.RoleId).Where(id => !granted.ContainsKey(id)).ToHashSet();
        var revoked = await dbContext.Roles
            .Where(r => revokedIds.Contains(r.Id))
            .Select(r => new { r.Id, Name = r.Name! })
            .ToListAsync(cancellationToken);

        // The directory has no idea this installation needs somebody able to administer it. Keep
        // the role against the mapping rather than locking everyone out on a routine sync — and
        // keep its provenance record too, so a later sync can still take it back once there is cover.
        var keptAdministrator = revoked.FirstOrDefault(r => r.Name == Roles.Administrator);
        if (keptAdministrator is not null
            && await AdministratorCover.IsLastAdministratorAsync(dbContext, user.Id, cancellationToken))
        {
            logger.LogError("Kept the Administrator role on {UserId} against {Provider}: they are the last administrator who can sign in",
                user.Id, provider);
            revoked.Remove(keptAdministrator);
            granted[keptAdministrator.Id] = keptAdministrator.Name;
        }

        var provenanceChanged = !managed.Select(a => a.RoleId).ToHashSet().SetEquals(granted.Keys);
        if (addNames.Count == 0 && revoked.Count == 0 && !provenanceChanged)
        {
            return;
        }

        if (revoked.Count > 0)
        {
            await userManager.RemoveFromRolesAsync(user, revoked.Select(r => r.Name));
        }

        if (addNames.Count > 0)
        {
            await userManager.AddToRolesAsync(user, addNames);
        }

        // Rewrite the provenance so it always equals what this application currently holds on the
        // directory's behalf.
        dbContext.ExternalRoleAssignments.RemoveRange(managed);
        foreach (var roleId in granted.Keys)
        {
            dbContext.ExternalRoleAssignments.Add(new ExternalRoleAssignment(user.Id, roleId, provider));
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        if (addNames.Count > 0 || revoked.Count > 0)
        {
            // Role changes have to reach a signed-in session, not wait for the next sign-in.
            await userManager.UpdateSecurityStampAsync(user);
            await AuditAsync(user.Id, AccountAuditEventType.RolesChanged,
                $"{provider}: +[{Join(addNames)}] -[{Join(revoked.Select(r => r.Name))}]", cancellationToken);
            logger.LogInformation("{Provider} set roles of {UserId}: +{Added} -{Removed}",
                provider, user.Id, addNames.Count, revoked.Count);
        }
    }

    private async Task AuditAsync(Guid userId, AccountAuditEventType type, string detail, CancellationToken cancellationToken)
    {
        dbContext.AccountAuditEvents.Add(new AccountAuditEvent(
            userId, type, "directory", detail, timeProvider.GetUtcNow()));
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private static string Join(IEnumerable<string> values)
    {
        var joined = string.Join(", ", values);
        return joined.Length == 0 ? "—" : joined;
    }
}
