using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using Webora.Application.Accounts;
using Webora.Application.Settings;
using Webora.Domain.Settings;
using Webora.Infrastructure.Persistence;

namespace Webora.Infrastructure.Settings;

public sealed class SiteSettingsService(
    WeboraDbContext dbContext,
    IStringLocalizer<AccountMessages> messages) : ISiteSettingsService
{
    public async Task<SiteSettingsDto> GetAsync(CancellationToken cancellationToken = default) =>
        ToDto(await GetOrCreateAsync(cancellationToken));

    public async Task<string?> GetCanonicalBaseUrlAsync(CancellationToken cancellationToken = default) =>
        (await GetOrCreateAsync(cancellationToken)).BaseUrl;

    public async Task<DomainPolicy> GetDomainPolicyAsync(CancellationToken cancellationToken = default)
    {
        // A read-only path for the hot middleware: never creates or tracks a row.
        var s = await dbContext.SiteSettings.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == SiteSettings.SingletonId, cancellationToken)
            ?? SiteSettings.CreateDefault();

        return new DomainPolicy(
            s.CanonicalHost, s.Port, s.ForceHttps, s.HstsEnabled,
            s.HstsMaxAgeDays, s.HstsIncludeSubDomains, s.WwwPreference, s.Aliases);
    }

    public async Task<AccountResult> UpdateDomainAsync(DomainSettingsDto domain, CancellationToken cancellationToken = default)
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

        var settings = await GetOrCreateAsync(cancellationToken);
        settings.UpdateDomain(
            domain.CanonicalHost, domain.Scheme, domain.Port, domain.ForceHttps,
            domain.HstsEnabled, domain.HstsMaxAgeDays, domain.HstsIncludeSubDomains,
            domain.WwwPreference, domain.Aliases);

        await dbContext.SaveChangesAsync(cancellationToken);
        return AccountResult.Success;
    }

    private async Task<SiteSettings> GetOrCreateAsync(CancellationToken cancellationToken)
    {
        var settings = await dbContext.SiteSettings
            .FirstOrDefaultAsync(s => s.Id == SiteSettings.SingletonId, cancellationToken);

        if (settings is null)
        {
            settings = SiteSettings.CreateDefault();
            dbContext.SiteSettings.Add(settings);
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        return settings;
    }

    private static SiteSettingsDto ToDto(SiteSettings s) =>
        new(
            new DomainSettingsDto(
                s.CanonicalHost, s.Scheme, s.Port, s.ForceHttps, s.HstsEnabled,
                s.HstsMaxAgeDays, s.HstsIncludeSubDomains, s.WwwPreference, s.Aliases),
            s.BaseUrl);

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
