using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using D3Parking.Application.Accounts;
using D3Parking.Application.Identity;
using D3Parking.Application.Settings;
using D3Parking.Domain.Accounts;
using D3Parking.Domain.Authorization;
using D3Parking.Domain.Settings;
using D3Parking.Infrastructure.Persistence;

namespace D3Parking.Infrastructure.Identity;

/// <summary>
/// Merges the host's configuration over the administrator-editable row and keeps the two secrets
/// encrypted at rest.
/// </summary>
/// <remarks>
/// Configuration wins field by field rather than wholesale: a deployment that pins only its tenant
/// id in an environment variable leaves every other field adjustable from the settings page, and a
/// deployment that pins everything gets a page that says so instead of one that accepts edits
/// nothing will ever read.
///
/// "Set" means the key is present and, for text, non-empty. That distinction is what lets the
/// shipped appsettings.json carry documentation without silently freezing the whole feature.
/// </remarks>
public sealed class EntraSettingsService(
    IDbContextFactory<D3ParkingDbContext> dbContextFactory,
    IMemoryCache cache,
    IConfiguration configuration,
    IDataProtectionProvider dataProtectionProvider,
    IEntraRuntimeReloader reloader,
    IStringLocalizer<AccountMessages> messages,
    TimeProvider timeProvider,
    ILogger<EntraSettingsService> logger) : IEntraSettingsService
{
    /// <summary>
    /// The purpose string binds the ciphertext to this use. Changing it makes every stored secret
    /// undecryptable, so it carries a version suffix rather than being edited in place.
    /// </summary>
    private const string ProtectorPurpose = "D3Parking.EntraSettings.v1";

    private readonly IDataProtector _protector = dataProtectionProvider.CreateProtector(ProtectorPurpose);

    public async Task<EntraIdOptions> GetEffectiveAsync(CancellationToken cancellationToken = default)
    {
        if (cache.TryGetValue(SettingsCacheKeys.EntraSettings, out var cached) && cached is EntraIdOptions hit)
        {
            return hit;
        }

        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var stored = await dbContext.EntraSettings.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == EntraSettings.SingletonId, cancellationToken)
            ?? EntraSettings.CreateDefault();

        var effective = Merge(stored, Unprotect(stored.ClientSecretProtected, EntraFields.ClientSecret),
            Unprotect(stored.ScimBearerTokenProtected, EntraFields.ScimBearerToken));

        using var entry = cache.CreateEntry(SettingsCacheKeys.EntraSettings);
        entry.Value = effective;
        entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(30);
        return effective;
    }

    /// <remarks>
    /// The blocking wait is confined to a cache miss on the OpenID Connect options path, which the
    /// options infrastructure calls synchronously from a thread-pool thread with no synchronization
    /// context to deadlock against. Every other caller goes through <see cref="GetEffectiveAsync"/>.
    /// </remarks>
    public EntraIdOptions GetEffective() =>
        cache.TryGetValue(SettingsCacheKeys.EntraSettings, out var cached) && cached is EntraIdOptions hit
            ? hit
            : GetEffectiveAsync().GetAwaiter().GetResult();

    public async Task<EntraSettingsView> GetForAdminAsync(CancellationToken cancellationToken = default)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var stored = await dbContext.EntraSettings.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == EntraSettings.SingletonId, cancellationToken)
            ?? EntraSettings.CreateDefault();

        var effective = Merge(stored, Unprotect(stored.ClientSecretProtected, EntraFields.ClientSecret),
            Unprotect(stored.ScimBearerTokenProtected, EntraFields.ScimBearerToken));

        // The page shows the values that are actually in force, so a locked field displays what
        // configuration decided rather than the row underneath it that nothing reads.
        var settings = new EntraSettingsDto(
            effective.Enabled,
            effective.TenantId,
            effective.ClientId,
            effective.Authority,
            effective.CallbackPath,
            effective.SignedOutCallbackPath,
            effective.DisplayName,
            effective.LinkByVerifiedEmail,
            effective.AllowJustInTimeProvisioning,
            [.. effective.DefaultRoles],
            effective.Scim.Enabled,
            effective.Scim.BlockOnDeprovision);

        return new EntraSettingsView(
            settings,
            !string.IsNullOrEmpty(effective.ClientSecret),
            !string.IsNullOrEmpty(effective.Scim.BearerToken),
            LockedFields());
    }

    public async Task<AccountResult> UpdateAsync(
        EntraSettingsUpdate update,
        Guid actingUserId,
        CancellationToken cancellationToken = default)
    {
        var input = update.Settings;

        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var settings = await GetOrCreateAsync(dbContext, cancellationToken);

        // Validated against what the merge would actually produce, not against what was typed: a
        // tenant id pinned in configuration satisfies the "sign-in needs a tenant" rule even though
        // the field arrived blank because the page had it disabled.
        var candidate = EntraSettings.CreateDefault();
        candidate.UpdateSignIn(
            input.Enabled, input.TenantId, input.ClientId, input.Authority,
            input.CallbackPath, input.SignedOutCallbackPath, input.DisplayName,
            input.LinkByVerifiedEmail, input.AllowJustInTimeProvisioning, input.DefaultRoles);
        candidate.UpdateScim(input.ScimEnabled, input.ScimBlockOnDeprovision);

        var clientSecret = Resolve(update.ClientSecret, settings.ClientSecretProtected, EntraFields.ClientSecret);
        var scimToken = Resolve(update.ScimBearerToken, settings.ScimBearerTokenProtected, EntraFields.ScimBearerToken);
        var effective = Merge(candidate, clientSecret, scimToken);

        if (await ValidateAsync(effective, dbContext, cancellationToken) is { } failure)
        {
            return failure;
        }

        settings.UpdateSignIn(
            input.Enabled, input.TenantId, input.ClientId, input.Authority,
            input.CallbackPath, input.SignedOutCallbackPath, input.DisplayName,
            input.LinkByVerifiedEmail, input.AllowJustInTimeProvisioning, input.DefaultRoles);
        settings.UpdateScim(input.ScimEnabled, input.ScimBlockOnDeprovision);

        ApplySecret(update.ClientSecret, plain => settings.SetClientSecret(Protect(plain)));
        ApplySecret(update.ScimBearerToken, plain => settings.SetScimBearerToken(Protect(plain)));

        // Whether a secret exists is worth auditing; its value never is.
        var detail =
            $"enabled={effective.Enabled} tenant={effective.TenantId ?? "-"} clientId={effective.ClientId ?? "-"} " +
            $"secret={(string.IsNullOrEmpty(effective.ClientSecret) ? "unset" : "set")} " +
            $"jit={effective.AllowJustInTimeProvisioning} link={effective.LinkByVerifiedEmail} " +
            $"defaultRoles=[{string.Join(", ", effective.DefaultRoles)}] " +
            $"scim={effective.Scim.Enabled} scimToken={(string.IsNullOrEmpty(effective.Scim.BearerToken) ? "unset" : "set")}";

        dbContext.AccountAuditEvents.Add(new AccountAuditEvent(
            actingUserId, AccountAuditEventType.SettingsChanged, $"admin:{actingUserId}",
            $"EntraId: {detail}", timeProvider.GetUtcNow()));

        await dbContext.SaveChangesAsync(cancellationToken);
        cache.Remove(SettingsCacheKeys.EntraSettings);

        // Only after the row is committed and the snapshot evicted, so whatever the reloader reads
        // back is the configuration that was just saved.
        await reloader.ReloadAsync(effective, cancellationToken);

        logger.LogInformation("Entra ID settings changed by {AdminId}: {Detail}", actingUserId, detail);
        return AccountResult.Success;
    }

    // --- merge -------------------------------------------------------------------------------

    /// <summary>Configuration over stored row, field by field.</summary>
    private EntraIdOptions Merge(EntraSettings stored, string? storedClientSecret, string? storedScimToken)
    {
        var section = configuration.GetSection(EntraIdOptions.SectionName);

        return new EntraIdOptions
        {
            Enabled = Flag(section, EntraFields.Enabled) ?? stored.Enabled,
            TenantId = Text(section, EntraFields.TenantId) ?? stored.TenantId,
            ClientId = Text(section, EntraFields.ClientId) ?? stored.ClientId,
            ClientSecret = Text(section, EntraFields.ClientSecret) ?? storedClientSecret,
            Authority = Text(section, EntraFields.Authority) ?? stored.Authority,
            CallbackPath = Text(section, EntraFields.CallbackPath) ?? stored.CallbackPath,
            SignedOutCallbackPath = Text(section, EntraFields.SignedOutCallbackPath) ?? stored.SignedOutCallbackPath,
            DisplayName = Text(section, EntraFields.DisplayName) ?? stored.DisplayName,
            LinkByVerifiedEmail = Flag(section, EntraFields.LinkByVerifiedEmail) ?? stored.LinkByVerifiedEmail,
            AllowJustInTimeProvisioning = Flag(section, EntraFields.AllowJustInTimeProvisioning) ?? stored.AllowJustInTimeProvisioning,
            DefaultRoles = List(section, EntraFields.DefaultRoles) ?? [.. stored.DefaultRoles],
            Scim = new ScimOptions
            {
                Enabled = Flag(section, EntraFields.ScimEnabled) ?? stored.ScimEnabled,
                BearerToken = Text(section, EntraFields.ScimBearerToken) ?? storedScimToken,
                BlockOnDeprovision = Flag(section, EntraFields.ScimBlockOnDeprovision) ?? stored.ScimBlockOnDeprovision,
            },
        };
    }

    private IReadOnlySet<string> LockedFields()
    {
        var section = configuration.GetSection(EntraIdOptions.SectionName);
        var locked = new HashSet<string>(StringComparer.Ordinal);

        foreach (var field in EntraFields.All)
        {
            var isSet = field switch
            {
                EntraFields.DefaultRoles => List(section, field) is not null,
                EntraFields.Enabled or EntraFields.LinkByVerifiedEmail or EntraFields.AllowJustInTimeProvisioning
                    or EntraFields.ScimEnabled or EntraFields.ScimBlockOnDeprovision => Flag(section, field) is not null,
                _ => Text(section, field) is not null,
            };

            if (isSet)
            {
                locked.Add(field);
            }
        }

        return locked;
    }

    private static string? Text(IConfiguration section, string key) =>
        section[key] is { } value && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;

    private bool? Flag(IConfiguration section, string key)
    {
        var raw = section[key];
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        if (bool.TryParse(raw.Trim(), out var parsed))
        {
            return parsed;
        }

        // Present but unreadable: fall through to the stored value rather than guess, and say so —
        // an operator who set this expects it to take effect.
        logger.LogWarning("EntraId:{Key} is not a boolean ('{Value}'); the stored setting is used instead", key, raw);
        return null;
    }

    private static string[]? List(IConfiguration section, string key)
    {
        var values = section.GetSection(key).GetChildren()
            .Select(child => child.Value)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!.Trim())
            .ToArray();

        return values.Length == 0 ? null : values;
    }

    // --- validation --------------------------------------------------------------------------

    private async Task<AccountResult?> ValidateAsync(
        EntraIdOptions effective,
        D3ParkingDbContext dbContext,
        CancellationToken cancellationToken)
    {
        // Switching sign-in on without the two values that identify the app registration would give
        // a button that can only ever fail, so the switch is refused rather than the sign-in.
        if (effective.Enabled && (string.IsNullOrWhiteSpace(effective.TenantId) || string.IsNullOrWhiteSpace(effective.ClientId)))
        {
            return AccountResult.Failure(messages["Error_EntraIncomplete"]);
        }

        if (effective.Enabled && string.IsNullOrWhiteSpace(effective.ClientSecret))
        {
            return AccountResult.Failure(messages["Error_EntraSecretRequired"]);
        }

        if (!string.IsNullOrWhiteSpace(effective.Authority)
            && (!Uri.TryCreate(effective.Authority, UriKind.Absolute, out var authority)
                || authority.Scheme != Uri.UriSchemeHttps))
        {
            return AccountResult.Failure(messages["Error_EntraAuthority"]);
        }

        if (string.Equals(effective.CallbackPath, effective.SignedOutCallbackPath, StringComparison.OrdinalIgnoreCase))
        {
            return AccountResult.Failure(messages["Error_EntraCallbackClash"]);
        }

        // The SCIM bearer token is the entire boundary on the provisioning endpoints. Enabling them
        // without one would publish an unauthenticated write API over the user directory.
        if (effective.Scim.Enabled && string.IsNullOrWhiteSpace(effective.Scim.BearerToken))
        {
            return AccountResult.Failure(messages["Error_EntraScimTokenRequired"]);
        }

        if (effective.Scim.Enabled && effective.Scim.BearerToken!.Trim().Length < MinimumScimTokenLength)
        {
            return AccountResult.Failure(messages["Error_EntraScimTokenWeak", MinimumScimTokenLength]);
        }

        foreach (var role in effective.DefaultRoles)
        {
            // Default roles are handed to accounts the directory creates on first sign-in. Pointing
            // one at Administrator would turn "anyone in the tenant" into an administrator here —
            // the same escalation the site-wide default role refuses.
            if (string.Equals(role, Roles.Administrator, StringComparison.OrdinalIgnoreCase))
            {
                return AccountResult.Failure(messages["Error_DefaultRoleReserved"]);
            }

            if (!await dbContext.Roles.AnyAsync(r => r.Name == role, cancellationToken))
            {
                return AccountResult.Failure(messages["Error_UnknownRole", role]);
            }
        }

        return null;
    }

    /// <summary>
    /// Short enough to be typed, long enough that guessing it is not a strategy. Entra generates
    /// far longer tokens; this only catches a hand-written placeholder.
    /// </summary>
    private const int MinimumScimTokenLength = 32;

    // --- secrets -----------------------------------------------------------------------------

    /// <summary>The plaintext a save should end up with, before it is encrypted again.</summary>
    private string? Resolve(EntraSecretUpdate update, string? storedProtected, string field) => update.Action switch
    {
        EntraSecretAction.Set => update.Value,
        EntraSecretAction.Clear => null,
        _ => Unprotect(storedProtected, field),
    };

    private static void ApplySecret(EntraSecretUpdate update, Action<string?> apply)
    {
        switch (update.Action)
        {
            case EntraSecretAction.Set:
                apply(update.Value);
                break;
            case EntraSecretAction.Clear:
                apply(null);
                break;
        }
    }

    private string? Protect(string? plaintext) =>
        string.IsNullOrWhiteSpace(plaintext) ? null : _protector.Protect(plaintext.Trim());

    private string? Unprotect(string? value, string field)
    {
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        try
        {
            return _protector.Unprotect(value);
        }
        catch (System.Security.Cryptography.CryptographicException ex)
        {
            // The data protection key ring that encrypted this is gone — a rebuilt container with
            // ephemeral keys, or a restore onto a different machine. Treated as "no secret" so the
            // application still starts; the administrator re-enters it on the settings page.
            logger.LogError(ex, "The stored Entra ID {Field} could not be decrypted and is being ignored; re-enter it in the settings", field);
            return null;
        }
    }

    private static async Task<EntraSettings> GetOrCreateAsync(D3ParkingDbContext dbContext, CancellationToken cancellationToken)
    {
        var settings = await dbContext.EntraSettings
            .FirstOrDefaultAsync(s => s.Id == EntraSettings.SingletonId, cancellationToken);

        if (settings is not null)
        {
            return settings;
        }

        settings = EntraSettings.CreateDefault();
        dbContext.EntraSettings.Add(settings);

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            return settings;
        }
        catch (DbUpdateException)
        {
            // A concurrent request created the singleton first; discard our insert and reload it.
            dbContext.Entry(settings).State = EntityState.Detached;
            return await dbContext.EntraSettings.FirstAsync(s => s.Id == EntraSettings.SingletonId, cancellationToken);
        }
    }
}
