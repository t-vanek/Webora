using System.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using D3Parking.Application.Abstractions.Email;
using D3Parking.Application.Accounts;
using D3Parking.Application.Mapping;
using D3Parking.Application.Notifications;
using D3Parking.Application.Settings;
using D3Parking.Domain.Accounts;
using D3Parking.Domain.Authorization;
using D3Parking.Domain.Notifications;
using D3Parking.Infrastructure.Identity;
using D3Parking.Infrastructure.Persistence;

namespace D3Parking.Infrastructure.Accounts;

public sealed class AccountService(
    UserManager<ApplicationUser> userManager,
    RoleManager<ApplicationRole> roleManager,
    // The scoped context is the SAME instance UserManager's store writes through (both resolve it
    // from the scope), which is what lets ConfirmEmailChangeAsync wrap Identity's separate saves
    // in one transaction. The factory below is for independent reads/writes.
    D3ParkingDbContext identityDbContext,
    IDbContextFactory<D3ParkingDbContext> dbContextFactory,
    IEmailSender emailSender,
    AccountAuditMapper auditMapper,
    INotificationService notifications,
    ISiteSettingsService siteSettings,
    IStringLocalizer<AccountMessages> messages,
    IOptions<AccountOptions> options,
    TimeProvider timeProvider,
    ILogger<AccountService> logger) : IAccountService
{
    private const string SuspendTokenPurpose = "SuspendAccount";
    private const string ReactivateTokenPurpose = "ReactivateAccount";

    public async Task<AccountResult> RegisterAsync(string email, string password, string? displayName, CancellationToken cancellationToken = default)
    {
        if (await userManager.FindByEmailAsync(email) is not null)
        {
            return AccountResult.Failure(messages["Error_EmailExists"]);
        }

        var user = new ApplicationUser
        {
            UserName = email,
            Email = email,
            DisplayName = displayName,
            Status = AccountStatus.PendingActivation,
        };

        var created = await userManager.CreateAsync(user, password);
        if (!created.Succeeded)
        {
            return ToFailure(created);
        }

        await AuditAsync(user.Id, AccountAuditEventType.Registered, "self", null, cancellationToken);
        await AssignDefaultRoleAsync(user, cancellationToken);
        await SendActivationEmailCoreAsync(user, cancellationToken);
        return AccountResult.Success;
    }

    private async Task AssignDefaultRoleAsync(ApplicationUser user, CancellationToken cancellationToken)
    {
        var role = await siteSettings.GetDefaultRoleAsync(cancellationToken);
        if (string.IsNullOrEmpty(role))
        {
            return;
        }

        if (!await roleManager.RoleExistsAsync(role))
        {
            logger.LogWarning("Configured default role {Role} does not exist; skipping assignment for {UserId}", role, user.Id);
            return;
        }

        var result = await userManager.AddToRoleAsync(user, role);
        if (result.Succeeded)
        {
            await AuditAsync(user.Id, AccountAuditEventType.RolesChanged, "self", $"default role: {role}", cancellationToken);
        }
        else
        {
            logger.LogWarning("Failed to assign default role {Role} to {UserId}: {Errors}", role, user.Id,
                string.Join("; ", result.Errors.Select(e => e.Description)));
        }
    }

    public async Task<AccountResult> SendActivationEmailAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var user = await FindAsync(userId);
        if (user is null)
        {
            return NotFound();
        }

        await SendActivationEmailCoreAsync(user, cancellationToken);
        return AccountResult.Success;
    }

    private async Task SendActivationEmailCoreAsync(ApplicationUser user, CancellationToken cancellationToken)
    {
        var token = await userManager.GenerateEmailConfirmationTokenAsync(user);
        var link = await BuildLinkAsync("account/confirm-email", ("userId", user.Id.ToString()), ("token", token));
        await SendAsync(user, messages["Email_Activation_Subject"], messages["Email_Activation_Body", link]);

        await AuditAsync(user.Id, AccountAuditEventType.ActivationRequested, "self", null, cancellationToken);
    }

    public async Task<AccountResult> ConfirmEmailAsync(Guid userId, string token, CancellationToken cancellationToken = default)
    {
        var user = await FindAsync(userId);
        if (user is null)
        {
            return NotFound();
        }

        var alreadyConfirmed = await userManager.IsEmailConfirmedAsync(user);
        var result = await userManager.ConfirmEmailAsync(user, token);
        if (!result.Succeeded)
        {
            return ToFailure(result);
        }

        if (user.Status == AccountStatus.PendingActivation)
        {
            return await TransitionAsync(user, AccountStatus.Active, "self", null, AccountAuditEventType.Activated, cancellationToken);
        }

        // The confirmation link stays valid until the security stamp changes, so it can be
        // re-clicked; only a confirmation that actually flipped the flag deserves an audit row —
        // a replay on an already-confirmed account must not keep stamping "Activated" entries.
        if (!alreadyConfirmed)
        {
            await AuditAsync(user.Id, AccountAuditEventType.Activated, "self", "email re-confirmed", cancellationToken);
        }

        return AccountResult.Success;
    }

    public async Task<AccountResult> ChangePasswordAsync(Guid userId, string currentPassword, string newPassword, CancellationToken cancellationToken = default)
    {
        var user = await FindAsync(userId);
        if (user is null)
        {
            return NotFound();
        }

        var result = await userManager.ChangePasswordAsync(user, currentPassword, newPassword);
        if (!result.Succeeded)
        {
            return ToFailure(result);
        }

        await AuditAsync(user.Id, AccountAuditEventType.PasswordChanged, "self", null, cancellationToken);
        // Security events always mirror to email — the mailbox is the channel an account thief
        // cannot silently clear, and the standard place people expect this heads-up.
        await NotifyKeysAsync(user.Id, NotificationCategory.SelfService, NotificationLevel.Security, "Notif_PasswordChanged_Title", "Notif_PasswordChanged_Body", cancellationToken, email: true);
        return AccountResult.Success;
    }

    public async Task<AccountResult> RequestPasswordResetAsync(string email, CancellationToken cancellationToken = default)
    {
        var user = await userManager.FindByEmailAsync(email);
        if (user is not null)
        {
            var token = await userManager.GeneratePasswordResetTokenAsync(user);
            var link = await BuildLinkAsync("account/reset-password", ("email", email), ("token", token));
            await SendAsync(user, messages["Email_Reset_Subject"], messages["Email_Reset_Body", link]);
            await AuditAsync(user.Id, AccountAuditEventType.PasswordResetRequested, "self", null, cancellationToken);
        }

        // Always report success so the endpoint does not reveal whether the address exists.
        return AccountResult.Success;
    }

    public async Task<AccountResult> ResetPasswordAsync(string email, string token, string newPassword, CancellationToken cancellationToken = default)
    {
        var user = await userManager.FindByEmailAsync(email);
        if (user is null)
        {
            return AccountResult.Failure(messages["Error_InvalidPasswordReset"]);
        }

        var result = await userManager.ResetPasswordAsync(user, token, newPassword);
        if (!result.Succeeded)
        {
            return ToFailure(result);
        }

        await AuditAsync(user.Id, AccountAuditEventType.PasswordReset, "self", null, cancellationToken);
        await NotifyKeysAsync(user.Id, NotificationCategory.SelfService, NotificationLevel.Security, "Notif_PasswordReset_Title", "Notif_PasswordReset_Body", cancellationToken, email: true);
        return AccountResult.Success;
    }

    public async Task<AccountResult> RequestEmailChangeAsync(Guid userId, string newEmail, CancellationToken cancellationToken = default)
    {
        var user = await FindAsync(userId);
        if (user is null)
        {
            return NotFound();
        }

        var token = await userManager.GenerateChangeEmailTokenAsync(user, newEmail);
        var link = await BuildLinkAsync("account/confirm-email-change", ("userId", user.Id.ToString()), ("email", newEmail), ("token", token));
        await SendToAsync(newEmail, user.DisplayName, messages["Email_EmailChange_Subject"], messages["Email_EmailChange_Body", link]);

        await AuditAsync(user.Id, AccountAuditEventType.EmailChangeRequested, "self", newEmail, cancellationToken);
        return AccountResult.Success;
    }

    public async Task<AccountResult> ConfirmEmailChangeAsync(Guid userId, string newEmail, string token, CancellationToken cancellationToken = default)
    {
        var user = await FindAsync(userId);
        if (user is null)
        {
            return NotFound();
        }

        // Email doubles as the username in D3Parking. The two updates are separate saves inside
        // Identity, so run them in one transaction — a failed rename must not leave the login
        // (username) on the old address while the profile already shows the new one. Returning
        // before Commit disposes the transaction and rolls the email change back.
        await using var transaction = await identityDbContext.Database.BeginTransactionAsync(cancellationToken);

        var result = await userManager.ChangeEmailAsync(user, newEmail, token);
        if (!result.Succeeded)
        {
            return ToFailure(result);
        }

        var renamed = await userManager.SetUserNameAsync(user, newEmail);
        if (!renamed.Succeeded)
        {
            return ToFailure(renamed);
        }

        await transaction.CommitAsync(cancellationToken);

        await AuditAsync(user.Id, AccountAuditEventType.EmailChanged, "self", newEmail, cancellationToken);
        await notifications.NotifyAsync(user.Id, NotificationCategory.SelfService, NotificationLevel.Security,
            messages["Notif_EmailChanged_Title"], messages["Notif_EmailChanged_Body", newEmail], cancellationToken);
        return AccountResult.Success;
    }

    public async Task<AccountResult> RequestPhoneChangeAsync(Guid userId, string newPhoneNumber, CancellationToken cancellationToken = default)
    {
        var user = await FindAsync(userId);
        if (user is null)
        {
            return NotFound();
        }

        var code = await userManager.GenerateChangePhoneNumberTokenAsync(user, newPhoneNumber);
        await SendAsync(user, messages["Email_Phone_Subject"], messages["Email_Phone_Body", code]);

        await AuditAsync(user.Id, AccountAuditEventType.PhoneChangeRequested, "self", newPhoneNumber, cancellationToken);
        return AccountResult.Success;
    }

    public async Task<AccountResult> ConfirmPhoneChangeAsync(Guid userId, string newPhoneNumber, string code, CancellationToken cancellationToken = default)
    {
        var user = await FindAsync(userId);
        if (user is null)
        {
            return NotFound();
        }

        var result = await userManager.ChangePhoneNumberAsync(user, newPhoneNumber, code);
        if (!result.Succeeded)
        {
            return ToFailure(result);
        }

        await AuditAsync(user.Id, AccountAuditEventType.PhoneChanged, "self", newPhoneNumber, cancellationToken);
        await notifications.NotifyAsync(user.Id, NotificationCategory.SelfService, NotificationLevel.Info,
            messages["Notif_PhoneChanged_Title"], messages["Notif_PhoneChanged_Body", newPhoneNumber], cancellationToken);
        return AccountResult.Success;
    }

    public async Task<AccountResult> DeactivateAsync(Guid userId, string? reason, CancellationToken cancellationToken = default)
    {
        var user = await FindAsync(userId);
        return user is null
            ? NotFound()
            : await TransitionAsync(user, AccountStatus.Deactivated, "self", reason, AccountAuditEventType.Deactivated, cancellationToken);
    }

    public async Task<AccountResult> ReactivateAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var user = await FindAsync(userId);
        return user is null
            ? NotFound()
            : await TransitionAsync(user, AccountStatus.Active, "self", null, AccountAuditEventType.Reactivated, cancellationToken);
    }

    public async Task<AccountResult> RequestReactivationAsync(string email, CancellationToken cancellationToken = default)
    {
        var user = await userManager.FindByEmailAsync(email);
        if (user is not null && user.Status is AccountStatus.Deactivated or AccountStatus.Suspended)
        {
            var token = await userManager.GenerateUserTokenAsync(user, TokenOptions.DefaultProvider, ReactivateTokenPurpose);
            var link = await BuildLinkAsync("account/confirm-reactivation", ("userId", user.Id.ToString()), ("token", token));
            await SendAsync(user, messages["Email_Reactivation_Subject"], messages["Email_Reactivation_Body", link]);
            await AuditAsync(user.Id, AccountAuditEventType.ReactivationRequested, "self", null, cancellationToken);
        }

        // Always report success so the endpoint does not reveal whether the address exists.
        return AccountResult.Success;
    }

    public async Task<AccountResult> ConfirmReactivationAsync(Guid userId, string token, CancellationToken cancellationToken = default)
    {
        var user = await FindAsync(userId);
        if (user is null)
        {
            return NotFound();
        }

        var valid = await userManager.VerifyUserTokenAsync(user, TokenOptions.DefaultProvider, ReactivateTokenPurpose, token);
        if (!valid)
        {
            return AccountResult.Failure(messages["Error_InvalidReactivationToken"]);
        }

        var result = await TransitionAsync(user, AccountStatus.Active, "self", null, AccountAuditEventType.Reactivated, cancellationToken);
        if (result.Succeeded)
        {
            // The confirmation token is stateless; rotating the security stamp invalidates it (and
            // any other outstanding token), so a used link can't be replayed to flip the account
            // state again later.
            await userManager.UpdateSecurityStampAsync(user);
        }

        return result;
    }

    public async Task<AccountResult> RequestSuspendAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var user = await FindAsync(userId);
        if (user is null)
        {
            return NotFound();
        }

        if (user.Status != AccountStatus.Active)
        {
            return AccountResult.Failure(messages["Error_SuspendOnlyActive"]);
        }

        var token = await userManager.GenerateUserTokenAsync(user, TokenOptions.DefaultProvider, SuspendTokenPurpose);
        var link = await BuildLinkAsync("account/confirm-suspend", ("userId", user.Id.ToString()), ("token", token));
        await SendAsync(user, messages["Email_Suspend_Subject"], messages["Email_Suspend_Body", link]);

        await AuditAsync(user.Id, AccountAuditEventType.SuspendRequested, "self", null, cancellationToken);
        return AccountResult.Success;
    }

    public async Task<AccountResult> ConfirmSuspendAsync(Guid userId, string token, CancellationToken cancellationToken = default)
    {
        var user = await FindAsync(userId);
        if (user is null)
        {
            return NotFound();
        }

        var valid = await userManager.VerifyUserTokenAsync(user, TokenOptions.DefaultProvider, SuspendTokenPurpose, token);
        if (!valid)
        {
            return AccountResult.Failure(messages["Error_InvalidSuspendToken"]);
        }

        // TransitionAsync rotates the security stamp for the non-Active target, which both makes
        // the used link single-shot (the token embeds the stamp) and invalidates the suspended
        // account's other live sessions.
        return await TransitionAsync(user, AccountStatus.Suspended, "self", null, AccountAuditEventType.Suspended, cancellationToken);
    }

    public async Task<AccountResult> BlockAsync(Guid userId, Guid adminId, string? reason, CancellationToken cancellationToken = default)
    {
        var user = await FindAsync(userId);
        return user is null
            ? NotFound()
            : await TransitionAsync(user, AccountStatus.Blocked, $"admin:{adminId}", reason, AccountAuditEventType.Blocked, cancellationToken);
    }

    public async Task<AccountResult> UnblockAsync(Guid userId, Guid adminId, CancellationToken cancellationToken = default)
    {
        var user = await FindAsync(userId);
        return user is null
            ? NotFound()
            : await TransitionAsync(user, AccountStatus.Active, $"admin:{adminId}", null, AccountAuditEventType.Unblocked, cancellationToken);
    }

    public async Task<IReadOnlyList<AccountAuditEntry>> GetAuditTrailAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var query = dbContext.AccountAuditEvents
            .AsNoTracking()
            .Where(e => e.UserId == userId)
            .OrderByDescending(e => e.OccurredAtUtc);

        return await auditMapper.ProjectToEntries(query).ToListAsync(cancellationToken);
    }

    private async Task<AccountResult> TransitionAsync(
        ApplicationUser user,
        AccountStatus target,
        string actor,
        string? reason,
        AccountAuditEventType auditType,
        CancellationToken cancellationToken)
    {
        if (user.Status == target)
        {
            return AccountResult.Success;
        }

        if (!AccountStatusTransitions.IsAllowed(user.Status, target))
        {
            return AccountResult.Failure(messages["Error_TransitionNotAllowed", user.Status, target]);
        }

        // The guard and the status write ride one serializable transaction on the scoped context
        // (the same one UserManager saves through), closing the check-then-act window in which two
        // admins disabling each other could both pass the last-administrator count.
        await using var transaction = await identityDbContext.Database
            .BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);

        // Deleting the last administrator and stripping the role are already refused
        // (UserAdminService); disabling the account is the same lockout through a different door —
        // a blocked admin cannot sign in, and only an admin could unblock them.
        if (target != AccountStatus.Active && await IsLastAdministratorAsync(user))
        {
            return AccountResult.Failure(messages["Error_LastAdministrator"]);
        }

        user.Status = target;
        user.StatusChangedAtUtc = timeProvider.GetUtcNow();
        user.StatusReason = reason;

        var result = await userManager.UpdateAsync(user);
        if (!result.Succeeded)
        {
            return ToFailure(result);
        }

        // A status that forbids sign-in must also end sign-ins that already happened. Only login
        // consults the status (D3ParkingSignInManager.CanSignInAsync); existing cookies and Blazor
        // circuits live on until their security stamp stops validating — so rotate it, and the
        // blocked/suspended/deactivated user is signed out at the next revalidation instead of
        // keeping full access indefinitely.
        if (target != AccountStatus.Active)
        {
            var stamped = await userManager.UpdateSecurityStampAsync(user);
            if (!stamped.Succeeded)
            {
                return ToFailure(stamped);
            }
        }

        await transaction.CommitAsync(cancellationToken);

        await AuditAsync(user.Id, auditType, actor, reason, cancellationToken);
        await NotifyTransitionAsync(user.Id, auditType, actor, reason, cancellationToken);
        logger.LogInformation("Account {UserId} transitioned to {Status} by {Actor}", user.Id, target, actor);
        return AccountResult.Success;
    }

    private async Task NotifyTransitionAsync(Guid userId, AccountAuditEventType type, string actor, string? reason, CancellationToken cancellationToken)
    {
        (NotificationLevel Level, string Title, string Body)? n = type switch
        {
            AccountAuditEventType.Activated => (NotificationLevel.Info, messages["Notif_Activated_Title"], messages["Notif_Activated_Body"]),
            AccountAuditEventType.Deactivated => (NotificationLevel.Warning, messages["Notif_Deactivated_Title"], messages["Notif_Deactivated_Body"]),
            AccountAuditEventType.Reactivated => (NotificationLevel.Info, messages["Notif_Reactivated_Title"], messages["Notif_Reactivated_Body"]),
            AccountAuditEventType.Suspended => (NotificationLevel.Warning, messages["Notif_Suspended_Title"], messages["Notif_Suspended_Body"]),
            AccountAuditEventType.Blocked => (NotificationLevel.Security, messages["Notif_Blocked_Title"],
                reason is null ? messages["Notif_Blocked_Body"] : messages["Notif_Blocked_Body_Reason", reason]),
            AccountAuditEventType.Unblocked => (NotificationLevel.Info, messages["Notif_Unblocked_Title"], messages["Notif_Unblocked_Body"]),
            _ => null,
        };

        if (n is { } notification)
        {
            // Admin-driven transitions are Administrative; user-driven ones are SelfService.
            var category = actor.StartsWith("admin:", StringComparison.Ordinal)
                ? NotificationCategory.Administrative
                : NotificationCategory.SelfService;
            await notifications.NotifyAsync(userId, category, notification.Level, notification.Title, notification.Body, cancellationToken);
        }
    }

    private Task NotifyKeysAsync(Guid userId, NotificationCategory category, NotificationLevel level, string titleKey, string bodyKey, CancellationToken cancellationToken, bool email = false) =>
        notifications.NotifyAsync(userId, category, level, messages[titleKey], messages[bodyKey], email, cancellationToken);

    private async Task AuditAsync(Guid userId, AccountAuditEventType type, string actor, string? detail, CancellationToken cancellationToken)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        dbContext.AccountAuditEvents.Add(new AccountAuditEvent(userId, type, actor, detail, timeProvider.GetUtcNow()));
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private Task<ApplicationUser?> FindAsync(Guid userId) => userManager.FindByIdAsync(userId.ToString());

    // Reads through the scoped identity context so the count participates in the caller's
    // serializable transaction (range locks on the admin role's membership rows).
    private async Task<bool> IsLastAdministratorAsync(ApplicationUser user)
    {
        if (!await userManager.IsInRoleAsync(user, Roles.Administrator))
        {
            return false;
        }

        var adminCount = await (from ur in identityDbContext.UserRoles
                                join r in identityDbContext.Roles on ur.RoleId equals r.Id
                                where r.Name == Roles.Administrator
                                select ur.UserId).CountAsync();

        return adminCount <= 1;
    }

    private Task SendAsync(ApplicationUser user, string subject, string html) =>
        SendToAsync(user.Email!, user.DisplayName, subject, html);

    // Wraps the (already localized, link-carrying) body in the shared branded layout, so account
    // emails look like every other mail the app sends.
    private Task SendToAsync(string email, string? name, string subject, string html) =>
        emailSender.SendAsync(new EmailMessage
        {
            To = email,
            ToName = name,
            Subject = subject,
            HtmlBody = D3Parking.Infrastructure.Email.BrandedEmail.RenderHtml(new(
                Heading: subject,
                BodyHtml: html,
                FooterReason: messages["Email_Chrome_Reason"].Value,
                FooterSettings: messages["Email_Chrome_Settings"].Value)),
        });

    private async Task<string> BuildLinkAsync(string path, params (string Key, string Value)[] query)
    {
        var baseUrl = (await siteSettings.GetCanonicalBaseUrlAsync() ?? options.Value.BaseUrl).TrimEnd('/');
        var queryString = string.Join('&', query.Select(q => $"{q.Key}={Uri.EscapeDataString(q.Value)}"));
        return $"{baseUrl}/{path}?{queryString}";
    }

    private AccountResult NotFound() => AccountResult.Failure(messages["Error_AccountNotFound"]);

    private static AccountResult ToFailure(IdentityResult result) =>
        AccountResult.Failure(result.Errors.Select(e => e.Description).ToArray());
}
