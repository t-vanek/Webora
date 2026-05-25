namespace Webora.Application.Settings;

/// <summary>
/// Cache keys for the runtime-read site settings. Shared so the readers (middleware, culture
/// provider) and the writer (the settings service, which evicts them on save) stay in sync.
/// </summary>
public static class SettingsCacheKeys
{
    public const string DomainPolicy = "webora:domain-policy";
    public const string DefaultLanguage = "webora:default-language";
    public const string PageCharset = "webora:page-charset";

    public static readonly IReadOnlyList<string> All = [DomainPolicy, DefaultLanguage, PageCharset];
}
