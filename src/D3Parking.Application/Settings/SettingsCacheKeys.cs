namespace D3Parking.Application.Settings;

/// <summary>
/// Cache keys for the runtime-read site settings. Shared so the readers (middleware, culture
/// provider) and the writer (the settings service, which evicts them on save) stay in sync.
/// </summary>
public static class SettingsCacheKeys
{
    public const string DomainPolicy = "d3parking:domain-policy";
    public const string DefaultLanguage = "d3parking:default-language";
    public const string PageCharset = "d3parking:page-charset";
    public const string Identity = "d3parking:site-identity";

    /// <summary>
    /// The merged Entra ID settings. Deliberately outside <see cref="All"/>: it is written by a
    /// different service and evicted on its own save, so a domain-name edit has no business
    /// throwing away a snapshot the sign-in path is reading.
    /// </summary>
    public const string EntraSettings = "d3parking:entra-settings";

    public static readonly IReadOnlyList<string> All = [DomainPolicy, DefaultLanguage, PageCharset, Identity];
}
