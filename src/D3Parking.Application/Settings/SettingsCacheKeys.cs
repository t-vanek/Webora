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

    public static readonly IReadOnlyList<string> All = [DomainPolicy, DefaultLanguage, PageCharset, Identity];
}
