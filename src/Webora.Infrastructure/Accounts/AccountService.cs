using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Webora.Application.Abstractions.Email;
using Webora.Application.Accounts;
using Webora.Application.Mapping;
using Webora.Application.Notifications;
using Webora.Domain.Accounts;
using Webora.Domain.Notifications;
using Webora.Infrastructure.Identity;
using Webora.Infrastructure.Persistence;

namespace Webora.Infrastructure.Accounts;

public sealed class AccountService(
    UserManager<ApplicationUser> userManager,
    WeboraDbContext dbContext,
    IEmailSender emailSender,
    AccountAuditMapper auditMapper,
    INotificationService notifications,
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
            return AccountResult.Failure("Účet s tímto e-mailem již existuje.");
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
        await SendActivationEmailCoreAsync(user, cancellationToken);
        return AccountResult.Success;
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
        var link = BuildLink("account/confirm-email", ("userId", user.Id.ToString()), ("token", token));
        await SendAsync(user, "Aktivace účtu",
            $"<p>Aktivujte svůj účet kliknutím na odkaz:</p><p><a href=\"{link}\">Aktivovat účet</a></p>");

        await AuditAsync(user.Id, AccountAuditEventType.ActivationRequested, "self", null, cancellationToken);
    }

    public async Task<AccountResult> ConfirmEmailAsync(Guid userId, string token, CancellationToken cancellationToken = default)
    {
        var user = await FindAsync(userId);
        if (user is null)
        {
            return NotFound();
        }

        var result = await userManager.ConfirmEmailAsync(user, token);
        if (!result.Succeeded)
        {
            return ToFailure(result);
        }

        if (user.Status == AccountStatus.PendingActivation)
        {
            return await TransitionAsync(user, AccountStatus.Active, "self", null, AccountAuditEventType.Activated, cancellationToken);
        }

        await AuditAsync(user.Id, AccountAuditEventType.Activated, "self", "email re-confirmed", cancellationToken);
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
        await notifications.NotifyAsync(user.Id, NotificationLevel.Security, "Heslo změněno",
            "Heslo k vašemu účtu bylo změněno.", cancellationToken);
        return AccountResult.Success;
    }

    public async Task<AccountResult> RequestPasswordResetAsync(string email, CancellationToken cancellationToken = default)
    {
        var user = await userManager.FindByEmailAsync(email);
        if (user is not null)
        {
            var token = await userManager.GeneratePasswordResetTokenAsync(user);
            var link = BuildLink("account/reset-password", ("email", email), ("token", token));
            await SendAsync(user, "Obnovení hesla",
                $"<p>Pro nastavení nového hesla klikněte na odkaz:</p><p><a href=\"{link}\">Obnovit heslo</a></p>" +
                "<p>Pokud jste o obnovení nežádali, e-mail ignorujte.</p>");
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
            return AccountResult.Failure("Neplatný požadavek na obnovení hesla.");
        }

        var result = await userManager.ResetPasswordAsync(user, token, newPassword);
        if (!result.Succeeded)
        {
            return ToFailure(result);
        }

        await AuditAsync(user.Id, AccountAuditEventType.PasswordReset, "self", null, cancellationToken);
        await notifications.NotifyAsync(user.Id, NotificationLevel.Security, "Heslo obnoveno",
            "Heslo k vašemu účtu bylo obnoveno.", cancellationToken);
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
        var link = BuildLink("account/confirm-email-change", ("userId", user.Id.ToString()), ("email", newEmail), ("token", token));
        await SendToAsync(newEmail, user.DisplayName, "Potvrzení změny e-mailu",
            $"<p>Potvrďte změnu e-mailu kliknutím na odkaz:</p><p><a href=\"{link}\">Potvrdit nový e-mail</a></p>");

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

        var result = await userManager.ChangeEmailAsync(user, newEmail, token);
        if (!result.Succeeded)
        {
            return ToFailure(result);
        }

        // Email doubles as the username in Webora, so keep them in sync.
        var renamed = await userManager.SetUserNameAsync(user, newEmail);
        if (!renamed.Succeeded)
        {
            return ToFailure(renamed);
        }

        await AuditAsync(user.Id, AccountAuditEventType.EmailChanged, "self", newEmail, cancellationToken);
        await notifications.NotifyAsync(user.Id, NotificationLevel.Security, "E-mail změněn",
            $"Přihlašovací e-mail byl změněn na {newEmail}.", cancellationToken);
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
        await SendAsync(user, "Kód pro změnu telefonu",
            $"<p>Váš ověřovací kód pro změnu telefonního čísla je:</p><p style=\"font-size:1.25rem;\"><strong>{code}</strong></p>");

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
        await notifications.NotifyAsync(user.Id, NotificationLevel.Info, "Telefon změněn",
            $"Telefonní číslo bylo změněno na {newPhoneNumber}.", cancellationToken);
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
            var link = BuildLink("account/confirm-reactivation", ("userId", user.Id.ToString()), ("token", token));
            await SendAsync(user, "Obnovení účtu",
                $"<p>Obnovte svůj účet kliknutím na odkaz:</p><p><a href=\"{link}\">Obnovit účet</a></p>");
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
            return AccountResult.Failure("Neplatný nebo prošlý odkaz pro obnovení účtu.");
        }

        return await TransitionAsync(user, AccountStatus.Active, "self", null, AccountAuditEventType.Reactivated, cancellationToken);
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
            return AccountResult.Failure("Uspat lze jen aktivní účet.");
        }

        var token = await userManager.GenerateUserTokenAsync(user, TokenOptions.DefaultProvider, SuspendTokenPurpose);
        var link = BuildLink("account/confirm-suspend", ("userId", user.Id.ToString()), ("token", token));
        await SendAsync(user, "Potvrzení uspání účtu",
            $"<p>Uspání účtu potvrďte kliknutím na odkaz:</p><p><a href=\"{link}\">Uspat účet</a></p>");

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
            return AccountResult.Failure("Neplatný nebo prošlý potvrzovací token.");
        }

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
            return AccountResult.Failure($"Přechod ze stavu '{user.Status}' do '{target}' není povolen.");
        }

        user.Status = target;
        user.StatusChangedAtUtc = timeProvider.GetUtcNow();
        user.StatusReason = reason;

        var result = await userManager.UpdateAsync(user);
        if (!result.Succeeded)
        {
            return ToFailure(result);
        }

        await AuditAsync(user.Id, auditType, actor, reason, cancellationToken);
        await NotifyTransitionAsync(user.Id, auditType, reason, cancellationToken);
        logger.LogInformation("Account {UserId} transitioned to {Status} by {Actor}", user.Id, target, actor);
        return AccountResult.Success;
    }

    private async Task NotifyTransitionAsync(Guid userId, AccountAuditEventType type, string? reason, CancellationToken cancellationToken)
    {
        (NotificationLevel Level, string Title, string Message)? n = type switch
        {
            AccountAuditEventType.Activated => (NotificationLevel.Info, "Účet aktivován", "Váš účet byl aktivován."),
            AccountAuditEventType.Deactivated => (NotificationLevel.Warning, "Účet deaktivován", "Váš účet byl deaktivován."),
            AccountAuditEventType.Reactivated => (NotificationLevel.Info, "Účet obnoven", "Váš účet byl obnoven."),
            AccountAuditEventType.Suspended => (NotificationLevel.Warning, "Účet uspán", "Váš účet byl uspán."),
            AccountAuditEventType.Blocked => (NotificationLevel.Security, "Účet zablokován",
                reason is null ? "Váš účet byl zablokován administrátorem." : $"Váš účet byl zablokován administrátorem: {reason}"),
            AccountAuditEventType.Unblocked => (NotificationLevel.Info, "Účet odblokován", "Váš účet byl odblokován."),
            _ => null,
        };

        if (n is { } notification)
        {
            await notifications.NotifyAsync(userId, notification.Level, notification.Title, notification.Message, cancellationToken);
        }
    }

    private async Task AuditAsync(Guid userId, AccountAuditEventType type, string actor, string? detail, CancellationToken cancellationToken)
    {
        dbContext.AccountAuditEvents.Add(new AccountAuditEvent(userId, type, actor, detail, timeProvider.GetUtcNow()));
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private Task<ApplicationUser?> FindAsync(Guid userId) => userManager.FindByIdAsync(userId.ToString());

    private Task SendAsync(ApplicationUser user, string subject, string html) =>
        SendToAsync(user.Email!, user.DisplayName, subject, html);

    private Task SendToAsync(string email, string? name, string subject, string html) =>
        emailSender.SendAsync(new EmailMessage { To = email, ToName = name, Subject = subject, HtmlBody = html });

    private string BuildLink(string path, params (string Key, string Value)[] query)
    {
        var baseUrl = options.Value.BaseUrl.TrimEnd('/');
        var queryString = string.Join('&', query.Select(q => $"{q.Key}={Uri.EscapeDataString(q.Value)}"));
        return $"{baseUrl}/{path}?{queryString}";
    }

    private static AccountResult NotFound() => AccountResult.Failure("Účet nenalezen.");

    private static AccountResult ToFailure(IdentityResult result) =>
        AccountResult.Failure(result.Errors.Select(e => e.Description).ToArray());
}
