using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using D3Parking.Application.Accounts;
using D3Parking.Application.Settings;
using D3Parking.Domain.Accounts;
using D3Parking.Domain.Settings;
using D3Parking.Infrastructure.Persistence;

namespace D3Parking.Infrastructure.Settings;

public sealed class SiteSettingsService(
    IDbContextFactory<D3ParkingDbContext> dbContextFactory,
    IMemoryCache cache,
    IStringLocalizer<AccountMessages> messages,
    TimeProvider timeProvider,
    ILogger<SiteSettingsService> logger) : ISiteSettingsService
{
    private const string DefaultCharset = "utf-8";

    public async Task<SiteSettingsDto> GetAsync(CancellationToken cancellationToken = default)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        return ToDto(await GetOrCreateAsync(dbContext, cancellationToken));
    }

    public async Task<string?> GetCanonicalBaseUrlAsync(CancellationToken cancellationToken = default)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        return (await GetOrCreateAsync(dbContext, cancellationToken)).BaseUrl;
    }

    public async Task<DomainPolicy> GetDomainPolicyAsync(CancellationToken cancellationToken = default)
    {
        // A read-only path for the hot middleware: never creates or tracks a row.
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var s = await dbContext.SiteSettings.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == SiteSettings.SingletonId, cancellationToken)
            ?? SiteSettings.CreateDefault();

        return new DomainPolicy(
            s.CanonicalHost, s.Port, s.ForceHttps, s.HstsEnabled,
            s.HstsMaxAgeDays, s.HstsIncludeSubDomains, s.HstsPreload, s.WwwPreference, s.Aliases,
            s.LowercaseUrls, s.TrailingSlash);
    }

    public async Task<AccountResult> UpdateDomainAsync(DomainSettingsDto domain, Guid actingUserId, CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(domain.CanonicalHost) && !IsValidHost(domain.CanonicalHost))
        {
            return AccountResult.Failure(messages["Error_InvalidHost", domain.CanonicalHost]);
        }

        foreach (var alias in domain.Aliases)
        {
            if (!string.IsNullOrWhiteSpace(alias) && !IsValidHost(alias))
            {
                return AccountResult.Failure(messages["Error_InvalidHost", alias]);
            }
        }

        if (domain.Port is { } port && port is < 1 or > 65535)
        {
            return AccountResult.Failure(messages["Error_InvalidPort"]);
        }

        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var settings = await GetOrCreateAsync(dbContext, cancellationToken);
        settings.UpdateDomain(
            domain.CanonicalHost, domain.Scheme, domain.Port, domain.ForceHttps,
            domain.HstsEnabled, domain.HstsMaxAgeDays, domain.HstsIncludeSubDomains,
            domain.HstsPreload, domain.WwwPreference, domain.Aliases);

        var detail = $"host={settings.CanonicalHost ?? "-"} scheme={settings.Scheme} port={settings.Port?.ToString() ?? "-"} " +
            $"forceHttps={settings.ForceHttps} hsts={settings.HstsEnabled} www={settings.WwwPreference} aliases={settings.Aliases.Count}";
        await FinalizeAsync(dbContext, "Domain", detail, actingUserId, cancellationToken);
        return AccountResult.Success;
    }

    public async Task<AccountResult> UpdateRegionalAsync(RegionalSettingsDto regional, Guid actingUserId, CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(regional.DefaultTimeZoneId) &&
            !TimeZoneInfo.TryFindSystemTimeZoneById(regional.DefaultTimeZoneId, out _))
        {
            return AccountResult.Failure(messages["Error_InvalidTimeZone", regional.DefaultTimeZoneId]);
        }

        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var settings = await GetOrCreateAsync(dbContext, cancellationToken);
        settings.UpdateRegional(regional.DefaultLanguage, regional.DefaultTimeZoneId);

        var detail = $"lang={settings.DefaultLanguage ?? "-"} tz={settings.DefaultTimeZoneId ?? "-"}";
        await FinalizeAsync(dbContext, "Region", detail, actingUserId, cancellationToken);
        return AccountResult.Success;
    }

    public async Task<AccountResult> UpdateEncodingAsync(EncodingSettingsDto encoding, Guid actingUserId, CancellationToken cancellationToken = default)
    {
        if (InvalidCharset(encoding.PageCharset) is { } badPage)
        {
            return AccountResult.Failure(messages["Error_InvalidCharset", badPage]);
        }

        if (InvalidCharset(encoding.EmailCharset) is { } badEmail)
        {
            return AccountResult.Failure(messages["Error_InvalidCharset", badEmail]);
        }

        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var settings = await GetOrCreateAsync(dbContext, cancellationToken);
        settings.UpdateEncoding(encoding.PageCharset, encoding.EmailCharset);

        var detail = $"page={settings.PageCharset ?? DefaultCharset} email={settings.EmailCharset ?? DefaultCharset}";
        await FinalizeAsync(dbContext, "Encoding", detail, actingUserId, cancellationToken);
        return AccountResult.Success;
    }

    public async Task<AccountResult> UpdateAccountsAsync(AccountsSettingsDto accounts, Guid actingUserId, CancellationToken cancellationToken = default)
    {
        // The default role is handed to every self-registered account the moment it activates —
        // pointing it at Administrator would silently make public registration a privilege
        // escalation, so the built-in admin role is refused outright.
        if (string.Equals(accounts.DefaultRole, Domain.Authorization.Roles.Administrator, StringComparison.OrdinalIgnoreCase))
        {
            return AccountResult.Failure(messages["Error_DefaultRoleReserved"]);
        }

        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        if (!string.IsNullOrWhiteSpace(accounts.DefaultRole) &&
            !await dbContext.Roles.AnyAsync(r => r.Name == accounts.DefaultRole, cancellationToken))
        {
            return AccountResult.Failure(messages["Error_UnknownRole", accounts.DefaultRole]);
        }

        var settings = await GetOrCreateAsync(dbContext, cancellationToken);
        settings.UpdateAccounts(accounts.DefaultRole);

        await FinalizeAsync(dbContext, "Accounts", $"defaultRole={settings.DefaultRole ?? "-"}", actingUserId, cancellationToken);
        return AccountResult.Success;
    }

    public async Task<string?> GetDefaultRoleAsync(CancellationToken cancellationToken = default)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        return (await GetOrCreateAsync(dbContext, cancellationToken)).DefaultRole;
    }

    public async Task<AccountResult> UpdateGeneralAsync(GeneralSettingsDto general, Guid actingUserId, CancellationToken cancellationToken = default)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var settings = await GetOrCreateAsync(dbContext, cancellationToken);
        settings.UpdateGeneral(general.SiteName, general.SiteDescription, general.LowercaseUrls, general.TrailingSlash);

        var detail = $"name={settings.SiteName ?? "-"} lowercaseUrls={settings.LowercaseUrls} trailingSlash={settings.TrailingSlash}";
        await FinalizeAsync(dbContext, "General", detail, actingUserId, cancellationToken);
        return AccountResult.Success;
    }

    public async Task<SiteIdentityDto> GetIdentityAsync(CancellationToken cancellationToken = default)
    {
        if (cache.TryGetValue(SettingsCacheKeys.Identity, out var cached) && cached is SiteIdentityDto identity)
        {
            return identity;
        }

        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var s = await GetOrCreateAsync(dbContext, cancellationToken);
        identity = new SiteIdentityDto(s.SiteName, s.SiteDescription);

        using var entry = cache.CreateEntry(SettingsCacheKeys.Identity);
        entry.Value = identity;
        entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(30);

        return identity;
    }

    public async Task<string> GetPageCharsetAsync(CancellationToken cancellationToken = default)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        return (await GetOrCreateAsync(dbContext, cancellationToken)).PageCharset ?? DefaultCharset;
    }

    public async Task<string> GetEmailCharsetAsync(CancellationToken cancellationToken = default)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        return (await GetOrCreateAsync(dbContext, cancellationToken)).EmailCharset ?? DefaultCharset;
    }

    public async Task<string?> GetDefaultLanguageAsync(CancellationToken cancellationToken = default)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        return (await GetOrCreateAsync(dbContext, cancellationToken)).DefaultLanguage;
    }

    public async Task<TimeZoneInfo> GetTimeZoneAsync(CancellationToken cancellationToken = default)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var settings = await GetOrCreateAsync(dbContext, cancellationToken);
        return !string.IsNullOrEmpty(settings.DefaultTimeZoneId)
            && TimeZoneInfo.TryFindSystemTimeZoneById(settings.DefaultTimeZoneId, out var tz)
                ? tz
                : TimeZoneInfo.Local;
    }

    /// <summary>Persists the change with an audit record and evicts the runtime-read caches.</summary>
    private async Task FinalizeAsync(D3ParkingDbContext dbContext, string section, string detail, Guid actingUserId, CancellationToken cancellationToken)
    {
        dbContext.AccountAuditEvents.Add(new AccountAuditEvent(
            actingUserId, AccountAuditEventType.SettingsChanged, $"admin:{actingUserId}", $"{section}: {detail}", timeProvider.GetUtcNow()));

        await dbContext.SaveChangesAsync(cancellationToken);

        foreach (var key in SettingsCacheKeys.All)
        {
            cache.Remove(key);
        }

        logger.LogInformation("Site settings ({Section}) changed by {AdminId}: {Detail}", section, actingUserId, detail);
    }

    private static async Task<SiteSettings> GetOrCreateAsync(D3ParkingDbContext dbContext, CancellationToken cancellationToken)
    {
        var settings = await dbContext.SiteSettings
            .FirstOrDefaultAsync(s => s.Id == SiteSettings.SingletonId, cancellationToken);

        if (settings is not null)
        {
            return settings;
        }

        settings = SiteSettings.CreateDefault();
        dbContext.SiteSettings.Add(settings);

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            return settings;
        }
        catch (DbUpdateException)
        {
            // A concurrent request created the singleton first; discard our insert and reload it.
            dbContext.Entry(settings).State = EntityState.Detached;
            return await dbContext.SiteSettings.FirstAsync(s => s.Id == SiteSettings.SingletonId, cancellationToken);
        }
    }

    private static SiteSettingsDto ToDto(SiteSettings s) =>
        new(
            new DomainSettingsDto(
                s.CanonicalHost, s.Scheme, s.Port, s.ForceHttps, s.HstsEnabled,
                s.HstsMaxAgeDays, s.HstsIncludeSubDomains, s.HstsPreload, s.WwwPreference, s.Aliases),
            new RegionalSettingsDto(s.DefaultLanguage, s.DefaultTimeZoneId),
            new EncodingSettingsDto(s.PageCharset, s.EmailCharset),
            new AccountsSettingsDto(s.DefaultRole),
            new GeneralSettingsDto(s.SiteName, s.SiteDescription, s.LowercaseUrls, s.TrailingSlash),
            s.BaseUrl);

    private static string? InvalidCharset(string? charset)
    {
        if (string.IsNullOrWhiteSpace(charset))
        {
            return null;
        }

        try
        {
            _ = Encoding.GetEncoding(charset.Trim());
            return null;
        }
        catch (ArgumentException)
        {
            return charset.Trim();
        }
    }

    private static bool IsValidHost(string value)
    {
        var host = value.Trim();
        var schemeIndex = host.IndexOf("://", StringComparison.Ordinal);
        if (schemeIndex >= 0)
        {
            host = host[(schemeIndex + 3)..];
        }

        host = host.Trim().Trim('/');
        return host.Length > 0 && Uri.CheckHostName(host) != UriHostNameType.Unknown;
    }
}
